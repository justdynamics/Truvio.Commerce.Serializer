using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Reporting;
using Truvio.Commerce.Serializer.Serialization;

namespace Truvio.Commerce.Serializer.Providers;

/// <summary>
/// Central dispatch: iterates predicates, resolves providers via ProviderRegistry,
/// validates each predicate, and aggregates results across all providers.
/// Supports FK-ordered deserialization, per-predicate cache invalidation, and
/// mode-aware (Replace/Merge) execution.
/// </summary>
public class SerializerOrchestrator
{
    private readonly ProviderRegistry _registry;
    private readonly FkDependencyResolver? _fkResolver;
    private readonly CacheInvalidator? _cacheInvalidator;
    private readonly EcomGroupFieldSchemaSync? _ecomSchemaSync;
    private readonly EcomProductFieldSchemaSync? _ecomProductFieldSchemaSync;
    private readonly ManifestWriter _manifestWriter;

    public SerializerOrchestrator(
        ProviderRegistry registry,
        FkDependencyResolver? fkResolver = null,
        CacheInvalidator? cacheInvalidator = null,
        EcomGroupFieldSchemaSync? ecomSchemaSync = null,
        ManifestWriter? manifestWriter = null,
        EcomProductFieldSchemaSync? ecomProductFieldSchemaSync = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fkResolver = fkResolver;
        _cacheInvalidator = cacheInvalidator;
        _ecomSchemaSync = ecomSchemaSync;
        _ecomProductFieldSchemaSync = ecomProductFieldSchemaSync;
        // Phase 43 / DESER-01: ManifestWriter is needed by the manifest-driven DeserializeAll
        // signature. Defaulting to a fresh instance keeps the legacy DeserializeAll(predicates, ...)
        // overload (which doesn't read the manifest) callable without explicit wiring.
        _manifestWriter = manifestWriter ?? new ManifestWriter();
    }

