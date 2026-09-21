using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Reporting;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// API-callable deserialize. Without <see cref="Scope"/> it deserializes every entry of the
/// <see cref="Mode"/> manifest (replace by default). With a scope it deserializes only the
/// entries, or the part of an entry, inside that predicate-shaped subtree or table, after the
/// boundary check against the configuration (<see cref="InlineScopeResolver"/>).
/// Each document runs with the mode in its own <c>ownership</c> header; documents without one run with
/// <see cref="Mode"/> (replace = source-wins, merge = destination-wins field fill).
///
/// Use via DW CLI: dw command Deserialize [mode=merge]
/// Or via Management API: POST /Admin/Api/Deserialize?mode=merge
///   body {"Mode":"merge","Scope":{"table":"EcomProducts"}}
/// </summary>
public class DeserializeCommand : CommandBase
{
    /// <summary>Serializer mode: "replace" (default) or "merge". Case-insensitive.</summary>
    public string Mode { get; set; } = "replace";

    /// <summary>
    /// Optional inline scope (predicate-shaped). Null deserializes the whole mode manifest.
    /// Also honored via query string: ?scope={json}.
    /// </summary>
    public InlineScope? Scope { get; set; }

    /// <summary>
    /// Phase 37-04 STRICT-01: optional strict-mode override. Null = use config.StrictMode,
    /// which itself falls back to the entry-point default (API/CLI default = true, per D-16).
    /// Explicit true/false overrides both.
    /// </summary>
    public bool? StrictMode { get; set; }

    /// <summary>
    /// Engine issue #5: quarantine the unresolvable-link warning class instead of failing the
    /// pass on it. One orphan link used to abort a run that had written hundreds of clean rows;
    /// with this on, such a link is reported as a QUARANTINED end-of-run block (and on
    /// <see cref="OrchestratorResult.QuarantinedWarnings"/>) while every other warning class
    /// still fails the run. Default false — a "strict" gate stays strict unless asked.
    /// Also honored via query string: ?quarantineUnresolvableLinks=true.
    /// </summary>
    public bool QuarantineUnresolvableLinks { get; set; }

    /// <summary>
    /// Phase 37-04: internal flag set by admin-UI action buttons to flip the entry-point
    /// default to AdminUi (lenient). API/CLI callers leave this false so they get the
    /// default-strict behavior. Not serialised to Management API.
    /// </summary>
    public bool IsAdminUiInvocation { get; set; } = false;

    /// <summary>
    /// Dry-run preview: the full deserialize pipeline runs and reports what WOULD be
    /// created/updated/skipped (with per-field [DRY-RUN] detail in the log), but nothing
    /// is written. Also honored via query string: ?dryRun=true.
    /// </summary>
    public bool IsDryRun { get; set; }

    /// <summary>Route name of a deprecated alias subclass; null on this command.</summary>
    internal virtual string? DeprecatedRoute => null;

    private string? _logFile;
    private readonly List<string> _logLines = new();

    private void Log(string message)
    {
        _logLines.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }

    private void FlushLog(string logFile, LogFileSummary summary)
    {
        LogFileWriter.WriteSummaryHeader(logFile, summary);
        foreach (var line in _logLines)
            File.AppendAllText(logFile, line + "\n");
    }

    public override CommandResult Handle() =>
        DeprecatedCommandAlias.Decorate(HandleCore(), DeprecatedRoute, "Deserialize");

