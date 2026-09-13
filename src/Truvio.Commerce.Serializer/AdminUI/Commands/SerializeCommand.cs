using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Dynamicweb.CoreUI.Data;

namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// API-callable serialize. Without <see cref="Scope"/> it serializes every configured predicate
/// of <see cref="Mode"/> (replace by default) and rewrites that mode's manifest. With a scope it
/// serializes only that predicate-shaped subtree or table, after the boundary check against the
/// configuration (<see cref="InlineScopeResolver"/>), and folds the result into the mode manifest.
///
/// Use via DW CLI: dw command Serialize [mode=merge]
/// Or via Management API: POST /Admin/Api/Serialize?mode=merge
///   body {"Mode":"replace","Scope":{"areaId":3,"path":"/Customer Center"}}
/// </summary>
public class SerializeCommand : CommandBase
{
    /// <summary>Serializer mode: "replace" (default) or "merge". Case-insensitive.</summary>
    public string Mode { get; set; } = "replace";

    /// <summary>
    /// Optional inline scope (predicate-shaped). Null runs every configured predicate of the mode.
    /// Also honored via query string: ?scope={json}.
    /// </summary>
    public InlineScope? Scope { get; set; }

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
        DeprecatedCommandAlias.Decorate(HandleCore(), DeprecatedRoute, "Serialize");