    // -------------------------------------------------------------------------
    // Mode-aware overloads (Phase 37-01)
    //
    // Phase 44 / CONVERGE-04 (D-07): the three [Obsolete] overloads that previously lived
    // here (two pre-Phase-37 SerializeAll/DeserializeAll defaults + the Phase-43 predicate-
    // typed DeserializeAll bridge) were deleted along with the predicate→entry bridge body.
    // Production deserialize callers route through the manifest-driven
    // DeserializeAll(Manifest, contentRoot, ...) public overload added in Phase 44 / D-01.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialize all predicates, scoped to the given mode. The mode/strategy pair is logged at the
    /// start of the run. Strategy is currently unused on the serialize path (it only affects
    /// deserialize conflict resolution), but is threaded through for symmetry with DeserializeAll.
    /// When <paramref name="manifestWriter"/> / <paramref name="manifestCleaner"/> are supplied,
    /// the orchestrator emits <c>{mode}-manifest.json</c> and deletes stale files under
    /// <paramref name="outputRoot"/> after the run (Phase 37-01 Task 2). Exceptions bubble out
    /// BEFORE the manifest step, so partial/failed runs leave stale files intact for debugging.
    /// </summary>
    public OrchestratorResult SerializeAll(
        List<ProviderPredicateDefinition> predicates,
        string outputRoot,
        SerializerMode mode,
        ConflictStrategy strategy,
        Action<string>? log = null,
        string? providerFilter = null,
        ManifestWriter? manifestWriter = null,
        ManifestCleaner? manifestCleaner = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        log?.Invoke($"=== Mode: {mode} | Strategy: {strategy} ===");

        // Multi-language: expand includeLanguageLayers Content predicates into one synthetic
        // predicate per language-layer area so each layer serializes (and gets a manifest
        // entry) like any other area. No-op when no predicate carries the flag.
        predicates = LanguageLayerExpander.Expand(
            predicates, LanguageLayerExpander.GetLanguageAreaIdsFromDw, log);

        var results = new List<SerializeResult>();
        var errors = new List<string>();

        foreach (var predicate in predicates)
        {
            if (providerFilter != null &&
                !string.Equals(predicate.ProviderType, providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var result = ExecuteScope(predicate, outputRoot, errors, log,
                excludeFieldsByItemType, excludeXmlElementsByType);
            if (result is not null)
                results.Add(result);
        }

        int stale = 0;
        if (manifestWriter != null || manifestCleaner != null)
        {
            // Manifest/subfolder label is the lowercased mode name ("replace" / "merge").
            var modeName = mode.ToString().ToLowerInvariant();
            var allWritten = results.SelectMany(r => r.WrittenFiles).ToList();

            // Phase 42-03: collect non-null Entry instances across providers. Validation-failed
            // results return null Entry (per SerializeResult.Entry docstring); they don't appear
            // in the manifest, but their files (if any) still feed the cleaner.
            var entries = results
                .Where(r => r.Entry is not null)
                .Select(r => r.Entry!)
                .ToList();

            // Phase 42-03 / MANIFEST-05: bake the by-ItemType exclusion maps into the envelope
            // so the deserialize path (Phase 43) does not need to consult Serializer.config.json
            // to read them.
            manifestWriter?.Write(outputRoot, modeName, entries,
                excludeFieldsByItemType: excludeFieldsByItemType,
                excludeXmlElementsByType: excludeXmlElementsByType);

            // Stale cleanup compares the manifest against THIS run's written files — a failed
            // predicate contributes nothing to WrittenFiles, so cleaning after a partial run
            // would delete every file the failed predicate wrote on previous successful runs.
            // Skip cleanup entirely when anything failed; the next clean run catches up.
            if (manifestCleaner != null)
            {
                if (errors.Count > 0 || results.Any(r => r.HasErrors))
                    log?.Invoke("Stale-file cleanup skipped: this run had errors, so the written-file set is incomplete and cleanup could delete files owned by the failed predicate(s).");
                else
                    stale = manifestCleaner.CleanStale(outputRoot, modeName, allWritten, log);
            }
        }

        return new OrchestratorResult { SerializeResults = results, Errors = errors, StaleFilesDeleted = stale };
    }

    /// <summary>
    /// Serializes ONE predicate: resolves its provider, pre-flight validates it and runs the
    /// provider. The unit both <see cref="SerializeAll"/> (every configured predicate of a mode)
    /// and <see cref="SerializeScope"/> (one inline API scope) are built from. Returns null when the
    /// predicate was skipped; the reason is appended to <paramref name="errors"/>.
    /// </summary>
    public SerializeResult? ExecuteScope(
        ProviderPredicateDefinition predicate,
        string outputRoot,
        List<string> errors,
        Action<string>? log = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        if (!_registry.HasProvider(predicate.ProviderType))
        {
            var msg = $"No provider registered for type '{predicate.ProviderType}' (predicate: {predicate.Name})";
            errors.Add(msg);
            log?.Invoke($"WARNING: Skipping predicate '{predicate.Name}' — no provider for type '{predicate.ProviderType}'");
            return null;
        }

        var provider = _registry.GetProvider(predicate.ProviderType);

        // Phase 43 / DESER-03: ValidatePredicate is no longer on the interface; each provider
        // exposes it concretely. The pre-flight keeps skip-on-invalid behaviour. Each provider's
        // own Serialize body validates again internally, so the pre-flight is a logging
        // convenience, not a correctness gate.
        var validation = ValidateBeforeSerialize(provider, predicate);
        if (!validation.IsValid)
        {
            errors.AddRange(validation.Errors.Select(e => $"{predicate.Name}: {e}"));
            log?.Invoke($"WARNING: Skipping predicate '{predicate.Name}' — validation failed: {string.Join(", ", validation.Errors)}");
            return null;
        }

        return provider.Serialize(predicate, outputRoot, log, excludeFieldsByItemType, excludeXmlElementsByType);
    }

    /// <summary>
    /// Serializes one inline API scope (the effective predicate from
    /// <see cref="InlineScopeResolver"/>) into <paramref name="modeRoot"/>. The scope's entries are
    /// folded into the existing <c>{mode}-manifest.json</c> (<see cref="ScopedManifest.Merge"/>)
    /// rather than replacing it, and stale-file cleanup does not run: the scope wrote a subset of
    /// the mode's files, so everything else it did not touch is still owned by other entries.
    /// </summary>
    public OrchestratorResult SerializeScope(
        ProviderPredicateDefinition scope,
        string modeRoot,
        SerializerMode mode,
        Action<string>? log = null,
        ManifestWriter? manifestWriter = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        log?.Invoke($"=== Mode: {mode} | Inline scope: '{scope.Name}' ===");

        var predicates = LanguageLayerExpander.Expand(
                new List<ProviderPredicateDefinition> { scope }, LanguageLayerExpander.GetLanguageAreaIdsFromDw, log)
            .Select(p => p with { IsInlineScope = true })
            .ToList();

        var results = new List<SerializeResult>();
        var errors = new List<string>();
        foreach (var predicate in predicates)
        {
            var result = ExecuteScope(predicate, modeRoot, errors, log, excludeFieldsByItemType, excludeXmlElementsByType);
            if (result is not null)
                results.Add(result);
        }

        var entries = results
            .Where(r => r.Entry is not null && !r.HasErrors)
            .Select(r => r.Entry!)
            .ToList();

        if (entries.Count > 0)
        {
            var writer = manifestWriter ?? _manifestWriter;
            var modeName = mode.ToString().ToLowerInvariant();
            var existing = writer.Read(modeRoot, modeName);
            writer.Write(modeRoot, modeName, ScopedManifest.Merge(existing?.Entries, entries),
                excludeFieldsByItemType: excludeFieldsByItemType,
                excludeXmlElementsByType: excludeXmlElementsByType);
            log?.Invoke($"{modeName}-manifest.json: folded {entries.Count} scoped entr{(entries.Count == 1 ? "y" : "ies")} " +
                        $"into {(existing is null ? "a new manifest" : $"{existing.Entries.Count} existing entries")}. " +
                        "Stale-file cleanup is left to the next full serialize of this mode.");
        }

        return new OrchestratorResult { SerializeResults = results, Errors = errors };
    }

    /// <summary>
    /// Deserializes one inline API scope from <paramref name="modeRoot"/>: reads the mode manifest,
    /// selects the entries (or the part of an entry) inside the scope via
    /// <see cref="ScopedManifest.SelectForScope"/>, and dispatches them through the same per-entry
    /// pipeline as <see cref="DeserializeAll(string, SerializerMode, ConflictStrategy, Action{string}, bool, string, StrictModeEscalator, IReadOnlyDictionary{string, List{string}}, IReadOnlyDictionary{string, List{string}})"/>.
    /// A scope that matches nothing in the manifest is a run-level error.
    /// </summary>
    public OrchestratorResult DeserializeScope(
        ProviderPredicateDefinition scope,
        string modeRoot,
        SerializerMode mode,
        ConflictStrategy strategy = ConflictStrategy.SourceWins,
        Action<string>? log = null,
        bool isDryRun = false,
        StrictModeEscalator? escalator = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var modeName = mode.ToString().ToLowerInvariant();
        var manifest = _manifestWriter.Read(modeRoot, modeName)
            ?? throw new InvalidOperationException(
                $"Manifest not found at {Path.Combine(modeRoot, $"{modeName}-manifest.json")}. " +
                "Run serialize first to produce the manifest, then re-run deserialize.");

        var selected = ScopedManifest.SelectForScope(manifest.Entries, scope, entry => ReadContentPagePaths(modeRoot, entry));
        log?.Invoke($"=== Inline scope '{scope.Name}': {selected.Count} of {manifest.Entries.Count} manifest entries selected ===");

        if (selected.Count == 0)
        {
            var msg = $"Inline scope '{scope.Name}' matches nothing in {modeName}-manifest.json. " +
                      "Serialize the scope in this mode first.";
            log?.Invoke($"ERROR: {msg}");
            return new OrchestratorResult { Errors = new List<string> { msg } };
        }

        return DeserializeEntries(selected, modeRoot, mode, strategy, log, isDryRun, providerFilter: null, escalator,
            manifest.ExcludeFieldsByItemType.Count > 0 ? manifest.ExcludeFieldsByItemType : null,
            manifest.ExcludeXmlElementsByType.Count > 0 ? manifest.ExcludeXmlElementsByType : null,
            isScoped: true);
    }

    /// <summary>(page.yml manifest key, menu-text content path) for every page of a Content entry's area tree.</summary>
    private static IEnumerable<(string FileKey, string ContentPath)> ReadContentPagePaths(string modeRoot, ContentEntry entry)
    {
        var contentDir = Path.Combine(modeRoot, "_content");
        if (string.IsNullOrEmpty(entry.AreaName) || !Directory.Exists(Path.Combine(contentDir, entry.AreaName)))
            return Array.Empty<(string, string)>();

        var area = new FileSystemStore().ReadTree(contentDir, entry.AreaName);
        return ScopedManifest.PagePaths(area.Pages).ToList();
    }

    // -------------------------------------------------------------------------
    // Phase 43 / DESER-01..05 + REPORT-01..05: manifest-driven deserialize.
    // Phase 44 / D-01: split into public Manifest-typed overload + thin disk-reading
    // wrapper. Single canonical dispatch site for full-deserialize + zip-import.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Phase 44 / CONVERGE-01 (D-01): manifest-driven deserialize accepting an in-memory
    /// <see cref="Manifest"/> directly. The on-disk-reading
    /// <see cref="DeserializeAll(string, SerializerMode, ConflictStrategy, Action{string}, bool, string, StrictModeEscalator, IReadOnlyDictionary{string, List{string}}, IReadOnlyDictionary{string, List{string}})"/>
    /// becomes a thin wrapper that reads the manifest then forwards here. Zip-import builds
    /// an in-memory <see cref="Manifest"/> (one synthesised <see cref="ContentEntry"/>) and
    /// calls this overload directly — single canonical dispatch site for full-deserialize +
    /// zip-import. MANIFEST-05 envelope precedence applied identically to the modeRoot
    /// overload.
    /// </summary>
    public OrchestratorResult DeserializeAll(
        Manifest manifest,
        string contentRoot,
        SerializerMode mode,
        ConflictStrategy strategy = ConflictStrategy.SourceWins,
        Action<string>? log = null,
        bool isDryRun = false,
        string? providerFilter = null,
        StrictModeEscalator? escalator = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        // MANIFEST-05 envelope precedence (same rule as the modeRoot overload).
        var effectiveExcludeFields = manifest.ExcludeFieldsByItemType.Count > 0
            ? (IReadOnlyDictionary<string, List<string>>)manifest.ExcludeFieldsByItemType
            : excludeFieldsByItemType;
        var effectiveExcludeXml = manifest.ExcludeXmlElementsByType.Count > 0
            ? (IReadOnlyDictionary<string, List<string>>)manifest.ExcludeXmlElementsByType
            : excludeXmlElementsByType;

        return DeserializeEntries(manifest.Entries, contentRoot, mode, strategy, log, isDryRun,
            providerFilter, escalator, effectiveExcludeFields, effectiveExcludeXml);
    }

    /// <summary>
    /// Phase 43 / DESER-01: manifest-driven deserialize. Reads
    /// <c>{mode}-manifest.json</c> from <paramref name="modeRoot"/> and dispatches each entry
    /// to the registered provider for its <c>ProviderType</c>. Per-entry outcomes (status,
    /// counts, errors, duration) populate <see cref="OrchestratorResult.EntryOutcomes"/>.
    /// </summary>
    /// <param name="modeRoot">Mode-scoped serialize directory (the dir containing
    /// <c>{mode}-manifest.json</c> + the per-provider subtrees).</param>
    /// <param name="mode">Serializer mode. The lowercased form is used as the manifest filename
    /// prefix (<c>"replace"</c> / <c>"merge"</c>).</param>
    /// <param name="strategy">Conflict strategy (Replace=SourceWins, Merge=DestinationWins).</param>
    /// <param name="log">Optional log sink — every entry emits a <c>[entryId] Status</c> line per
    /// REPORT-05 / SC-5.</param>
    /// <param name="isDryRun">When true, providers report would-be work without touching the DB.</param>
    /// <param name="providerFilter">Optional filter. Entries whose <c>ProviderType</c> doesn't
    /// match get an <see cref="EntryStatus.Skipped"/> outcome rather than being silently dropped
    /// (per REPORT-01 / D-02).</param>
    /// <param name="escalator">Optional strict-mode escalator. Phase 37-04 wiring is preserved
    /// unchanged; <see cref="CumulativeStrictModeException"/> at end-of-run produces an
    /// <see cref="EntryOutcome.RunLevelError"/> in addition to the run-level errors list.</param>
    /// <param name="excludeFieldsByItemType">Caller-supplied fallback when the manifest envelope
    /// has no envelope-level by-ItemType field exclusions. The manifest envelope (when populated
    /// per MANIFEST-05) takes precedence.</param>
    /// <param name="excludeXmlElementsByType">Same, for XML element exclusions.</param>
    public OrchestratorResult DeserializeAll(
        string modeRoot,
        SerializerMode mode,
        ConflictStrategy strategy = ConflictStrategy.SourceWins,
        Action<string>? log = null,
        bool isDryRun = false,
        string? providerFilter = null,
        StrictModeEscalator? escalator = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        // Phase 44 / D-01: thin wrapper over DeserializeAll(Manifest, ...). The disk read
        // happens here; everything past this point is identical to zip-import's in-memory
        // path. MANIFEST-05 envelope precedence is applied inside the Manifest-typed
        // overload, so the two paths agree on every dispatch invariant.
        //
        // Read {mode}-manifest.json — the lowercased mode name ("replace" / "merge").
        var modeName = mode.ToString().ToLowerInvariant();
        var manifest = _manifestWriter.Read(modeRoot, modeName);
        if (manifest == null)
            throw new InvalidOperationException(
                $"Manifest not found at {Path.Combine(modeRoot, $"{modeName}-manifest.json")}. " +
                "Run serialize first to produce the manifest, then re-run deserialize.");

        return DeserializeAll(manifest, modeRoot, mode, strategy, log, isDryRun, providerFilter,
            escalator, excludeFieldsByItemType, excludeXmlElementsByType);
    }

    /// <summary>
    /// Phase 43 internal test seam (per ARCHITECTURE.md §5): dispatch a pre-built entry list
    /// without touching the filesystem. Production callers go through the public
    /// <see cref="DeserializeAll(string, SerializerMode, ConflictStrategy, Action{string}, bool, string, StrictModeEscalator, IReadOnlyDictionary{string, List{string}}, IReadOnlyDictionary{string, List{string}})"/>
    /// which reads the manifest and calls this. Tests construct entry fixtures directly.
    /// </summary>
    internal OrchestratorResult DeserializeEntries(
        IReadOnlyList<ManifestEntry> entries,
        string modeRoot,
        SerializerMode mode,
        ConflictStrategy strategy,
        Action<string>? log,
        bool isDryRun,
        string? providerFilter,
        StrictModeEscalator? escalator,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType,
        bool isScoped = false)
    {
        // Engine issue #12: gate every entry's serviceCaches against DwCacheServiceRegistry
        // here — the first statement of the only dispatch site — so an unknown name fails the
        // call with zero rows written and the entry named, instead of surfacing at
        // InvalidateCaches after the entry's rows are already on the target.
        ManifestCacheValidation.ValidateServiceCaches(entries);

        // Phase 37-04 STRICT-01: wrap log with escalator (verbatim from legacy body).
        escalator ??= StrictModeEscalator.Null;
        var wrappedLog = WrapLogWithEscalator(log, escalator);
        wrappedLog($"=== Mode: {mode} | Strategy: {strategy} | Strict: {escalator.IsStrict} ===");

        var workingEntries = entries.ToList();

        // Phase 43 / DESER-02 / SC-6: FK ordering on entries[]. Same algorithm as the
        // predicate-typed legacy body, swapping `predicates` for `workingEntries.OfType<SqlTableEntry>`.
        if (_fkResolver != null)
        {
            var sqlEntries = workingEntries.OfType<SqlTableEntry>().ToList();
            if (sqlEntries.Count > 1)
            {
                var tableNames = sqlEntries
                    .Where(e => !string.IsNullOrEmpty(e.Table))
                    .Select(e => e.Table)
                    .ToList();

                var orderedTables = _fkResolver.GetDeserializationOrder(tableNames);

                var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < orderedTables.Count; i++)
                    orderIndex[orderedTables[i]] = i;

                // Reorder SqlTable entries by FK order; keep non-SqlTable entries in their
                // original relative order. Same shape as the legacy predicate-typed sort.
                var nonSqlEntries = workingEntries
                    .Where(e => e is not SqlTableEntry)
                    .ToList();
                var sortedSqlEntries = sqlEntries
                    .OrderBy(e => orderIndex.TryGetValue(e.Table, out var idx) ? idx : int.MaxValue)
                    .Cast<ManifestEntry>()
                    .ToList();

                workingEntries = sortedSqlEntries.Concat(nonSqlEntries).ToList();

                wrappedLog($"FK ordering: {string.Join(" -> ", orderedTables)}");
            }
        }

        // Phase 37-05 / LINK-02 pass 2 (D-22): when ANY SqlTable entry has a non-empty
        // ResolveLinksInColumns list, Content entries MUST run BEFORE those SqlTable entries
        // so the source→target page ID map is built and available at write time.
        var anySqlNeedsLinks = workingEntries
            .OfType<SqlTableEntry>()
            .Any(s => s.ResolveLinksInColumns.Count > 0);
        if (anySqlNeedsLinks)
        {
            var contentEntries = workingEntries.OfType<ContentEntry>().Cast<ManifestEntry>().ToList();
            var otherEntries = workingEntries.Where(e => e is not ContentEntry).ToList();
            if (contentEntries.Count > 0)
            {
                workingEntries = contentEntries.Concat(otherEntries).ToList();
                wrappedLog(
                    $"LINK-02 ordering: running {contentEntries.Count} Content entries " +
                    "first so cross-env page ID map is available to SqlTable link resolution.");
            }
        }

        // Per-entry dispatch loop. Builds EntryOutcome per entry per REPORT-02; Phase 44 / IN-01
        // deleted the transient legacyResults list (OrchestratorResult.DeserializeResults
        // field went with it), so consumers drive off EntryOutcomes exclusively.
        var entryOutcomes = new List<EntryOutcome>();
        var errors = new List<string>();
        var aggregatedPageMap = new Dictionary<int, int>();

        foreach (var entry in workingEntries)
        {
            // providerFilter exclusion → Skipped per REPORT-01 / D-02 / SC-2.
            if (providerFilter != null &&
                !string.Equals(entry.ProviderType, providerFilter, StringComparison.OrdinalIgnoreCase))
            {
                entryOutcomes.Add(EntryOutcome.Skipped(entry,
                    $"providerFilter='{providerFilter}' excluded providerType='{entry.ProviderType}'"));
                wrappedLog($"[{entry.EntryId}] Skipped: providerFilter exclusion");
                continue;
            }

            entryOutcomes.Add(ExecuteEntry(entry, modeRoot, strategy, wrappedLog, isDryRun,
                excludeFieldsByItemType, excludeXmlElementsByType, aggregatedPageMap, errors));
        }

        // Deferred permissions (groups-after-content ordering trap): the LINK-02 pass forces
        // Content entries ahead of the SqlTable predicate that creates the customer user groups,
        // so a tile/page permission referencing a group was applied with an interim Anonymous=None
        // deny and recorded to the ledger. Every entry — including the group-creating predicate —
        // has now run, so re-apply the recorded permission sets against a fresh group cache. Runs
        // for all modes; an empty ledger (no deferrals — e.g. groups pre-seeded, or no group
        // permissions in the YAML) is a no-op.
        if (!isDryRun && providerFilter is null)
            Serialization.DeferredPermissionLedger.Finalize(modeRoot, wrappedLog);

        // Area ITEM fields are replace-owned but may reference pages that only arrive in the
        // merge pass (chrome bindings, legal-page links) — the replace pass leaves those as
        // source ids. Now that both modes' pages are on target, finalize them by re-writing
        // from the replace YAML and resolving with the complete map.
        // A scoped pass finalizes deferred page links only: rewriting every replace area's item
        // fields would reach outside the scope. The next full merge pass finalizes those.
        if (!isDryRun && mode == SerializerMode.Merge && providerFilter is null)
            FinalizeReplaceAreaLinks(modeRoot, wrappedLog, includeAreaItemLinks: !isScoped);

        // Phase 37-04 STRICT-01: end-of-run gate. CONTEXT line 99-100 — strict-mode
        // CumulativeStrictModeException is routed into both the run-level errors list AND
        // a synthetic RunLevelError EntryOutcome so HasErrors aggregates from EntryOutcomes.
        try
        {
            escalator.AssertNoWarnings();
        }
        catch (CumulativeStrictModeException ex)
        {
            errors.Add(ex.Message);
            entryOutcomes.Add(EntryOutcome.RunLevelError(ex.Message));
            wrappedLog($"ERROR: {ex.Message}");
        }

        // Engine issue #5: quarantined warnings did NOT fail the run, but they must never be
        // silent — they are reported as their own end-of-run block and carried on the result.
        // Snapshot the list: the lines go back through wrappedLog, and the "  - " indent keeps
        // the wrapper's WARNING-prefix capture from re-recording them.
        var quarantined = escalator.QuarantinedWarnings.ToList();
        if (quarantined.Count > 0)
        {
            wrappedLog(
                $"QUARANTINED: {quarantined.Count} warning(s) were quarantined per-item and did " +
                "NOT fail this pass (strict-mode quarantine is active for their class):");
            foreach (var q in quarantined)
                wrappedLog($"  - {q}");
        }

        return new OrchestratorResult
        {
            EntryOutcomes = entryOutcomes,
            Errors = errors,
            QuarantinedWarnings = quarantined
        };
    }

    /// <summary>
    /// Dispatches ONE manifest entry to its provider and runs the entry's post-processing (page-map
    /// aggregation for later link resolution, cache invalidation, schema syncs). The per-entry unit
    /// of every deserialize: full mode passes, zip import and inline scopes.
    /// </summary>
    private EntryOutcome ExecuteEntry(
        ManifestEntry entry,
        string modeRoot,
        ConflictStrategy strategy,
        Action<string> wrappedLog,
        bool isDryRun,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType,
        Dictionary<int, int> aggregatedPageMap,
        List<string> errors)
    {
        // No provider registered → Failed per D-02.
        if (!_registry.HasProvider(entry.ProviderType))
        {
            var msg = $"No provider registered for type '{entry.ProviderType}' (entry: {entry.EntryId})";
            errors.Add(msg);
            wrappedLog($"[{entry.EntryId}] Failed: {msg}");
            return EntryOutcome.Failed(entry, msg);
        }

        // Phase 37-05 / LINK-02 pass 2: build an InternalLinkResolver from the accumulated
        // map when this entry is a SqlTableEntry that opted in via ResolveLinksInColumns.
        InternalLinkResolver? perRunResolver = null;
        var needsLinks = entry is SqlTableEntry sqlNeedsLinks
                         && sqlNeedsLinks.ResolveLinksInColumns.Count > 0
                         && aggregatedPageMap.Count > 0;
        if (needsLinks)
            perRunResolver = new InternalLinkResolver(aggregatedPageMap, wrappedLog);

        var provider = _registry.GetProvider(entry.ProviderType);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ProviderDeserializeResult result;
        try
        {
            result = provider.Deserialize(entry, modeRoot, wrappedLog, isDryRun, strategy,
                perRunResolver, excludeFieldsByItemType, excludeXmlElementsByType);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var emsg = $"Entry '{entry.EntryId}' threw: {ex.Message}";
            errors.Add(emsg);
            wrappedLog($"[{entry.EntryId}] Failed: {emsg}");
            return EntryOutcome.Failed(entry, emsg, sw.Elapsed);
        }
        sw.Stop();

        var outcome = EntryOutcome.From(entry, result, sw.Elapsed);

        // Per-entry log line per REPORT-05 / SC-5 (CONTEXT line 50 format).
        wrappedLog($"[{entry.EntryId}] {outcome.Status}: {result.Summary}");

        // Aggregate source→target page map (Content provider populates it; downstream
        // SqlTable entries with ResolveLinksInColumns consume it via perRunResolver).
        if (result.SourceToTargetPageMap != null)
        {
            foreach (var kvp in result.SourceToTargetPageMap)
                aggregatedPageMap.TryAdd(kvp.Key, kvp.Value);
        }

        // Cache invalidation gated on entry being a SqlTableEntry with ServiceCaches set.
        if (!isDryRun && entry is SqlTableEntry sqlEntryCache
            && sqlEntryCache.ServiceCaches.Count > 0
            && !result.HasErrors)
        {
            if (_cacheInvalidator == null)
            {
                wrappedLog(
                    $"WARNING: Entry '{entry.EntryId}' declares {sqlEntryCache.ServiceCaches.Count} " +
                    "service cache(s) but no CacheInvalidator is wired — caches will NOT be cleared");
            }
            else
            {
                try { _cacheInvalidator.InvalidateCaches(sqlEntryCache.ServiceCaches.ToList(), wrappedLog); }
                catch (Exception ex)
                {
                    wrappedLog($"WARNING: Cache invalidation failed for entry '{entry.EntryId}': {ex.Message}");
                }
            }
        }

        // Schema sync gated on entry being a SqlTableEntry with SchemaSync = "EcomGroupFields".
        if (!isDryRun && _ecomSchemaSync != null
            && entry is SqlTableEntry sqlEntrySync
            && !string.IsNullOrEmpty(sqlEntrySync.SchemaSync)
            && string.Equals(sqlEntrySync.SchemaSync, "EcomGroupFields", StringComparison.OrdinalIgnoreCase)
            && !result.HasErrors)
        {
            try
            {
                wrappedLog($"Running schema sync for {entry.EntryId}...");
                _ecomSchemaSync.SyncSchema(wrappedLog);
            }
            catch (Exception ex)
            {
                wrappedLog($"WARNING: Schema sync failed for entry '{entry.EntryId}': {ex.Message}");
            }
        }

        // Column-backed product-field schema sync (LRN-hosted-publish-01). EcomProductField
        // definition rows are column-backed on EcomProducts; deserializing them without
        // creating the columns breaks every product read and silently zeroes index builds.
        // Run the sync right after the EcomProductField entry writes so the columns exist
        // BEFORE the EcomProducts entry (ordered later) writes its rows. Triggered by the
        // schemaSync="EcomProductFields" marker OR the table name, so a config that predates
        // the marker (the exact shape that surfaced the bug) is still protected. After the
        // sync, warn (strict: escalate via WrapLogWithEscalator) if any definition still
        // lacks a backing column.
        if (!isDryRun && _ecomProductFieldSchemaSync != null
            && entry is SqlTableEntry sqlEntryProductSync
            && (string.Equals(sqlEntryProductSync.SchemaSync, "EcomProductFields", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sqlEntryProductSync.Table, "EcomProductField", StringComparison.OrdinalIgnoreCase))
            && !result.HasErrors)
        {
            try
            {
                wrappedLog($"Running product-field schema sync for {entry.EntryId}...");
                _ecomProductFieldSchemaSync.SyncSchema(wrappedLog);
                _ecomProductFieldSchemaSync.WarnMissingColumns(wrappedLog);
            }
            catch (Exception ex)
            {
                wrappedLog($"WARNING: Product-field schema sync failed for entry '{entry.EntryId}': {ex.Message}");
            }
        }

        return outcome;
    }

    /// <summary>
    /// Locates the sibling REPLACE mode root next to the merge mode root and finalizes the
    /// area item links of every whole-area replace Content entry (see
    /// <see cref="Serialization.ContentDeserializer.FinalizeAreaItemLinks"/>). Best-effort:
    /// absent sibling roots / manifests are skipped silently (single-mode setups).
    /// </summary>
    private void FinalizeReplaceAreaLinks(string mergeModeRoot, Action<string> log, bool includeAreaItemLinks = true)
    {
        try
        {
            var serializeRoot = Path.GetDirectoryName(
                Path.GetFullPath(mergeModeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (serializeRoot is null || !Directory.Exists(serializeRoot))
                return;

            foreach (var siblingRoot in includeAreaItemLinks ? Directory.GetDirectories(serializeRoot) : Array.Empty<string>())
            {
                if (string.Equals(Path.GetFullPath(siblingRoot), Path.GetFullPath(mergeModeRoot), StringComparison.OrdinalIgnoreCase))
                    continue;

                var manifest = _manifestWriter.Read(siblingRoot, Path.GetFileName(siblingRoot));
                if (manifest is null)
                    continue;

                var contentDir = Path.Combine(siblingRoot, "_content");
                if (!Directory.Exists(contentDir))
                    continue;

                foreach (var entry in manifest.Entries.OfType<ContentEntry>())
                {
                    var deserializer = new Serialization.ContentDeserializer(
                        entry, contentDir, log: log,
                        excludeFieldsByItemType: manifest.ExcludeFieldsByItemType.Count > 0
                            ? manifest.ExcludeFieldsByItemType
                            : null);
                    deserializer.FinalizeAreaItemLinks();
                }
            }

            // Deferred PAGE/paragraph link occurrences recorded by any pass (either mode
            // root) are finalized now that every page is on target. The cross-mode map is
            // guid-anchored: every YAML page of every mode root, resolved to its target id.
            var store = new Infrastructure.FileSystemStore();
            var allYamlPages = new List<Models.SerializedPage>();
            foreach (var modeRoot in Directory.GetDirectories(serializeRoot))
            {
                var contentDir = Path.Combine(modeRoot, "_content");
                if (!Directory.Exists(contentDir)) continue;
                foreach (var areaDir in Directory.GetDirectories(contentDir))
                {
                    if (!File.Exists(Path.Combine(areaDir, "area.yml"))) continue;
                    try { allYamlPages.AddRange(store.ReadTree(contentDir, Path.GetFileName(areaDir)).Pages); }
                    catch { /* best-effort */ }
                }
            }
            var guidCache = new Dictionary<Guid, int>();
            foreach (var dwArea in Dynamicweb.Content.Services.Areas.GetAreas())
                foreach (var page in Dynamicweb.Content.Services.Pages.GetPagesByAreaID(dwArea.ID))
                    if (page.UniqueId != Guid.Empty)
                        guidCache.TryAdd(page.UniqueId, page.ID);
            var crossModeMap = InternalLinkResolver.BuildSourceToTargetMap(allYamlPages, guidCache);

            foreach (var modeRoot in Directory.GetDirectories(serializeRoot))
                Serialization.DeferredLinkLedger.Finalize(modeRoot, crossModeMap, log);
        }
        catch (Exception ex)
        {
            log($"WARNING: replace area link finalization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 43 / DESER-03: typed-dispatch validation helper for the serialize-side and the
    /// legacy predicate-typed DeserializeAll body. ValidatePredicate is no longer on the
    /// <see cref="ISerializationProvider"/> contract; each concrete provider keeps it as a
    /// public method for serialize-time input gating. This helper polymorphically routes to
    /// the right concrete method without re-introducing the interface dependency. Returns
    /// <see cref="ValidationResult.Success"/> for unrecognised provider types so callers
    /// fall through to the provider's own internal validation in Serialize/Deserialize.
    /// </summary>
    private static ValidationResult ValidateBeforeSerialize(ISerializationProvider provider, ProviderPredicateDefinition predicate)
    {
        return provider switch
        {
            Content.ContentProvider c => c.ValidatePredicate(predicate),
            SqlTable.SqlTableProvider s => s.ValidatePredicate(predicate),
            _ => ValidationResult.Success()
        };
    }

    /// <summary>
    /// Phase 37-04: wrap the caller's log so every "WARNING:" line (from anywhere —
    /// orchestrator, provider, ContentDeserializer, InternalLinkResolver, etc.) routes
    /// through the escalator. Non-WARNING lines pass through unchanged. In strict mode
    /// the warning is recorded for end-of-run assertion; the single log emission still
    /// reaches the caller's sink so operators see every warning in real time.
    /// </summary>
    private static Action<string> WrapLogWithEscalator(Action<string>? callerLog, StrictModeEscalator escalator)
    {
        return msg =>
        {
            if (msg is null)
            {
                callerLog?.Invoke(string.Empty);
                return;
            }

            // Forward to the caller's log first so the line appears in real-time output.
            callerLog?.Invoke(msg);

            // Route WARNING lines into the escalator's record buffer (strict) without a
            // second log emission — we pass a null log sink to Escalate.
            if (msg.TrimStart().StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                escalator.RecordOnly(msg);
        };
    }
}

/// <summary>
/// Aggregated result from orchestrator operations across multiple providers.
/// </summary>
public record OrchestratorResult
{
    public List<SerializeResult> SerializeResults { get; init; } = new();

    /// <summary>
    /// Phase 43 / REPORT-03 + Phase 44 / IN-01: per-entry outcomes — one <see cref="EntryOutcome"/>
    /// per dispatched manifest entry, plus optional <c>Skipped</c> outcomes (providerFilter
    /// exclusion) and optional run-level synthetic outcomes (strict-mode escalation). The
    /// canonical source of truth driving <see cref="HasErrors"/> per REPORT-04. The pre-Phase-44
    /// <c>DeserializeResults</c> compatibility surface was deleted along with its
    /// <see cref="Summary"/> fallback branch (Phase 44 / IN-01 + IN-02).
    /// </summary>
    public List<EntryOutcome> EntryOutcomes { get; init; } = new();

    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Engine issue #5: warnings whose class was quarantined for this run — reported loudly
    /// but deliberately excluded from <see cref="Errors"/> / <see cref="HasErrors"/> so one
    /// unresolvable link no longer fails a pass that wrote hundreds of clean rows. Empty
    /// unless the caller opted a class into quarantine.
    /// </summary>
    public List<string> QuarantinedWarnings { get; init; } = new();

    /// <summary>
    /// Stale files deleted by <see cref="ManifestCleaner"/> during post-serialize cleanup
    /// (Phase 37-01 Task 2). Zero when no cleaner was wired or no stale files were found.
    /// </summary>
    public int StaleFilesDeleted { get; init; }

    /// <summary>
    /// Phase 43 / REPORT-04 / SC-3 + Phase 44 / IN-01: HasErrors aggregates from
    /// <list type="number">
    /// <item>Run-level <see cref="Errors"/> (e.g. orchestrator-level wiring failures).</item>
    /// <item>Any <see cref="SerializeResults"/> entry with errors.</item>
    /// <item>Any <see cref="EntryOutcomes"/> entry whose status is <see cref="EntryStatus.Failed"/>.</item>
    /// </list>
    /// Phase 44 / IN-01 deleted the pre-existing <c>DeserializeResults.Any(...)</c> clause along
    /// with the field — EntryOutcome.From propagates ProviderDeserializeResult.HasErrors into
    /// EntryStatus.Failed, so the EntryOutcomes clause covers exactly the same surface plus
    /// orchestrator-level failure modes (no provider registered, dispatch threw, strict-mode
    /// RunLevelError).
    /// </summary>
    public bool HasErrors =>
        Errors.Count > 0 ||
        SerializeResults.Any(r => r.HasErrors) ||
        EntryOutcomes.Any(e => e.Status == EntryStatus.Failed);

    /// <summary>
    /// Engine issue #10: the outcomes that correspond to a real manifest entry — every
    /// <see cref="EntryOutcomes"/> element except the synthetic
    /// <see cref="EntryOutcome.RunLevelEntryId"/> outcome strict-mode escalation appends.
    /// A manifest of N entries has exactly N of these, which is what the run report counts.
    /// </summary>
    public IReadOnlyList<EntryOutcome> ManifestEntryOutcomes =>
        EntryOutcomes.Where(o => o.EntryId != EntryOutcome.RunLevelEntryId).ToList();

    /// <summary>
    /// Engine issue #11: every error the run produced — run-level <see cref="Errors"/> first,
    /// then each failed entry's own error strings prefixed with its entry id. Before this, a
    /// per-entry failure (e.g. "Unable to resolve the item type") reached only the log file:
    /// the API message read "1 failed ... Errors: " with an empty list, because
    /// <see cref="Errors"/> carries run-level errors only.
    /// </summary>
    public IReadOnlyList<string> AllErrors
    {
        get
        {
            var all = new List<string>(Errors);
            var seen = new HashSet<string>(Errors, StringComparer.Ordinal);

            foreach (var outcome in EntryOutcomes)
            {
                if (outcome.Status != EntryStatus.Failed) continue;
                if (outcome.EntryId == EntryOutcome.RunLevelEntryId) continue; // already in Errors

                foreach (var error in outcome.Errors)
                {
                    var line = $"[{outcome.EntryId}] {error}";
                    if (seen.Add(line))
                        all.Add(line);
                }
            }

            return all;
        }
    }

    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (SerializeResults.Count > 0)
            {
                var totalRows = SerializeResults.Sum(r => r.RowsSerialized);
                parts.Add($"Serialized: {totalRows} rows across {SerializeResults.Count} predicates");
            }

            // Phase 44 / IN-02: dead else-if branch over DeserializeResults removed along
            // with the field. EntryOutcomes is the canonical surface.
            // Engine issue #10: count manifest entries only. The synthetic RunLevelError outcome
            // strict mode appends is not an entry, and counting it reported "across 10 entries"
            // for a 9-entry manifest while every other counter stayed consistent.
            var manifestOutcomes = ManifestEntryOutcomes;
            if (manifestOutcomes.Count > 0)
            {
                var created = manifestOutcomes.Sum(o => o.Counts.Created);
                var updated = manifestOutcomes.Sum(o => o.Counts.Updated);
                var skipped = manifestOutcomes.Sum(o => o.Counts.Skipped);
                var failed = manifestOutcomes.Sum(o => o.Counts.Failed);
                parts.Add($"Deserialized: {created} created, {updated} updated, {skipped} skipped, {failed} failed across {manifestOutcomes.Count} entries");
            }

            if (Errors.Count > 0)
                parts.Add($"Errors: {Errors.Count}");

            if (QuarantinedWarnings.Count > 0)
                parts.Add($"Quarantined: {QuarantinedWarnings.Count}");

            return string.Join(". ", parts);
        }
    }
}