    private CommandResult HandleCore()
    {
        // Parse mode string strictly before any path interpolation.
        if (!Enum.TryParse<SerializerMode>(Mode?.Trim(), ignoreCase: true, out var serializerMode))
        {
            return new()
            {
                Status = CommandResult.ResultType.Invalid,
                Message = $"Invalid mode '{Mode}'. Expected 'replace' or 'merge' (case-insensitive)."
            };
        }

        // D-38-11: DW CommandBase does not bind query params by default for POST.
        // Fallback: if Mode stayed at the "replace" default, check the query string.
        // UNCONDITIONAL per D-38-11 + checker blocker B4 — no curl-probe escape hatch.
        if (string.Equals(Mode, "replace", StringComparison.OrdinalIgnoreCase))
        {
            var fromQuery = Dynamicweb.Context.Current?.Request?["mode"];
            if (!string.IsNullOrEmpty(fromQuery))
            {
                Mode = fromQuery;
                if (!Enum.TryParse<SerializerMode>(Mode?.Trim(), ignoreCase: true, out serializerMode))
                {
                    return new()
                    {
                        Status = CommandResult.ResultType.Invalid,
                        Message = $"Invalid mode '{Mode}'. Expected 'replace' or 'merge' (case-insensitive)."
                    };
                }
            }
        }

        // D-38-11 (extension): honor ?strictMode=true|false if supplied via query string.
        // Only applies when StrictMode is still null (not overridden by the JSON body).
        if (StrictMode is null)
        {
            var strictFromQuery = Dynamicweb.Context.Current?.Request?["strictMode"];
            if (!string.IsNullOrEmpty(strictFromQuery) && bool.TryParse(strictFromQuery, out var strictQ))
            {
                StrictMode = strictQ;
            }
        }

        // Same query-string fallback for the issue-#5 quarantine knob
        // (?quarantineUnresolvableLinks=true), D-38-11 precedent.
        if (!QuarantineUnresolvableLinks)
        {
            var quarantineFromQuery = Dynamicweb.Context.Current?.Request?["quarantineUnresolvableLinks"];
            if (!string.IsNullOrEmpty(quarantineFromQuery) && bool.TryParse(quarantineFromQuery, out var quarantineQ))
            {
                QuarantineUnresolvableLinks = quarantineQ;
            }
        }

        // Same query-string fallback for dry-run (?dryRun=true), D-38-11 precedent.
        if (!IsDryRun)
        {
            var dryFromQuery = Dynamicweb.Context.Current?.Request?["dryRun"];
            if (!string.IsNullOrEmpty(dryFromQuery) && bool.TryParse(dryFromQuery, out var dryQ))
            {
                IsDryRun = dryQ;
            }
        }

        var (scope, invalidScope) = InlineScopeRequest.Read(Scope);
        if (invalidScope is not null)
            return invalidScope;

        try
        {
            var configPath = ConfigPathResolver.FindConfigFile();
            if (configPath == null)
                return new() { Status = CommandResult.ResultType.Error, Message = "Serializer.config.json not found" };

            // Phase 43 / DESER-04: a full deserialize is config-free — the orchestrator reads the
            // manifest from disk and dispatches per-entry. An inline scope loads the config
            // because the configuration is the boundary the scope must fall inside.
            InlineScopeResolution? resolution = null;
            if (scope is not null)
            {
                var config = ConfigLoader.Load(configPath);
                resolution = InlineScopeResolver.Resolve(scope, serializerMode, config,
                    InlineScopeRequest.IdentifierValidator(), forDeserialize: true, InlineScopeRequest.ResolvePage);
                if (!resolution.IsValid)
                    return InlineScopeRequest.Rejected(scope, resolution);
            }

            // Mode subfolder is the lowercased mode name (Phase 42 ManifestWriter convention).
            var filesRoot = ConfigPathResolver.GetFilesRoot(configPath);
            var systemDir = Path.Combine(filesRoot, "System");
            var paths = SerializerPathResolver.EnsureDirectories(systemDir);

            // Mode subfolder is the lowercased mode name ("replace" / "merge").
            var modeName = serializerMode.ToString().ToLowerInvariant();
            var modeRoot = Path.Combine(paths.SerializeRoot, modeName);

            // Conflict strategy is hardcoded per mode (Replace=SourceWins, Merge=DestinationWins).
            // It is the fallback for documents that carry no ownership header.
            var modeStrategy = DefaultConflictStrategyForMode(serializerMode);

            // Phase 44 / WR-04: _logFile is created BEFORE the inner try region so the
            // outer-most catch can flush accumulated lines even if a downstream call throws
            // before reaching the inner try/finally.
            _logFile = LogFileWriter.CreateLogFile(paths.Log, "Deserialize",
                IsDryRun ? $"{modeName}-dryrun" : modeName);
            Log($"=== Serializer Deserialize (API) started [mode: {serializerMode}{(IsDryRun ? " | DRY RUN" : "")}] ===");
            if (DeprecatedRoute is not null)
                Log($"DEPRECATED: {DeprecatedCommandAlias.Notice(DeprecatedRoute, "Deserialize")}");

            try
            {
                if (!Directory.Exists(modeRoot))
                    return new() { Status = CommandResult.ResultType.Error, Message = $"Mode subfolder not found: {modeRoot}" };

                var yamlCount = Directory.GetFiles(modeRoot, "*.yml", SearchOption.AllDirectories).Length;
                if (yamlCount == 0)
                    return new() { Status = CommandResult.ResultType.Error, Message = $"{modeRoot} contains no YAML files" };

                // Phase 43 / DESER-05: strict-mode default sourced from entry-point + per-call request
                // override. The configValue: null literal is grep-friendly per CONTEXT line 52 — the
                // config.StrictMode setting is no longer consulted on the deserialize path. A one-time
                // WARNING fires (Task 7) when the legacy setting is still on disk.
                var entryPoint = IsAdminUiInvocation
                    ? StrictModeResolver.EntryPoint.AdminUi
                    : StrictModeResolver.EntryPoint.Api;
                var strict = StrictModeResolver.Resolve(entryPoint, configValue: null, requestValue: StrictMode);
                Log($"=== Strict mode: {strict} (entry-point: {entryPoint}) ===");

                // Engine issue #5: opt-in per-class quarantine. Empty set = pre-issue-#5
                // behavior (every warning class fails the run).
                var quarantinedClasses = QuarantineUnresolvableLinks
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StrictModeWarningClass.UnresolvableLink }
                    : null;
                if (QuarantineUnresolvableLinks)
                    Log($"=== Quarantined warning classes: {StrictModeWarningClass.UnresolvableLink} ===");

                var escalator = new StrictModeEscalator(strict, Log, quarantinedClasses);

                // Phase 43 / DESER-01: orchestrator reads the manifest itself; no predicates parameter.
                // The envelope's by-ItemType exclusion maps are consulted by the orchestrator (per
                // MANIFEST-05), so the caller no longer threads them in.
                var orchestrator = ProviderRegistry.CreateOrchestrator(filesRoot);
                OrchestratorResult result;
                if (resolution is not null)
                {
                    Log($"=== Inline scope: {scope} -> '{resolution.Predicate!.Name}' inside configured predicate '{resolution.Fence!.Name}' ===");
                    result = orchestrator.DeserializeScope(
                        resolution.Predicate,
                        modeRoot,
                        serializerMode,
                        modeStrategy,
                        Log,
                        isDryRun: IsDryRun,
                        escalator: escalator);
                }
                else
                {
                    result = orchestrator.DeserializeAll(
                        modeRoot,
                        serializerMode,
                        modeStrategy,
                        Log,
                        isDryRun: IsDryRun,
                        providerFilter: null,
                        escalator: escalator);
                }

                // Build summary with advice and flush log. Phase 43 / REPORT-03: drive off
                // result.EntryOutcomes (canonical) — Phase 44 / IN-01 deleted DeserializeResults.
                var advice = AdviceGenerator.GenerateAdvice(result);
                var summary = new LogFileSummary
                {
                    Operation = "Deserialize",
                    Mode = modeName,
                    DryRun = IsDryRun,
                    Timestamp = DateTime.UtcNow,
                    Predicates = result.EntryOutcomes
                        .Where(o => o.EntryId != EntryOutcome.RunLevelEntryId)  // Phase 44 / IN-06
                        .Select(o => new PredicateSummary
                        {
                            Name = o.EntryId,
                            Table = o.EntryId,
                            Created = o.Counts.Created,
                            Updated = o.Counts.Updated,
                            Skipped = o.Counts.Skipped,
                            Failed = o.Counts.Failed,
                            Deleted = o.Counts.Deleted,
                            Errors = o.Errors.ToList(),
                            Warnings = o.Warnings.ToList()
                        }).ToList(),
                    TotalCreated = result.EntryOutcomes.Sum(o => o.Counts.Created),
                    TotalUpdated = result.EntryOutcomes.Sum(o => o.Counts.Updated),
                    TotalSkipped = result.EntryOutcomes.Sum(o => o.Counts.Skipped),
                    TotalFailed = result.EntryOutcomes.Sum(o => o.Counts.Failed),
                    TotalDeleted = result.EntryOutcomes.Sum(o => o.Counts.Deleted),
                    Errors = result.Errors.ToList(),
                    Advice = advice
                };
                FlushLog(_logFile, summary);

                var label = resolution is not null ? $"{serializerMode}, scope: {scope}" : serializerMode.ToString();
                var message = BuildMessage(result, label, IsDryRun, Path.GetFileName(_logFile));

                // D-38-12: HTTP status is driven by result.HasErrors. Zero-error result maps to Ok
                // regardless of Message content. Phase 43 / REPORT-04 / SC-3: HasErrors now aggregates
                // from EntryOutcomes (any EntryStatus.Failed → true). Test seam at InvokeMapStatusForTest.
                // Engine issue #10: the response also carries the walked entries by name.
                return MapStatusFromResult(result, message, BuildResultModel(result, modeName, IsDryRun));
            }
            catch (Exception ex)
            {
                // Phase 44 / WR-04: flush accumulated log lines on the way out so the
                // deprecation WARNING + any other in-flight diagnostics survive the
                // exception path. Without this, an orchestrator throw discarded every
                // log line emitted between _logFile creation and the throw.
                Log($"ERROR: Deserialization failed: {ex.Message}");
                try
                {
                    var failSummary = new LogFileSummary
                    {
                        Operation = "Deserialize",
                        Mode = modeName,
                        DryRun = IsDryRun,
                        Timestamp = DateTime.UtcNow,
                        Predicates = new List<PredicateSummary>(),
                        Errors = new List<string> { ex.Message }
                    };
                    FlushLog(_logFile, failSummary);
                }
                catch { /* best effort — flush failure must not mask the original exception */ }
                return new() { Status = CommandResult.ResultType.Error, Message = $"Deserialization failed: {ex.Message}" };
            }
        }
        catch (Exception ex)
        {
            // Outer catch — pre-_logFile-creation failure (configPath not found, etc.).
            // No log to flush.
            return new() { Status = CommandResult.ResultType.Error, Message = $"Deserialization failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Per-mode default conflict strategy on the deserialize path (config is not consulted).
    /// Hardcoded per mode: Replace=SourceWins, Merge=DestinationWins.
    /// </summary>
    private static ConflictStrategy DefaultConflictStrategyForMode(SerializerMode mode) =>
        mode == SerializerMode.Merge ? ConflictStrategy.DestinationWins : ConflictStrategy.SourceWins;

    /// <summary>
    /// D-38-12 test seam: exposes the status-mapping branch of <see cref="Handle"/> so
    /// <c>DeserializeCommandTests.Handle_ZeroErrors_SynthOrchestratorResult_ReturnsOk</c>
    /// can assert the zero-error == Ok invariant unconditionally against a synthetic
    /// <see cref="OrchestratorResult"/>, without running the full deserialize pipeline.
    /// </summary>
    internal static CommandResult InvokeMapStatusForTest(OrchestratorResult result)
        => MapStatusFromResult(result, result.Summary ?? string.Empty, null);

    /// <summary>
    /// D-38-12: HTTP status driven by <see cref="OrchestratorResult.HasErrors"/>.
    /// Zero-error result == Ok. Pure function; no side effects.
    /// </summary>
    private static CommandResult MapStatusFromResult(OrchestratorResult result, string message, object? model)
    {
        return new CommandResult
        {
            Status = result.HasErrors ? CommandResult.ResultType.Error : CommandResult.ResultType.Ok,
            Message = message,
            Model = model
        };
    }

    /// <summary>
    /// Engine issue #11: the response message's "Errors:" tail is built from
    /// <see cref="OrchestratorResult.AllErrors"/> — run-level errors AND each failed entry's own
    /// error strings. The per-entry strings used to reach only the log file, so a run reporting
    /// "1 failed" answered with an empty "Errors: " list and nothing named the reason.
    /// </summary>
    internal static string BuildMessage(OrchestratorResult result, string label, bool isDryRun, string? logFileName)
    {
        var message = isDryRun
            ? $"[{label} DRY RUN — nothing was written] {result.Summary} " +
              $"Per-item [DRY-RUN] detail: Log Viewer > {logFileName}."
            : $"[{label}] {result.Summary}";

        var allErrors = result.AllErrors;
        if (result.HasErrors && allErrors.Count > 0)
            message += $" Errors: {string.Join("; ", allErrors)}";

        if (result.QuarantinedWarnings.Count > 0)
            message += " Quarantined (reported, did not fail the run): " +
                       string.Join("; ", result.QuarantinedWarnings);

        return message;
    }

    /// <summary>
    /// Engine issue #10: build the structured response payload — the walked manifest entries
    /// by name with their own counts, so a manifest/engine disagreement is visible in the
    /// response rather than inferrable from a total.
    /// </summary>
    internal static DeserializeResultModel BuildResultModel(OrchestratorResult result, string mode, bool isDryRun)
    {
        var outcomes = result.ManifestEntryOutcomes;

        return new DeserializeResultModel
        {
            Mode = mode,
            DryRun = isDryRun,
            EntryCount = outcomes.Count,
            TotalCreated = outcomes.Sum(o => o.Counts.Created),
            TotalUpdated = outcomes.Sum(o => o.Counts.Updated),
            TotalSkipped = outcomes.Sum(o => o.Counts.Skipped),
            TotalFailed = outcomes.Sum(o => o.Counts.Failed),
            Entries = outcomes.Select(o => new DeserializeEntryModel
            {
                EntryId = o.EntryId,
                ProviderType = o.ProviderType,
                Status = o.Status.ToString(),
                Created = o.Counts.Created,
                Updated = o.Counts.Updated,
                Skipped = o.Counts.Skipped,
                Failed = o.Counts.Failed,
                Errors = o.Errors.ToList()
            }).ToList(),
            Errors = result.AllErrors.ToList(),
            QuarantinedWarnings = result.QuarantinedWarnings.ToList()
        };
    }
}