    private CommandResult HandleCore()
    {
        // Parse the mode string strictly; reject anything that isn't Replace or Merge
        // BEFORE any path-interpolation so the string never reaches the filesystem.
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
        // The fallback ALWAYS lands regardless of local curl probe results — D-38-11 is
        // the locked decision that `?mode=merge` binding is broken today.
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

        var (scope, invalidScope) = InlineScopeRequest.Read(Scope);
        if (invalidScope is not null)
            return invalidScope;

        try
        {
            var configPath = ConfigPathResolver.FindConfigFile();
            if (configPath == null)
                return new() { Status = CommandResult.ResultType.Error, Message = "Serializer.config.json not found" };

            var config = ConfigLoader.Load(configPath);

            // Inline scope: the configuration is the boundary, not the program.
            InlineScopeResolution? resolution = null;
            if (scope is not null)
            {
                resolution = InlineScopeResolver.Resolve(scope, serializerMode, config,
                    InlineScopeRequest.IdentifierValidator(), forDeserialize: false, InlineScopeRequest.ResolvePage);
                if (!resolution.IsValid)
                    return InlineScopeRequest.Rejected(scope, resolution);
            }

            // Mode-filter the flat predicate list.
            var modePredicates = config.Predicates.Where(p => p.Mode == serializerMode).ToList();
            var modeSubfolder = config.GetSubfolderForMode(serializerMode);
            var modeStrategy = config.GetConflictStrategyForMode(serializerMode);

            if (modePredicates.Count == 0)
                return new()
                {
                    Status = CommandResult.ResultType.Error,
                    Message = $"No {serializerMode} predicates configured"
                };

            var filesRoot = ConfigPathResolver.GetFilesRoot(configPath);
            var systemDir = Path.Combine(filesRoot, "System");
            var paths = config.EnsureDirectories(systemDir);

            var modeRoot = Path.Combine(paths.SerializeRoot, modeSubfolder);
            Directory.CreateDirectory(modeRoot);

            _logFile = LogFileWriter.CreateLogFile(paths.Log, "Serialize", serializerMode.ToString().ToLowerInvariant());
            Log($"=== Serializer Serialize (API) started [mode: {serializerMode}] ===");
            if (DeprecatedRoute is not null)
                Log($"DEPRECATED: {DeprecatedCommandAlias.Notice(DeprecatedRoute, "Serialize")}");

            var orchestrator = ProviderRegistry.CreateOrchestrator(filesRoot);
            OrchestratorResult result;
            if (resolution is not null)
            {
                Log($"=== Inline scope: {scope} -> '{resolution.Predicate!.Name}' inside configured predicate '{resolution.Fence!.Name}' ===");
                result = orchestrator.SerializeScope(
                    resolution.Predicate,
                    modeRoot,
                    serializerMode,
                    Log,
                    manifestWriter: new ManifestWriter(),
                    excludeFieldsByItemType: config.ExcludeFieldsByItemType,
                    excludeXmlElementsByType: config.ExcludeXmlElementsByType);
            }
            else
            {
                result = orchestrator.SerializeAll(
                    modePredicates,
                    modeRoot,
                    serializerMode,
                    modeStrategy,
                    Log,
                    providerFilter: null,
                    manifestWriter: new ManifestWriter(),
                    manifestCleaner: new ManifestCleaner(),
                    excludeFieldsByItemType: config.ExcludeFieldsByItemType,
                    excludeXmlElementsByType: config.ExcludeXmlElementsByType);
            }

            var fileCount = Directory.Exists(modeRoot)
                ? Directory.GetFiles(modeRoot, "*.yml", SearchOption.AllDirectories).Length
                : 0;

            // Build summary and flush log
            var summary = new LogFileSummary
            {
                Operation = "Serialize",
                Mode = serializerMode.ToString().ToLowerInvariant(),
                Timestamp = DateTime.UtcNow,
                Predicates = result.SerializeResults.Select(r => new PredicateSummary
                {
                    Name = r.TableName,
                    Table = r.TableName,
                    Created = r.RowsSerialized
                }).ToList(),
                TotalCreated = result.SerializeResults.Sum(r => r.RowsSerialized),
                Errors = result.Errors.ToList()
            };
            FlushLog(_logFile, summary);

            var scopeLabel = resolution is not null ? $", scope: {scope}" : "";
            var message = $"Serialization complete ({serializerMode}{scopeLabel}). {fileCount} YAML files in {modeRoot}. {result.Summary}";
            if (result.StaleFilesDeleted > 0)
                message += $" Cleaned {result.StaleFilesDeleted} stale file(s).";
            if (result.HasErrors)
            {
                // Per-predicate errors drive HasErrors too — surface them, not just the
                // run-level list, so the API response never says a bare "Errors: ".
                var allErrors = result.Errors
                    .Concat(result.SerializeResults
                        .Where(r => r.HasErrors)
                        .SelectMany(r => r.Errors.Select(e => $"{r.TableName}: {e}")));
                message += $" Errors: {string.Join("; ", allErrors)}";
            }

            // D-38-12: HTTP status is driven by result.HasErrors (Errors.Count > 0 ||
            // SerializeResults.Any(r => r.HasErrors)). A zero-error result MUST map to Ok
            // regardless of Message content. The test in SerializeCommandTests
            // (Handle_ZeroErrors_SynthOrchestratorResult_ReturnsOk) uses SynthOrchestratorResult
            // to assert this unconditionally — no environment-dependent branching.
            return MapStatusFromResult(result, message);
        }
        catch (Exception ex)
        {
            return new() { Status = CommandResult.ResultType.Error, Message = $"Serialization failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// D-38-12 test seam: exposes the status-mapping branch of <see cref="Handle"/> so
    /// <c>SerializeCommandTests.Handle_ZeroErrors_SynthOrchestratorResult_ReturnsOk</c>
    /// can assert the zero-error == Ok invariant unconditionally against a synthetic
    /// <see cref="OrchestratorResult"/>, without running the full serialize pipeline.
    /// </summary>
    internal static CommandResult InvokeMapStatusForTest(OrchestratorResult result)
        => MapStatusFromResult(result, result.Summary ?? string.Empty);

    /// <summary>
    /// D-38-12: HTTP status driven by <see cref="OrchestratorResult.HasErrors"/>.
    /// Zero-error result == Ok. Pure function; no side effects.
    /// </summary>
    private static CommandResult MapStatusFromResult(OrchestratorResult result, string message)
    {
        return new CommandResult
        {
            Status = result.HasErrors ? CommandResult.ResultType.Error : CommandResult.ResultType.Ok,
            Message = message
        };
    }
}
