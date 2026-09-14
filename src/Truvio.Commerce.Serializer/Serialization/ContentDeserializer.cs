using Dynamicweb.Content;
using Dynamicweb.Data;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Orchestrates the disk-to-Truvio Commerce deserialization pipeline:
/// reads YAML files via FileSystemStore.ReadTree(), resolves GUID identity against
/// the target database, writes items in dependency order (Area > Pages > GridRows > Paragraphs),
/// supports dry-run mode with field-level diffs, and handles errors with cascade-skip semantics.
///
/// <para>
/// Phase 44 / CONVERGE-01 + D-04: pivoted from <see cref="SerializerConfiguration"/>-driven
/// dispatch (predicate list inside config) to a single <see cref="ContentEntry"/> per call.
/// One area's worth of work per <see cref="Deserialize"/> invocation — the orchestrator
/// invokes this once per <see cref="ContentEntry"/> in the manifest. The synthetic predicate
/// at the previous <c>ContentProvider.Deserialize</c> call site is gone.
/// </para>
/// </summary>
public class ContentDeserializer
{
    private readonly ContentEntry _entry;
    private readonly string _contentRoot;
    private readonly IReadOnlyDictionary<string, List<string>>? _excludeFieldsByItemType;
    private readonly IContentStore _store;
    private readonly Action<string>? _log;
    private readonly bool _isDryRun;
    private readonly string? _filesRoot;
    private readonly ConflictStrategy _conflictStrategy;
    private readonly TargetSchemaCache _schemaCache;
    private readonly PermissionMapper _permissionMapper;
    private readonly TemplateAssetManifest _templateManifest;
    private readonly StrictModeEscalator _templateEscalator;
    // Phase 38 A.2 (D-38-05): test seam for Area SQL write paths so the
    // SET IDENTITY_INSERT [Area] ON/OFF wrapping can be asserted without a live DB.
    // Production default: DwSqlExecutor (wraps Dynamicweb.Data.Database.ExecuteNonQuery).
    private readonly ISqlExecutor _sqlExecutor;

    /// <summary>
    /// When <see cref="ConflictStrategy.DestinationWins"/> (Merge mode), pages whose
    /// <c>PageUniqueId</c> is already present on target are field-level filled from the Merge
    /// YAML: scalars, sub-object DTO properties, ItemFields, and PropertyItem fields each
    /// honor <see cref="MergePredicate.IsUnsetForMerge(object?, System.Type)"/>. Only fields
    /// that are NULL or at the type default on target are filled from YAML; destination tweaks
    /// already set on target survive intrinsically. Page permissions are never touched on the
    /// Merge UPDATE path.
    /// </summary>
    /// <param name="entry">Manifest entry for the single area subtree to deserialize.
    /// Carries AreaId, Path, PageId, AcknowledgedOrphanPageIds, ExcludeAreaColumns, and
    /// ExcludeFields (the latter promoted from <c>ProviderPredicateDefinition.ExcludeFields</c>
    /// per Phase 44 D-04 / BLOCKER 2).</param>
    /// <param name="contentRoot">Filesystem root containing the per-area YAML subtrees
    /// (replaces the pre-pivot <c>SerializerConfiguration.OutputDirectory</c>).</param>
    /// <param name="excludeFieldsByItemType">Optional by-ItemType field exclusions threaded
    /// from the orchestrator (envelope-level per MANIFEST-05). Null/empty → no by-type
    /// exclusions; preserved exactly to keep the Replace-side area-creation path's exclusion
    /// semantics identical.</param>
    /// <param name="schemaCache">
    /// Shared target-schema cache used by the Area write path for schema-drift tolerance and
    /// YAML→CLR type coercion (Phase 37-02). Defaults to a new instance backed by the live
    /// INFORMATION_SCHEMA query loader.
    /// </param>
    /// <param name="sqlExecutor">
    /// Phase 38 A.2 (D-38-05): optional test seam for the Area write paths. Production callers
    /// pass <c>null</c> to get a <see cref="DwSqlExecutor"/> wrapping the live Dynamicweb.Data.Database
    /// static API. Tests inject a Moq&lt;ISqlExecutor&gt; to capture CommandBuilder text and
    /// assert on the SET IDENTITY_INSERT [Area] ON/INSERT/OFF ordering.
    /// </param>
    public ContentDeserializer(
        ContentEntry entry,
        string contentRoot,
        IContentStore? store = null,
        Action<string>? log = null,
        bool isDryRun = false,
        string? filesRoot = null,
        ConflictStrategy conflictStrategy = ConflictStrategy.SourceWins,
        TargetSchemaCache? schemaCache = null,
        // Phase 38 A.2 (D-38-05): test seam for Area write paths.
        ISqlExecutor? sqlExecutor = null,
        // Phase 44 / D-04: envelope-level by-ItemType field exclusions threaded from
        // SerializerOrchestrator (MANIFEST-05). Optional; null/empty = no by-type exclusions.
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _contentRoot = contentRoot ?? throw new ArgumentNullException(nameof(contentRoot));
        _excludeFieldsByItemType = excludeFieldsByItemType;
        _store = store ?? new FileSystemStore();
        _log = log;
        _isDryRun = isDryRun;
        _filesRoot = filesRoot;
        _conflictStrategy = conflictStrategy;
        _permissionMapper = new PermissionMapper(log);
        _schemaCache = schemaCache ?? new TargetSchemaCache();
        _templateManifest = new TemplateAssetManifest();
        // Phase 37-05: manifest validation uses a lenient escalator by default — the
        // orchestrator's strict-mode wrapper (Phase 37-04) will intercept the WARNING
        // lines and escalate them at end-of-run when strict mode is active.
        _templateEscalator = new StrictModeEscalator(strict: false, log: _log);
        // Phase 38 A.2 (D-38-05): default SqlExecutor wraps Dynamicweb.Data.Database.
        _sqlExecutor = sqlExecutor ?? new DwSqlExecutor();
    }

    private void Log(string message) => _log?.Invoke(message);

    /// <summary>
    /// Conflict strategy for one document: the mode in its <see cref="DocumentHeader"/>, or the
    /// pass strategy when the document has no header.
    /// </summary>
    private ConflictStrategy StrategyFor(DocumentHeader? header) => DocumentHeader.StrategyFor(header, _conflictStrategy);

    // -------------------------------------------------------------------------
    // Write context — carries mutable state through the recursive tree walk
    // -------------------------------------------------------------------------

    private class WriteContext
    {
        public int TargetAreaId { get; set; }
        public int ParentPageId { get; set; }  // 0 for root pages
        public Dictionary<Guid, int> PageGuidCache { get; set; } = new();
        public HashSet<Guid> FailedParentGuids { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        /// <summary>Fields excluded from serialization — must NOT be nulled out during deserialization.</summary>
        public IReadOnlySet<string>? ExcludeFields { get; set; }
        /// <summary>Per-item-type field exclusions from config-level dictionary.</summary>
        public IReadOnlyDictionary<string, List<string>>? ExcludeFieldsByItemType { get; set; }
    }

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Phase 44 / D-04: deserialize the single <see cref="ContentEntry"/> threaded in via the
    /// constructor. The orchestrator's per-entry switch dispatches one invocation per Content
    /// entry, so this body does one area's worth of work — read its YAML subtree, write pages,
    /// then resolve cross-area links over the content root once.
    /// </summary>
    public DeserializeResult Deserialize()
    {
        if (!Directory.Exists(_contentRoot))
        {
            var msg = $"contentRoot '{_contentRoot}' does not exist. " +
                      "Cannot deserialize — run serialization first to create it.";
            Log(msg);
            return new DeserializeResult
            {
                Errors = new List<string> { msg }
            };
        }

        // Phase 37-05 / TEMPLATE-01: pre-flight template manifest validation. Runs once
        // before any page writes so operators see missing-template errors up-front rather
        // than per-page during the run. Missing templates flow through the escalator —
        // orchestrator's strict-mode log wrapper elevates WARNING lines to the cumulative
        // exception when strict mode is active.
        ValidateTemplateManifest();

        // Collect this area's pages + GUID cache for the cross-area link-resolution pass
        // that runs after the per-entry write loop.
        var allAreaPages = new List<SerializedPage>();
        var globalPageGuidCache = new Dictionary<Guid, int>();

        // Phase 44 / D-04: a single ContentEntry drives a single area subtree write.
        // The orchestrator's per-entry switch dispatches one invocation per Content entry.
        // The entry's AreaName is the authoritative pointer to its YAML directory — it was
        // written together with the tree. Resolving via the live area instead breaks on a
        // blank target: GetArea() is null there and ReadTree(null) silently grabs the FIRST
        // area directory, so every entry deserializes the same tree (multi-area regression
        // found by the language-layer E2E). Live lookup stays as fallback for pre-AreaName
        // manifests only.
        var areaName = !string.IsNullOrEmpty(_entry.AreaName)
            ? _entry.AreaName
            : Services.Areas.GetArea(_entry.AreaId)?.Name;
        var area = _store.ReadTree(_contentRoot, areaName);

        // Multiple predicates of the same mode share one on-disk area directory; the read
        // tree is the MERGED union of all their files. Prune to THIS entry's manifest files
        // — without it every entry re-deserializes (and re-link-resolves) every sibling
        // entry's content: thousands of phantom merge-updates, and already-rewritten target
        // ids reinterpreted as source ids in the link pass.
        // File keys are compared WITHOUT the "_content/" prefix: full-pipeline manifests key
        // files mode-root-relative ("_content/<Area>/.../page.yml") while zip-import entries
        // key them zip-root-relative ("<Area>/.../page.yml") — comparing raw keys pruned
        // EVERY page on zip import and the upload silently wrote nothing.
        if (_entry.Files.Count > 0)
        {
            var entryFiles = new HashSet<string>(_entry.Files.Select(NormalizeFileKey), StringComparer.OrdinalIgnoreCase);
            area = area with { Pages = PruneToEntryFiles(area.Pages, entryFiles, _entry.StubUnlistedAncestors) };
        }

        // Snapshot pages that exist BEFORE this entry writes. Structural-stub ancestors
        // (deep-rooted predicates) that already exist were written and link-resolved by
        // the predicate that owns them — they must not be re-resolved by this entry.
        var preExistingPageGuids = new HashSet<Guid>(
            Services.Pages.GetPagesByAreaID(_entry.AreaId)
                .Where(p => p.UniqueId != Guid.Empty)
                .Select(p => p.UniqueId));

        var result = DeserializePredicate(_entry, area, globalPageGuidCache, allAreaPages);
        int totalCreated = result.Created;
        int totalUpdated = result.Updated;
        int totalSkipped = result.Skipped;
        int totalFailed = result.Failed;
        var allErrors = new List<string>(result.Errors);

        // Deferred permissions (groups-after-content ordering trap): a tile/page permission that
        // references a group not yet on target was applied with an interim Anonymous=None deny and
        // its full intended list recorded on the mapper. Persist those to the mode-root ledger so
        // the orchestrator's end-of-run pass re-applies them once the group-creating predicates
        // have executed. Mirrors the DeferredLinkLedger.Append modeRoot derivation below.
        if (!_isDryRun && _permissionMapper.PendingDeferrals.Count > 0)
        {
            var permModeRoot = Path.GetDirectoryName(_contentRoot.TrimEnd('/', '\\'));
            if (permModeRoot is not null)
            {
                DeferredPermissionLedger.Append(permModeRoot, _permissionMapper.PendingDeferrals);
                Log($"Deferred {_permissionMapper.PendingDeferrals.Count} permission set(s) to end-of-run re-apply (group(s) not yet on target at content-write time).");
            }
        }

        // Phase 2: Resolve internal links using a CROSS-AREA map
        // Read ALL area directories from the content root to build a complete source→target map
        // (ContentProvider calls us per-area, but links reference pages across areas)
        if (!_isDryRun && globalPageGuidCache.Count > 0)
        {
            // Collect pages from ALL area directories for a complete source ID map
            var allYamlPages = new List<SerializedPage>();
            var allGuidCache = new Dictionary<Guid, int>();

            // Scan all area subdirectories in the content root
            foreach (var areaDir in Directory.GetDirectories(_contentRoot))
            {
                var areaYml = Path.Combine(areaDir, "area.yml");
                if (!File.Exists(areaYml)) continue;

                try
                {
                    var areaData = _store.ReadTree(_contentRoot, Path.GetFileName(areaDir));
                    allYamlPages.AddRange(areaData.Pages);
                }
                catch { /* skip unreadable areas */ }
            }

            // Multi-mode runs ship sibling YAML (replace + merge under the same SerializeRoot).
            // Include sibling pages in the map so links to already-deserialized sibling pages
            // resolve, and remember which sibling pages are NOT on target yet — links to those
            // are deferred (rewritten during the sibling mode's own pass), not warnings.
            var siblingPages = ReadSiblingModePages();
            if (siblingPages.Count > 0)
                allYamlPages.AddRange(siblingPages);

            // Same-mode predicates that run AFTER this entry are sibling passes too: their
            // YAML pages are in allYamlPages but not yet on target. Defer links to them —
            // the end-of-merge-run ledger finalization rewrites the recorded occurrences.
            var sameModeLaterPages = allYamlPages;

            // Build GUID cache from ALL areas in the target DB
            foreach (var masterArea in Services.Areas.GetAreas())
            {
                foreach (var page in Services.Pages.GetPagesByAreaID(masterArea.ID))
                    if (page.UniqueId != Guid.Empty)
                        allGuidCache.TryAdd(page.UniqueId, page.ID);
            }

            var crossAreaMap = InternalLinkResolver.BuildSourceToTargetMap(allYamlPages, allGuidCache);
            Log($"Cross-area link resolution: {crossAreaMap.Count} page ID mappings from {Directory.GetDirectories(_contentRoot).Length} areas");

            // Sibling pages not yet on target = deferred link targets for this pass.
            var deferredIds = new HashSet<int>();
            CollectSourcePageIds(siblingPages, deferredIds);
            CollectSourcePageIds(sameModeLaterPages, deferredIds);
            deferredIds.ExceptWith(crossAreaMap.Keys);
            if (deferredIds.Count > 0)
                Log($"Sibling-mode link targets not yet on target (deferred to their own pass): {deferredIds.Count}");

            // Build paragraph map too
            var paragraphCache = new Dictionary<Guid, int>();
            foreach (var masterArea in Services.Areas.GetAreas())
                foreach (var page in Services.Pages.GetPagesByAreaID(masterArea.ID))
                    foreach (var para in Services.Paragraphs.GetParagraphsByPageId(page.ID))
                        if (para.UniqueId != Guid.Empty)
                            paragraphCache.TryAdd(para.UniqueId, para.ID);
            var paragraphMap = InternalLinkResolver.BuildSourceToTargetParagraphMap(allYamlPages, paragraphCache);

            var acknowledgedIds = _entry.AcknowledgedOrphanPageIds.Count > 0
                ? new HashSet<int>(_entry.AcknowledgedOrphanPageIds)
                : null;
            var resolver = new InternalLinkResolver(crossAreaMap, _log,
                sourceToTargetParagraphIds: paragraphMap,
                deferredSourcePageIds: deferredIds.Count > 0 ? deferredIds : null,
                acknowledgedSourcePageIds: acknowledgedIds);
            // Resolve ONLY the pages this entry wrote (their fields still hold source ids).
            // Re-scanning the whole area re-interprets links a previous entry or mode already
            // rewrote: the target ids in those links collide with unrelated source ids — at
            // best spurious strict-mode warnings, at worst a silent double-rewrite to the
            // wrong page (surfaced by the multi-entry-per-area language-layer E2E).
            // Area-level item fields follow the same ownership rule as their write.
            var entryTargetPageIds = new HashSet<int>();
            CollectEntryTargetPageIds(allAreaPages, globalPageGuidCache, entryTargetPageIds, preExistingPageGuids);
            var entryOwnsAreaState = _entry.PageId == 0 && (_entry.Path == "/" || _entry.Path.Length == 0);
            ResolveLinksInArea(_entry.AreaId, resolver, entryTargetPageIds, entryOwnsAreaState);

            var (resolved, unresolved, paraResolved, paraUnresolved) = resolver.GetStats();
            if (resolved > 0 || unresolved > 0 || resolver.DeferredCount > 0)
                Log($"Link resolution: {resolved} page links resolved, {unresolved} unresolvable, {resolver.DeferredCount} deferred to sibling mode; {paraResolved} paragraph anchors resolved, {paraUnresolved} unresolvable");

            // Persist deferred-link occurrences for end-of-merge-run finalization.
            if (resolver.DeferredRecords.Count > 0)
            {
                var modeRoot = Path.GetDirectoryName(_contentRoot.TrimEnd('/', '\\'));
                if (modeRoot is not null)
                    DeferredLinkLedger.Append(modeRoot, resolver.DeferredRecords);
            }

            // Multi-language: restore master links (Page.MasterPageId/MasterType,
            // Paragraph.MasterParagraphID/GlobalRecordPageID) from the GUID references the
            // mapper emitted. Runs against the full-DB caches so cross-area masters resolve;
            // masters not yet on target (language entry ordered before its master) warn.
            RestoreMasterLinks(allAreaPages, allGuidCache, paragraphCache);
        }

        var aggregated = new DeserializeResult
        {
            Created = totalCreated,
            Updated = totalUpdated,
            Skipped = totalSkipped,
            Failed = totalFailed,
            Errors = allErrors
        };

        Log(aggregated.Summary);
        if (aggregated.HasErrors)
        {
            foreach (var error in aggregated.Errors)
                Log(error);
        }

        return aggregated;
    }

    /// <summary>
    /// Locates sibling mode roots (e.g. <c>SerializeRoot/merge</c> while deserializing
    /// <c>SerializeRoot/replace</c>) and reads their page trees. Only applies when the content
    /// root follows the <c>&lt;SerializeRoot&gt;/&lt;mode&gt;/_content</c> convention — zip
    /// imports and ad-hoc roots return empty. Best-effort: unreadable areas are skipped.
    /// </summary>
    private List<SerializedPage> ReadSiblingModePages()
    {
        var result = new List<SerializedPage>();
        try
        {
            if (!string.Equals(Path.GetFileName(_contentRoot.TrimEnd('/', '\\')), "_content", StringComparison.OrdinalIgnoreCase))
                return result;

            var modeRoot = Path.GetDirectoryName(_contentRoot.TrimEnd('/', '\\'));
            var serializeRoot = modeRoot is null ? null : Path.GetDirectoryName(modeRoot);
            if (serializeRoot is null || !Directory.Exists(serializeRoot))
                return result;

            foreach (var siblingModeRoot in Directory.GetDirectories(serializeRoot))
            {
                if (string.Equals(Path.GetFullPath(siblingModeRoot), Path.GetFullPath(modeRoot!), StringComparison.OrdinalIgnoreCase))
                    continue;

                var siblingContent = Path.Combine(siblingModeRoot, "_content");
                if (!Directory.Exists(siblingContent))
                    continue;

                foreach (var areaDir in Directory.GetDirectories(siblingContent))
                {
                    if (!File.Exists(Path.Combine(areaDir, "area.yml")))
                        continue;
                    try { result.AddRange(_store.ReadTree(siblingContent, Path.GetFileName(areaDir)).Pages); }
                    catch { /* best-effort */ }
                }
            }
        }
        catch { /* best-effort — sibling awareness must never break the run */ }
        return result;
    }

    /// <summary>
    /// End-of-merge-run finalization for a REPLACE Content entry's area ITEM fields.
    /// Area item fields are replace-owned but may reference pages that arrive in the merge
    /// pass (header/footer bindings, legal-page links): at replace time those links cannot
    /// resolve and are left as source ids. Once every mode's pages are on target, this
    /// re-writes the fields from the replace YAML (fresh SOURCE ids — a deterministic input,
    /// so no risk of reinterpreting already-rewritten target ids) and resolves them against
    /// the complete cross-mode map. Construct with the REPLACE entry + the REPLACE _content root.
    /// </summary>
    public void FinalizeAreaItemLinks()
    {
        try
        {
            var ownsAreaState = _entry.PageId == 0 && (_entry.Path == "/" || _entry.Path.Length == 0);
            if (!ownsAreaState)
                return;

            var targetArea = Services.Areas.GetArea(_entry.AreaId);
            if (targetArea is null || string.IsNullOrEmpty(targetArea.ItemType) || string.IsNullOrEmpty(targetArea.ItemId))
                return;

            var areaName = !string.IsNullOrEmpty(_entry.AreaName) ? _entry.AreaName : targetArea.Name;
            var area = _store.ReadTree(_contentRoot, areaName);
            if (string.IsNullOrEmpty(area.ItemType) || area.ItemFields.Count == 0)
                return;

            // Same exclusion semantics as the AREA-01 write this finalization repeats.
            var entryExclude = _entry.ExcludeFields.Count > 0
                ? new HashSet<string>(_entry.ExcludeFields, StringComparer.OrdinalIgnoreCase)
                : null;
            IReadOnlySet<string>? effectiveExclude = _excludeFieldsByItemType is { Count: > 0 }
                ? ExclusionMerger.MergeFieldExclusions(
                    entryExclude?.ToList() ?? new List<string>(), _excludeFieldsByItemType, area.ItemType)
                : entryExclude;
            SaveItemFields(area.ItemType, targetArea.ItemId, area.ItemFields, effectiveExclude);

            // Cross-mode map: every YAML page (this mode + siblings) resolved to target by guid.
            var allYamlPages = new List<SerializedPage>();
            foreach (var areaDir in Directory.GetDirectories(_contentRoot))
            {
                if (!File.Exists(Path.Combine(areaDir, "area.yml"))) continue;
                try { allYamlPages.AddRange(_store.ReadTree(_contentRoot, Path.GetFileName(areaDir)).Pages); }
                catch { /* best-effort */ }
            }
            allYamlPages.AddRange(ReadSiblingModePages());

            var allGuidCache = new Dictionary<Guid, int>();
            foreach (var dwArea in Services.Areas.GetAreas())
                foreach (var page in Services.Pages.GetPagesByAreaID(dwArea.ID))
                    if (page.UniqueId != Guid.Empty)
                        allGuidCache.TryAdd(page.UniqueId, page.ID);

            var map = InternalLinkResolver.BuildSourceToTargetMap(allYamlPages, allGuidCache);
            var acknowledged = _entry.AcknowledgedOrphanPageIds.Count > 0
                ? new HashSet<int>(_entry.AcknowledgedOrphanPageIds)
                : null;
            var resolver = new InternalLinkResolver(map, _log, acknowledgedSourcePageIds: acknowledged);
            ResolveLinksInItemFields(area.ItemType, targetArea.ItemId, resolver);

            var (resolved, unresolved, _, _) = resolver.GetStats();
            Log($"Area link finalization: area {_entry.AreaId} item fields re-resolved ({resolved} resolved, {unresolved} unresolvable).");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Area link finalization failed for area {_entry.AreaId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps only pages whose page.yml belongs to the given manifest-entry file set, plus
    /// any page with kept descendants (parent chain must survive so attachment works).
    /// Pages outside the entry are dropped entirely — they belong to sibling entries.
    /// </summary>
    /// <summary>Strip the full-pipeline "_content/" manifest-key prefix so file keys from
    /// mode-root manifests and zip-root entries compare equal.</summary>
    internal static string NormalizeFileKey(string key) =>
        key.StartsWith("_content/", StringComparison.OrdinalIgnoreCase) ? key["_content/".Length..] : key;

    /// <param name="stubUnlistedAncestors">Scope-narrowed entries (<see cref="ContentEntry.StubUnlistedAncestors"/>):
    /// an ancestor kept only because a descendant is listed becomes a structural stub (scalars
    /// only, no grid rows, no permissions), so a subtree deserialize does not rewrite the pages above it.</param>
    internal static List<SerializedPage> PruneToEntryFiles(List<SerializedPage> pages, HashSet<string> entryFiles,
        bool stubUnlistedAncestors = false)
    {
        var kept = new List<SerializedPage>();
        foreach (var page in pages)
        {
            var children = PruneToEntryFiles(page.Children, entryFiles, stubUnlistedAncestors);
            var selfIncluded = page.SourceFile is not null && entryFiles.Contains(NormalizeFileKey(page.SourceFile));
            if (selfIncluded)
                kept.Add(page with { Children = children });
            else if (children.Count > 0)
                kept.Add(stubUnlistedAncestors
                    ? page with
                    {
                        Children = children,
                        IsStructuralStub = true,
                        GridRows = new List<SerializedGridRow>(),
                        Permissions = new List<SerializedPermission>()
                    }
                    : page with { Children = children });
        }
        return kept;
    }

    /// <summary>
    /// Maps this entry's YAML pages (recursively) to their TARGET page ids via the GUID
    /// cache. The result is the exact set of pages the entry wrote — the only pages whose
    /// fields still carry source ids and may be link-resolved. Structural-stub ancestors
    /// that PRE-EXISTED this entry are skipped (their fields were written and resolved by
    /// the predicate that owns them — re-resolving would reinterpret target ids as source
    /// ids); stubs this entry CREATED (e.g. merge-only onto a blank database) do resolve.
    /// </summary>
    private static void CollectEntryTargetPageIds(
        List<SerializedPage> pages,
        Dictionary<Guid, int> pageGuidCache,
        HashSet<int> targetIds,
        HashSet<Guid>? preExistingPageGuids = null)
    {
        foreach (var page in pages)
        {
            var preExistingStub = page.IsStructuralStub
                && preExistingPageGuids is not null
                && preExistingPageGuids.Contains(page.PageUniqueId);
            if (!preExistingStub && pageGuidCache.TryGetValue(page.PageUniqueId, out var targetId))
                targetIds.Add(targetId);
            if (page.Children.Count > 0)
                CollectEntryTargetPageIds(page.Children, pageGuidCache, targetIds, preExistingPageGuids);
        }
    }

    private static void CollectSourcePageIds(List<SerializedPage> pages, HashSet<int> ids)
    {
        foreach (var page in pages)
        {
            if (page.SourcePageId.HasValue)
                ids.Add(page.SourcePageId.Value);
            CollectSourcePageIds(page.Children, ids);
        }
    }

    /// <summary>
    /// Phase 37-05 / TEMPLATE-01: read <c>templates.manifest.yml</c> from the output root
    /// and verify every referenced cshtml / grid-row / item-type file exists on the target
    /// filesystem. Runs before any page writes so operators see upfront whether templates
    /// are in place. No-op when <see cref="_filesRoot"/> is null (unit tests typically
    /// don't provide one) or no manifest is present (older baselines pre-Phase-37-05).
    /// </summary>
    private void ValidateTemplateManifest()
    {
        if (string.IsNullOrEmpty(_filesRoot)) return;

        List<TemplateReference>? refs;
        try
        {
            refs = _templateManifest.Read(_contentRoot);
        }
        catch (Exception ex)
        {
            Log($"WARNING: Could not read template manifest: {ex.Message}");
            return;
        }

        if (refs == null || refs.Count == 0) return;

        Log($"Validating {TemplateAssetManifest.ManifestFileName} ({refs.Count} reference(s))...");
        var missing = _templateManifest.Validate(_filesRoot, refs, _templateEscalator);
        Log($"Template validation: {refs.Count - missing} found, {missing} missing");
    }

    // -------------------------------------------------------------------------
    // Missing-area decision (AREA-04 dry-run parity)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decides what to do about a Content entry whose target area is not on the target,
    /// for BOTH run kinds, in one place.
    ///
    /// <para>
    /// The dry run exists to report what the real run would do. When the two disagreed, the
    /// dry run logged <c>Warning: Area with ID {n} not found. Skipping entry</c> for content
    /// the real run deserialized cleanly, strict mode escalated that warning, and a gate
    /// using the dry run as its go/no-go signal failed on a correct composition.
    /// </para>
    ///
    /// <para>
    /// The parity invariant this encodes: the dry run simulates a create exactly when the
    /// real run performs one, and skips exactly when the real run also cannot create. The
    /// area's serialized property count is the only input that decides it, so the two
    /// answers cannot drift apart.
    /// </para>
    /// </summary>
    internal static MissingAreaAction ResolveMissingAreaAction(bool isDryRun, int areaPropertyCount)
    {
        if (areaPropertyCount <= 0) return MissingAreaAction.Skip;
        return isDryRun ? MissingAreaAction.SimulateCreate : MissingAreaAction.Create;
    }

    // -------------------------------------------------------------------------
    // Entry-level processing (Phase 44 / D-04: ContentEntry-typed)
    // -------------------------------------------------------------------------

    private DeserializeResult DeserializePredicate(ContentEntry entry, SerializedArea area,
        Dictionary<Guid, int>? globalPageGuidCache = null, List<SerializedPage>? allAreaPages = null)
    {
        // Phase 44 / D-04 (BLOCKER 2): read ExcludeFields from the constructor-injected
        // _entry — not from a transient predicate that no longer exists on this path.
        var excludeFieldsSet = _entry.ExcludeFields.Count > 0
            ? new HashSet<string>(_entry.ExcludeFields, StringComparer.OrdinalIgnoreCase)
            : null;

        // Set when the dry run stands in for an area the real run would have created, so the
        // walk below never queries the live tree for an area id that is not there yet.
        var simulatedArea = false;

        var targetArea = Services.Areas.GetArea(entry.AreaId);
        if (targetArea == null)
        {
            // AREA-04 dry-run parity: the dry run has to REPORT what the real run would do,
            // and the real run creates this area. Skipping the entry here instead made the
            // dry run log "Warning: Area with ID {n} not found. Skipping entry" for content
            // a real run deserializes cleanly, and strict mode escalated that warning into
            // an HTTP 400 — so a gate whose dry run is its go/no-go signal failed on a
            // composition that was correct.
            //
            // Simulate instead: announce the create, then FALL THROUGH into the normal walk
            // with targetArea still null. That is safe and is the point — every write below
            // is already behind !_isDryRun, and each page resolves through pageGuidCache,
            // which GetPagesByAreaID returns empty for a missing area, so every page and
            // grid row reports as a [DRY-RUN] CREATE. Exactly the real run's outcome, with
            // zero DB writes.
            //
            // Both answers come from ResolveMissingAreaAction so the two runs cannot drift
            // apart again: an area with no serialized properties is one the real run cannot
            // create either, and there the dry run keeps the warning and the skip. Parity
            // means matching both answers, not only the happy one.
            var missingAreaAction = ResolveMissingAreaAction(_isDryRun, area.Properties.Count);

            if (missingAreaAction == MissingAreaAction.SimulateCreate)
            {
                Log($"[DRY-RUN] CREATE area {entry.AreaId} ('{area.Name}') — would create from YAML data; continuing as if it existed");
                simulatedArea = true;
            }
            // AREA-04: Create the area if it doesn't exist on target
            else if (missingAreaAction == MissingAreaAction.Create)
            {
                Log($"Area with ID {entry.AreaId} not found. Creating from YAML data.");
                try
                {
                    // Exclusion dicts are top-level on SerializerConfiguration. The Replace-side
                    // area-creation path is mode-agnostic w.r.t. the exclusion dict — Merge-mode
                    // fill does not run this code path (Merge reaches WriteSimpleScalarFieldsViaMerge
                    // / etc.) so a top-level read is correct for both modes. Sourced from the
                    // constructor-injected envelope dict.
                    var createAreaExclude = _excludeFieldsByItemType != null && _excludeFieldsByItemType.Count > 0 && !string.IsNullOrEmpty(area.ItemType)
                    ? ExclusionMerger.MergeFieldExclusions(
                        excludeFieldsSet?.ToList() ?? new List<string>(),
                        _excludeFieldsByItemType,
                        area.ItemType)
                    : excludeFieldsSet;
                CreateAreaFromProperties(entry.AreaId, area, createAreaExclude);
                    Services.Areas.ClearCache(); // Critical: per project_dw_area_cache.md
                    targetArea = Services.Areas.GetArea(entry.AreaId);
                    if (targetArea == null)
                    {
                        Log($"ERROR: Area creation succeeded but GetArea still returns null after cache clear. Skipping entry '{entry.EntryId}'.");
                        return new DeserializeResult();
                    }
                    Log($"Area created: ID={entry.AreaId}, Name={area.Name}");
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Failed to create area {entry.AreaId}: {ex.Message}. Skipping entry '{entry.EntryId}'.");
                    return new DeserializeResult();
                }
            }
            else
            {
                Log($"Warning: Area with ID {entry.AreaId} not found. Skipping entry '{entry.EntryId}'.");
                return new DeserializeResult();
            }
        }

        Log($"Deserializing entry '{entry.EntryId}' into area ID={entry.AreaId}");

        // Multi-language: when this area is a language layer, validate its master area and
        // ecom language exist on target before pages are written.
        ValidateLanguageLayerArea(entry.AreaId, area.Properties);

        // Pre-build page GUID cache for the entire area (avoids per-item full table scans).
        // A simulated area holds no pages by construction — asking the live tree for the
        // pages of an area id that does not exist would be a query about nothing, so the
        // cache starts empty and every page below reports as a [DRY-RUN] CREATE.
        IEnumerable<Page> allPages = simulatedArea
            ? Array.Empty<Page>()
            : Services.Pages.GetPagesByAreaID(entry.AreaId);
        var pageGuidCache = allPages
            .Where(p => p.UniqueId != Guid.Empty)
            .ToDictionary(p => p.UniqueId, p => p.ID);

        var ctx = new WriteContext
        {
            TargetAreaId = entry.AreaId,
            ParentPageId = 0,
            PageGuidCache = pageGuidCache,
            ExcludeFields = excludeFieldsSet,
            ExcludeFieldsByItemType = _excludeFieldsByItemType != null && _excludeFieldsByItemType.Count > 0
                ? _excludeFieldsByItemType
                : null
        };

        // Area-level state (properties + area ItemType fields) belongs to the whole-area
        // entry. A partial-path entry (e.g. merge '/Posts' after replace '/') re-writing it
        // would at best merge-skip and at worst clobber the owning entry's already
        // link-resolved values — and the later re-resolution of those fields would
        // re-interpret rewritten TARGET ids as source ids.
        var ownsAreaState = entry.PageId == 0 && (entry.Path == "/" || entry.Path.Length == 0);

        // Write full area properties (AREA-04)
        if (ownsAreaState && area.Properties.Count > 0 && !_isDryRun)
        {
            Log($"Writing {area.Properties.Count} area properties for area ID={entry.AreaId}");
            var areaPropsExclude = ctx.ExcludeFieldsByItemType != null && !string.IsNullOrEmpty(area.ItemType)
                ? ExclusionMerger.MergeFieldExclusions(
                    ctx.ExcludeFields?.ToList() ?? new List<string>(),
                    ctx.ExcludeFieldsByItemType,
                    area.ItemType)
                : ctx.ExcludeFields;
            var excludeAreaColumnsSet = entry.ExcludeAreaColumns.Count > 0
                ? new HashSet<string>(entry.ExcludeAreaColumns, StringComparer.OrdinalIgnoreCase)
                : null;
            WriteAreaProperties(entry.AreaId, area.Properties, areaPropsExclude, excludeAreaColumnsSet);
            Services.Areas.ClearCache();
        }

        // Save area-level ItemType fields (AREA-01)
        if (ownsAreaState && !string.IsNullOrEmpty(area.ItemType) && area.ItemFields.Count > 0 && !_isDryRun)
        {
            var targetAreaItemId = targetArea.ItemId;

            // If the area has no Item row yet, create one (same pattern as GridRow Item creation)
            if (string.IsNullOrEmpty(targetAreaItemId) || Services.Items.GetItem(area.ItemType, targetAreaItemId) == null)
            {
                try
                {
                    var item = new Dynamicweb.Content.Items.Item(area.ItemType);
                    using (var itemContext = new Dynamicweb.Content.Items.ItemContext())
                        item.Save(itemContext);
                    targetAreaItemId = item.Id;
                    targetArea.ItemId = targetAreaItemId;
                    targetArea.ItemType = area.ItemType;
                    Services.Areas.SaveArea(targetArea);
                    Services.Areas.ClearCache();
                    Log($"Created area Item: type={area.ItemType}, id={targetAreaItemId}");
                }
                catch (Exception ex)
                {
                    Log($"WARNING: Could not create area Item: {ex.Message}");
                }
            }
            else if (targetArea.ItemType != area.ItemType)
            {
                // Repair binding for an Area whose Item exists but whose AreaItemType column
                // is blank or stale (e.g., written by a pre-fix deserialize). Without this
                // assignment, the downstream ResolveLinksInArea guard skips link remapping.
                targetArea.ItemType = area.ItemType;
                Services.Areas.SaveArea(targetArea);
                Services.Areas.ClearCache();
                Log($"Repaired area binding: type={area.ItemType}, id={targetAreaItemId}");
            }

            if (!string.IsNullOrEmpty(targetAreaItemId))
            {
                Log($"Applying area ItemType fields: type={area.ItemType}, id={targetAreaItemId}, fields={area.ItemFields.Count}");
                var effectiveExclude = ctx.ExcludeFieldsByItemType != null
                    ? ExclusionMerger.MergeFieldExclusions(
                        ctx.ExcludeFields?.ToList() ?? new List<string>(),
                        ctx.ExcludeFieldsByItemType,
                        area.ItemType)
                    : ctx.ExcludeFields;
                SaveItemFields(area.ItemType, targetAreaItemId, area.ItemFields, effectiveExclude);
            }
        }

        foreach (var page in area.Pages)
        {
            DeserializePageSafe(page, ctx);
        }

        // Contribute this area's pages and GUID cache to the global collections for cross-area link resolution
        if (globalPageGuidCache != null)
        {
            foreach (var kvp in ctx.PageGuidCache)
                globalPageGuidCache.TryAdd(kvp.Key, kvp.Value);
        }
        allAreaPages?.AddRange(area.Pages);

        return new DeserializeResult
        {
            Created = ctx.Created,
            Updated = ctx.Updated,
            Skipped = ctx.Skipped,
            Failed = ctx.Failed,
            Errors = ctx.Errors
        };
    }

    // -------------------------------------------------------------------------
    // Area SQL property write-back
    // -------------------------------------------------------------------------

    /// <summary>
    /// Write area properties back to the [Area] table via SQL UPDATE.
    /// Skips columns in excludeFields to preserve environment-specific values.
    /// Also skips columns not present on the target schema (logs a warning once per column).
    /// Type coercion and schema-drift handling delegate to the shared <see cref="TargetSchemaCache"/>
    /// (Phase 37-02).
    /// </summary>
    private void WriteAreaProperties(int areaId, Dictionary<string, object> properties, IReadOnlySet<string>? excludeFields, IReadOnlySet<string>? excludeAreaColumns = null)
    {
        if (properties.Count == 0) return;

        var targetCols = _schemaCache.GetColumns("Area");

        var cb = new CommandBuilder();
        var first = true;
        foreach (var kvp in properties)
        {
            // Skip excluded fields (per AREA-05) and excluded area columns (per AREA-08)
            if (excludeFields?.Contains(kvp.Key) == true) continue;
            if (excludeAreaColumns?.Contains(kvp.Key) == true) continue;

            // Skip columns that don't exist on the target schema (graceful cross-version handling)
            if (!targetCols.Contains(kvp.Key))
            {
                _schemaCache.LogMissingColumnOnce("Area", kvp.Key, _log);
                continue;
            }

            var coerced = _schemaCache.Coerce("Area", kvp.Key, kvp.Value);
            CommandBuilderValues.AddValue(cb,
                first ? $"UPDATE [Area] SET [{kvp.Key}] = " : $", [{kvp.Key}] = ", coerced);
            first = false;
        }
        // If all properties were excluded, nothing to update
        if (first) return;

        cb.Add(" WHERE [AreaID] = {0}", areaId);
        // Phase 38 A.2 (D-38-05): routed through ISqlExecutor seam for testability.
        _sqlExecutor.ExecuteNonQuery(cb);
    }

    /// <summary>
    /// Create a new area row via SQL INSERT using serialized properties.
    /// Called when the target area does not exist (AREA-04).
    /// </summary>
    private void CreateAreaFromProperties(int areaId, SerializedArea area, IReadOnlySet<string>? excludeFields)
    {
        var columns = new List<string> { "[AreaID]", "[AreaName]", "[AreaSort]", "[AreaUniqueId]" };
        var values = new List<object> { areaId, area.Name, area.SortOrder, area.AreaId };

        var targetCols = _schemaCache.GetColumns("Area");
        foreach (var kvp in area.Properties)
        {
            if (excludeFields?.Contains(kvp.Key) == true) continue;
            if (!targetCols.Contains(kvp.Key))
            {
                _schemaCache.LogMissingColumnOnce("Area", kvp.Key, _log);
                continue;
            }
            columns.Add($"[{kvp.Key}]");
            values.Add(_schemaCache.Coerce("Area", kvp.Key, kvp.Value) ?? DBNull.Value);
        }

        // 2026-04-20: wrap in SET IDENTITY_INSERT so explicit AreaID writes succeed against
        // a fresh target where Area.AreaId is an identity column. Keeping the areaId stable
        // across env is required for predicate.areaId references to work.
        //
        // Phase 38 WR-02: Wrap the INSERT in TRY/CATCH so SET IDENTITY_INSERT [Area] OFF is
        // always emitted even when the INSERT throws (FK violation, duplicate AreaUniqueId,
        // etc.). Without the CATCH, a failed INSERT would leave the connection's session
        // state with IDENTITY_INSERT still ON for [Area], and subsequent work on the same
        // pooled connection could fail unexpectedly. THROW re-raises the original exception
        // so the caller still sees the failure. The outer OFF is a belt-and-braces terminator
        // for the success path (TRY completes without entering CATCH).
        var cb = new CommandBuilder();
        cb.Add("SET IDENTITY_INSERT [Area] ON; ");
        cb.Add("BEGIN TRY ");
        cb.Add($"INSERT INTO [Area] ({string.Join(", ", columns)}) VALUES (");
        for (int i = 0; i < values.Count; i++)
            CommandBuilderValues.AddValue(cb, i > 0 ? ", " : "", values[i]);
        cb.Add("); ");
        cb.Add("END TRY BEGIN CATCH ");
        cb.Add("SET IDENTITY_INSERT [Area] OFF; ");
        cb.Add("THROW; ");
        cb.Add("END CATCH; ");
        cb.Add("SET IDENTITY_INSERT [Area] OFF;");
        // Phase 38 A.2 (D-38-05): routed through ISqlExecutor seam so the
        // SET IDENTITY_INSERT [Area] ON/OFF wrapping can be asserted by tests.
        _sqlExecutor.ExecuteNonQuery(cb);
    }

    // -------------------------------------------------------------------------
    // Phase 38 A.2 (D-38-05): internal test hooks for Area SQL write paths.
    // Access is gated by the <InternalsVisibleTo Include="Truvio.Commerce.Serializer.Tests" />
    // entry in Truvio.Commerce.Serializer.csproj. Production code never calls these.
    // The forwarder pattern avoids making the private methods public or using
    // reflection — reviewable and deterministic (checker warning W2 resolution).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Test-only forwarder to the private <c>CreateAreaFromProperties</c>.
    /// Drives the Area INSERT path so the SET IDENTITY_INSERT [Area] ON/INSERT/OFF
    /// ordering can be asserted via the injected <see cref="ISqlExecutor"/>.
    /// </summary>
    internal void InvokeCreateAreaFromPropertiesForTest(int areaId, SerializedArea area, IReadOnlySet<string>? excludeFields)
        => CreateAreaFromProperties(areaId, area, excludeFields);

    /// <summary>
    /// Test-only forwarder to the private <c>WriteAreaProperties</c>. Drives the
    /// Area UPDATE path to confirm it does NOT emit IDENTITY_INSERT wrappers.
    /// </summary>
    internal void InvokeUpdateAreaFromPropertiesForTest(int areaId, Dictionary<string, object> properties, IReadOnlySet<string>? excludeFields, IReadOnlySet<string>? excludeAreaColumns = null)
        => WriteAreaProperties(areaId, properties, excludeFields, excludeAreaColumns);

    // -------------------------------------------------------------------------
    // Page deserialization
    // -------------------------------------------------------------------------

    private void DeserializePageSafe(SerializedPage dto, WriteContext ctx)
    {
        // Cascade skip: if any ancestor failed, skip this page and all its children
        if (ctx.FailedParentGuids.Contains(dto.PageUniqueId))
        {
            ctx.Skipped++;
            Log($"SKIPPED page {dto.PageUniqueId} ('{dto.MenuText}') — parent failed");
            return;
        }

        // Check if any ancestor of this page is in the failed set by traversal context
        // (FailedParentGuids accumulates failed pages; children have their parent GUID tracked separately)
        // The cascade skip check above handles direct parent matching; the broader check is handled
        // by not recursing into children when a parent throws (implicit via exception handling below)

        try
        {
            int resolvedId = DeserializePage(dto, ctx);

            // In dry-run mode, don't attempt grid rows/children with synthetic -1 ID
            if (resolvedId < 0 && _isDryRun)
            {
                // Still log children would be processed
                foreach (var child in dto.Children)
                {
                    Log($"[DRY-RUN] SKIP child {child.PageUniqueId} ('{child.MenuText}') — parent is CREATE in dry-run");
                    ctx.Skipped++;
                }
                return;
            }

            if (resolvedId < 0)
                return;

            // Process grid rows for this page
            var gridRowCache = Services.Grids.GetGridRowsByPageId(resolvedId)
                .Where(gr => gr.UniqueId != Guid.Empty)
                .ToDictionary(gr => gr.UniqueId, gr => gr.ID);

            foreach (var row in dto.GridRows)
            {
                DeserializeGridRowSafe(row, resolvedId, gridRowCache, ctx);
            }

            // Recurse children with this page as parent
            var savedParentPageId = ctx.ParentPageId;
            ctx.ParentPageId = resolvedId;
            foreach (var child in dto.Children)
            {
                DeserializePageSafe(child, ctx);
            }
            ctx.ParentPageId = savedParentPageId;
        }
        catch (Exception ex)
        {
            ctx.Failed++;
            var msg = $"ERROR deserializing page {dto.PageUniqueId} ('{dto.MenuText}'): {ex.Message}";
            ctx.Errors.Add(msg);
            Log(msg);

            // Mark this page as failed so all descendant pages are cascade-skipped
            ctx.FailedParentGuids.Add(dto.PageUniqueId);
            Log($"  SKIPPED children of {dto.PageUniqueId} due to parent failure");
        }
    }

    /// <summary>
    /// Writes a single page to DW (insert or update). Returns the resolved numeric page ID,
    /// or -1 in dry-run CREATE mode (no DW ID assigned).
    /// </summary>
    private int DeserializePage(SerializedPage dto, WriteContext ctx)
    {
        // Phase 37-05: inline template validation removed — the manifest pre-flight
        // (ValidateTemplateManifest) now covers all layout / item-type / grid-row refs.

        if (!ctx.PageGuidCache.TryGetValue(dto.PageUniqueId, out var existingId))
        {
            // INSERT path — GUID not found in target area
            if (_isDryRun)
            {
                Log($"[DRY-RUN] CREATE page {dto.PageUniqueId} ('{dto.MenuText}')");
                foreach (var f in dto.Fields)
                    Log($"  set {f.Key} = '{f.Value}'");
                if (dto.Permissions.Count > 0)
                    Log($"[DRY-RUN] Would apply {dto.Permissions.Count} permission(s) to page {dto.PageUniqueId}");
                ctx.Created++;
                return -1;
            }

            var page = new Page();
            page.UniqueId = dto.PageUniqueId;
            page.AreaId = ctx.TargetAreaId;
            page.ParentPageId = ctx.ParentPageId;
            page.MenuText = dto.MenuText;
            page.UrlName = dto.UrlName;
            page.Active = dto.IsActive;
            page.Sort = dto.SortOrder;
            page.ItemType = dto.ItemType ?? string.Empty;
            page.LayoutTemplate = dto.Layout ?? string.Empty;
            page.LayoutApplyToSubPages = dto.LayoutApplyToSubPages;
            page.IsFolder = dto.IsFolder;
            page.IsTemplate = dto.IsTemplate;
            page.TreeSection = dto.TreeSection ?? string.Empty;
            ApplyPageProperties(page, dto);
            // Do NOT set page.ID — leave 0 for insert path (Pitfall 4)

            var saved = Services.Pages.SavePage(page, skipLanguages: true);
            ctx.PageGuidCache[dto.PageUniqueId] = saved.ID;

            // Apply ItemType fields via ItemService (page.Item[key] = value does not persist)
            var refetched = Services.Pages.GetPage(saved.ID);
            if (refetched != null)
            {
                var pageExclude = ctx.ExcludeFieldsByItemType != null
                    ? ExclusionMerger.MergeFieldExclusions(
                        ctx.ExcludeFields?.ToList() ?? new List<string>(),
                        ctx.ExcludeFieldsByItemType,
                        dto.ItemType)
                    : ctx.ExcludeFields;
                SaveItemFields(refetched.ItemType, refetched.ItemId, dto.Fields, pageExclude);

                // Re-apply LayoutTemplate if DW overwrote it during HandleItemStructure
                // (DW sets it to the ItemType's default template on new pages)
                if (!string.IsNullOrEmpty(dto.Layout) && refetched.LayoutTemplate != dto.Layout)
                {
                    Log($"  Re-applying LayoutTemplate: '{refetched.LayoutTemplate}' -> '{dto.Layout}'");
                    refetched.LayoutTemplate = dto.Layout;
                    Services.Pages.SavePage(refetched, skipLanguages: true);
                }

                // Apply PropertyItem fields (e.g. Icon, SubmenuType)
                SavePropertyItemFields(refetched, dto.PropertyFields, pageExclude);

                ResyncMenuTextAfterItemWrite(saved.ID, dto);
            }

            ctx.Created++;
            Log($"CREATED page {dto.PageUniqueId} -> ID={saved.ID}");
            _permissionMapper.ApplyPermissions(saved.ID, "Page", dto.Permissions);
            return saved.ID;
        }
        else
        {
            // UPDATE path — GUID matched an existing page
            // Load existing page from DW so it has an internally-set ID (DW Entity<int>.ID has no public setter)
            var existingPage = Services.Pages.GetPage(existingId);
            if (existingPage == null)
            {
                throw new InvalidOperationException(
                    $"Could not load existing page with ID {existingId} for update.");
            }

            // Merge mode — field-level fill.
            // Supersedes the row-level skip previously enforced here (Phase 37-01 D-06).
            // The page document's own header mode decides; the pass strategy is the fallback.
            if (StrategyFor(dto.Ownership) == ConflictStrategy.DestinationWins)
            {
                var mergeExclude = ctx.ExcludeFieldsByItemType != null
                    ? ExclusionMerger.MergeFieldExclusions(
                        ctx.ExcludeFields?.ToList() ?? new List<string>(),
                        ctx.ExcludeFieldsByItemType,
                        dto.ItemType)
                    : ctx.ExcludeFields;

                if (_isDryRun)
                {
                    LogMergeFillDryRun(dto, existingPage, mergeExclude, ctx);
                    return existingId;
                }

                // Identity — always source-wins per D-05.
                existingPage.UniqueId = dto.PageUniqueId;
                existingPage.AreaId = ctx.TargetAreaId;
                existingPage.ParentPageId = ctx.ParentPageId;

                int filled = 0;
                int left = 0;

                filled += MergePageScalars(existingPage, dto, ref left);
                filled += ApplyPagePropertiesWithMerge(existingPage, dto, ref left);

                Services.Pages.SavePage(existingPage, skipLanguages: true);

                // D-02 / D-03: field-level merge for ItemFields + PropertyItem fields.
                filled += MergeItemFields(existingPage.ItemType, existingPage.ItemId, dto.Fields, mergeExclude, ref left);
                filled += MergePropertyItemFields(existingPage, dto.PropertyFields, mergeExclude, ref left);

                // Permissions NOT applied on Merge UPDATE.
                // (Intentionally absent: no _permissionMapper.ApplyPermissions call here.)

                // D-11: new log format + counter repurpose.
                if (filled == 0) ctx.Skipped++;
                else ctx.Updated++;
                Log($"Merge-fill: page {dto.PageUniqueId} (ID={existingId}) - {filled} filled, {left} left");

                // D-07: child recursion (gridrows -> columns -> paragraphs) continues below and
                // inherits _conflictStrategy automatically.
                return existingId;
            }

            if (_isDryRun)
            {
                LogDryRunPageUpdate(dto, existingPage, ctx);
                return existingId;
            }

            // Apply scalar properties (source-wins)
            existingPage.UniqueId = dto.PageUniqueId;
            existingPage.AreaId = ctx.TargetAreaId;
            existingPage.ParentPageId = ctx.ParentPageId;
            existingPage.MenuText = dto.MenuText;
            existingPage.UrlName = dto.UrlName;
            existingPage.Active = dto.IsActive;
            existingPage.Sort = dto.SortOrder;
            existingPage.ItemType = dto.ItemType ?? string.Empty;
            existingPage.LayoutTemplate = dto.Layout ?? string.Empty;
            existingPage.LayoutApplyToSubPages = dto.LayoutApplyToSubPages;
            existingPage.IsFolder = dto.IsFolder;
            existingPage.IsTemplate = dto.IsTemplate;
            existingPage.TreeSection = dto.TreeSection ?? string.Empty;
            ApplyPageProperties(existingPage, dto);

            Services.Pages.SavePage(existingPage, skipLanguages: true);

            // Apply ItemType fields via ItemService (source-wins)
            var updatePageExclude = ctx.ExcludeFieldsByItemType != null
                ? ExclusionMerger.MergeFieldExclusions(
                    ctx.ExcludeFields?.ToList() ?? new List<string>(),
                    ctx.ExcludeFieldsByItemType,
                    dto.ItemType)
                : ctx.ExcludeFields;
            SaveItemFields(existingPage.ItemType, existingPage.ItemId, dto.Fields, updatePageExclude);

            // Apply PropertyItem fields (e.g. Icon, SubmenuType)
            SavePropertyItemFields(existingPage, dto.PropertyFields, updatePageExclude);

            ResyncMenuTextAfterItemWrite(existingId, dto);

            ctx.Updated++;
            Log($"UPDATED page {dto.PageUniqueId} (ID={existingId})");
            _permissionMapper.ApplyPermissions(existingId, "Page", dto.Permissions);
            return existingId;
        }
    }

    // -------------------------------------------------------------------------
    // Grid row deserialization
    // -------------------------------------------------------------------------

    private void DeserializeGridRowSafe(
        SerializedGridRow dto,
        int pageId,
        Dictionary<Guid, int> gridRowCache,
        WriteContext ctx)
    {
        try
        {
            int resolvedGridRowId = DeserializeGridRow(dto, pageId, gridRowCache, ctx);

            if (resolvedGridRowId < 0 && _isDryRun)
                return;

            if (resolvedGridRowId < 0)
                return;

            // Build paragraph GUID cache for this page
            var paragraphCache = Services.Paragraphs.GetParagraphsByPageId(pageId)
                .Where(p => p.UniqueId != Guid.Empty)
                .ToDictionary(p => p.UniqueId, p => p.ID);

            foreach (var column in dto.Columns)
            {
                foreach (var para in column.Paragraphs)
                {
                    DeserializeParagraphSafe(para, pageId, resolvedGridRowId, column.Id, paragraphCache, ctx);
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Failed++;
            var msg = $"ERROR deserializing grid row {dto.Id} on page {pageId}: {ex.Message}";
            ctx.Errors.Add(msg);
            Log(msg);
        }
    }

    private int DeserializeGridRow(
        SerializedGridRow dto,
        int pageId,
        Dictionary<Guid, int> gridRowCache,
        WriteContext ctx)
    {
        // Phase 37-05: inline validation removed — manifest pre-flight covers these refs.

        if (!gridRowCache.TryGetValue(dto.Id, out var existingGridRowId))
        {
            // INSERT path
            if (_isDryRun)
            {
                Log($"[DRY-RUN] CREATE grid row {dto.Id} (sort={dto.SortOrder}) on page {pageId}");
                if (dto.Permissions.Count > 0)
                    Log($"[DRY-RUN] Would apply {dto.Permissions.Count} permission(s) to grid row {dto.Id}");
                ctx.Created++;
                return -1;
            }

            var row = new GridRow(pageId);
            row.UniqueId = dto.Id;
            row.Sort = dto.SortOrder;
            if (!string.IsNullOrEmpty(dto.DefinitionId))
                row.DefinitionId = dto.DefinitionId;
            if (!string.IsNullOrEmpty(dto.ItemType))
                row.ItemType = dto.ItemType;
            ApplyGridRowVisualProperties(row, dto);
            // Do NOT set row.ID (insert path)

            Services.Grids.SaveGridRow(row);

            // Re-query to get DW-assigned numeric ID (Pitfall 1: SaveGridRow returns bool, not GridRow)
            var saved = Services.Grids.GetGridRowsByPageId(pageId)
                .FirstOrDefault(gr => gr.UniqueId == dto.Id);

            if (saved == null)
                throw new InvalidOperationException($"Could not find inserted grid row with GUID {dto.Id}");

            // GridRow.SaveGridRow does NOT auto-create Items (unlike SaveParagraph).
            // Create Item manually and link it to the grid row.
            if (!string.IsNullOrEmpty(dto.ItemType) && string.IsNullOrEmpty(saved.ItemId))
            {
                try
                {
                    var item = new Dynamicweb.Content.Items.Item(dto.ItemType);
                    using (var itemContext = new Dynamicweb.Content.Items.ItemContext())
                        item.Save(itemContext);
                    Log($"  GridRow Item created: type={dto.ItemType}, id={item.Id}");
                    saved.ItemId = item.Id;
                    Services.Grids.SaveGridRow(saved);
                    var gridRowExclude = ctx.ExcludeFieldsByItemType != null
                        ? ExclusionMerger.MergeFieldExclusions(
                            ctx.ExcludeFields?.ToList() ?? new List<string>(),
                            ctx.ExcludeFieldsByItemType,
                            dto.ItemType)
                        : ctx.ExcludeFields;
                    SaveItemFields(dto.ItemType, item.Id, dto.Fields, gridRowExclude);
                }
                catch (Exception ex)
                {
                    Log($"  WARNING: GridRow Item creation failed: {ex.Message}");
                }
            }
            else if (!string.IsNullOrEmpty(saved.ItemId))
            {
                var gridRowExclude2 = ctx.ExcludeFieldsByItemType != null
                    ? ExclusionMerger.MergeFieldExclusions(
                        ctx.ExcludeFields?.ToList() ?? new List<string>(),
                        ctx.ExcludeFieldsByItemType,
                        dto.ItemType)
                    : ctx.ExcludeFields;
                SaveItemFields(dto.ItemType, saved.ItemId, dto.Fields, gridRowExclude2);
            }

            var newGridRowId = saved.ID;
            ctx.Created++;
            Log($"CREATED grid row {dto.Id} -> ID={newGridRowId} on page {pageId}");
            _permissionMapper.ApplyPermissions(newGridRowId, "GridRow", dto.Permissions);
            return newGridRowId;
        }
        else
        {
            // UPDATE path
            if (_isDryRun)
            {
                // Fetch existing to compare sort order
                var existingRows = Services.Grids.GetGridRowsByPageId(pageId);
                var existingRow = existingRows.FirstOrDefault(gr => gr.ID == existingGridRowId);
                if (existingRow != null && existingRow.Sort != dto.SortOrder)
                {
                    Log($"[DRY-RUN] UPDATE grid row {dto.Id} (ID={existingGridRowId}): Sort: {existingRow.Sort} -> {dto.SortOrder}");
                    ctx.Updated++;
                }
                else
                {
                    Log($"[DRY-RUN] SKIP grid row {dto.Id} (ID={existingGridRowId}) (unchanged)");
                    ctx.Skipped++;
                }
                if (StrategyFor(dto.Ownership) != ConflictStrategy.DestinationWins && dto.Permissions.Count > 0)
                    Log($"[DRY-RUN] Would apply {dto.Permissions.Count} permission(s) to grid row {dto.Id}");
                return existingGridRowId;
            }

            // Load existing grid row from DW so it has internally-set ID (DW Entity<int>.ID has no public setter)
            var existingRow2 = Services.Grids.GetGridRowsByPageId(pageId)
                .FirstOrDefault(gr => gr.ID == existingGridRowId);
            if (existingRow2 == null)
            {
                throw new InvalidOperationException(
                    $"Could not load existing grid row with ID {existingGridRowId} for update.");
            }

            existingRow2.UniqueId = dto.Id;
            existingRow2.Sort = dto.SortOrder;
            if (!string.IsNullOrEmpty(dto.DefinitionId))
                existingRow2.DefinitionId = dto.DefinitionId;
            if (!string.IsNullOrEmpty(dto.ItemType))
                existingRow2.ItemType = dto.ItemType;
            ApplyGridRowVisualProperties(existingRow2, dto);

            Services.Grids.SaveGridRow(existingRow2);

            // Apply ItemType fields via ItemService
            if (!string.IsNullOrEmpty(existingRow2.ItemId))
            {
                var gridRowUpdateExclude = ctx.ExcludeFieldsByItemType != null
                    ? ExclusionMerger.MergeFieldExclusions(
                        ctx.ExcludeFields?.ToList() ?? new List<string>(),
                        ctx.ExcludeFieldsByItemType,
                        dto.ItemType)
                    : ctx.ExcludeFields;
                SaveItemFields(dto.ItemType, existingRow2.ItemId, dto.Fields, gridRowUpdateExclude);
            }

            ctx.Updated++;
            Log($"UPDATED grid row {dto.Id} (ID={existingGridRowId})");
            // Permissions NOT applied on Merge UPDATE.
            // (Intentionally absent on the DestinationWins path: no ApplyPermissions call there.)
            if (StrategyFor(dto.Ownership) != ConflictStrategy.DestinationWins)
                _permissionMapper.ApplyPermissions(existingGridRowId, "GridRow", dto.Permissions);
            return existingGridRowId;
        }
    }

    // -------------------------------------------------------------------------
    // Paragraph deserialization
    // -------------------------------------------------------------------------

    private void DeserializeParagraphSafe(
        SerializedParagraph dto,
        int pageId,
        int gridRowId,
        int columnId,
        Dictionary<Guid, int> paragraphCache,
        WriteContext ctx)
    {
        try
        {
            DeserializeParagraph(dto, pageId, gridRowId, columnId, paragraphCache, ctx);
        }
        catch (Exception ex)
        {
            ctx.Failed++;
            var msg = $"ERROR deserializing paragraph {dto.ParagraphUniqueId} on page {pageId}: {ex.Message}";
            ctx.Errors.Add(msg);
            Log(msg);
        }
    }

    private void DeserializeParagraph(
        SerializedParagraph dto,
        int pageId,
        int gridRowId,
        int columnId,
        Dictionary<Guid, int> paragraphCache,
        WriteContext ctx)
    {
        // Phase 37-05: inline validation removed — manifest pre-flight covers item types.

        if (!paragraphCache.TryGetValue(dto.ParagraphUniqueId, out var existingParagraphId))
        {
            // INSERT path
            if (_isDryRun)
            {
                Log($"[DRY-RUN] CREATE paragraph {dto.ParagraphUniqueId} (sort={dto.SortOrder}, type={dto.ItemType}) on page {pageId}");
                foreach (var f in dto.Fields)
                    Log($"  set {f.Key} = '{f.Value}'");
                if (dto.Permissions.Count > 0)
                    Log($"[DRY-RUN] Would apply {dto.Permissions.Count} permission(s) to paragraph {dto.ParagraphUniqueId}");
                ctx.Created++;
                return;
            }

            var para = new Paragraph();
            para.UniqueId = dto.ParagraphUniqueId;
            para.PageID = pageId;
            para.GridRowId = gridRowId;
            para.GridRowColumn = columnId;
            para.Sort = dto.SortOrder;
            para.Header = dto.Header;
            para.Template = dto.Template;
            para.ColorSchemeId = dto.ColorSchemeId;
            para.ItemType = dto.ItemType;
            para.ModuleSystemName = dto.ModuleSystemName ?? string.Empty;
            para.ModuleSettings = XmlFormatter.Compact(dto.ModuleSettings) ?? string.Empty;
            // Do NOT set para.ID (insert path)

            Services.Paragraphs.SaveParagraph(para);

            // Re-query to get assigned ID
            var saved = Services.Paragraphs.GetParagraphsByPageId(pageId)
                .FirstOrDefault(p => p.UniqueId == dto.ParagraphUniqueId);

            // Apply ItemType fields via ItemService using paragraph's ItemId (not paragraph ID)
            if (saved != null)
            {
                var paraExclude = ctx.ExcludeFieldsByItemType != null
                    ? ExclusionMerger.MergeFieldExclusions(
                        ctx.ExcludeFields?.ToList() ?? new List<string>(),
                        ctx.ExcludeFieldsByItemType,
                        dto.ItemType)
                    : ctx.ExcludeFields;
                SaveItemFields(dto.ItemType, saved.ItemId, dto.Fields, paraExclude);

                // Re-apply fields that DW may overwrite during HandleItemStructure:
                // - Header: DW sets it to Item's title (template default)
                // - ModuleSystemName/ModuleSettings: may not persist on new paragraphs
                bool needsResave = false;
                if (saved.Header != (dto.Header ?? string.Empty))
                {
                    saved.Header = dto.Header ?? string.Empty;
                    needsResave = true;
                }
                if (!string.IsNullOrEmpty(dto.ModuleSystemName) && saved.ModuleSystemName != dto.ModuleSystemName)
                {
                    saved.ModuleSystemName = dto.ModuleSystemName;
                    saved.ModuleSettings = XmlFormatter.Compact(dto.ModuleSettings) ?? string.Empty;
                    needsResave = true;
                }
                if (!string.IsNullOrEmpty(dto.Template) && saved.Template != dto.Template)
                {
                    saved.Template = dto.Template;
                    needsResave = true;
                }
                if (!string.IsNullOrEmpty(dto.ColorSchemeId) && saved.ColorSchemeId != dto.ColorSchemeId)
                {
                    saved.ColorSchemeId = dto.ColorSchemeId;
                    needsResave = true;
                }
                if (needsResave)
                    Services.Paragraphs.SaveParagraph(saved);

                _permissionMapper.ApplyPermissions(saved.ID, "Paragraph", dto.Permissions);
            }
            else if (dto.Permissions.Count > 0)
            {
                Log($"WARNING: could not re-query inserted paragraph {dto.ParagraphUniqueId} — permissions not applied");
            }

            ctx.Created++;
            Log($"CREATED paragraph {dto.ParagraphUniqueId} on page {pageId}");
        }
        else
        {
            // UPDATE path
            if (_isDryRun)
            {
                var existingParagraphs = Services.Paragraphs.GetParagraphsByPageId(pageId);
                var existing = existingParagraphs.FirstOrDefault(p => p.ID == existingParagraphId);
                if (existing != null)
                    LogDryRunParagraphUpdate(dto, existing, ctx);
                return;
            }

            // Load existing paragraph for update
            var existingForUpdate = Services.Paragraphs.GetParagraphsByPageId(pageId)
                .FirstOrDefault(p => p.ID == existingParagraphId);

            if (existingForUpdate == null)
            {
                throw new InvalidOperationException(
                    $"Could not load existing paragraph with ID {existingParagraphId} for update.");
            }

            existingForUpdate.UniqueId = dto.ParagraphUniqueId;
            existingForUpdate.GridRowId = gridRowId;
            existingForUpdate.GridRowColumn = columnId;
            existingForUpdate.Sort = dto.SortOrder;
            existingForUpdate.Header = dto.Header;
            existingForUpdate.Template = dto.Template;
            existingForUpdate.ColorSchemeId = dto.ColorSchemeId;
            existingForUpdate.ItemType = dto.ItemType;
            existingForUpdate.ModuleSystemName = dto.ModuleSystemName ?? string.Empty;
            existingForUpdate.ModuleSettings = XmlFormatter.CompactWithMerge(dto.ModuleSettings, existingForUpdate.ModuleSettings) ?? string.Empty;

            Services.Paragraphs.SaveParagraph(existingForUpdate);

            // Apply ItemType fields via ItemService (source-wins)
            var paraUpdateExclude = ctx.ExcludeFieldsByItemType != null
                ? ExclusionMerger.MergeFieldExclusions(
                    ctx.ExcludeFields?.ToList() ?? new List<string>(),
                    ctx.ExcludeFieldsByItemType,
                    dto.ItemType)
                : ctx.ExcludeFields;
            SaveItemFields(existingForUpdate.ItemType, existingForUpdate.ItemId, dto.Fields, paraUpdateExclude);
            // Permissions NOT applied on Merge UPDATE.
            // (Intentionally absent on the DestinationWins path: no ApplyPermissions call there.)
            if (StrategyFor(dto.Ownership) != ConflictStrategy.DestinationWins)
                _permissionMapper.ApplyPermissions(existingParagraphId, "Paragraph", dto.Permissions);
            ctx.Updated++;
            Log($"UPDATED paragraph {dto.ParagraphUniqueId} (ID={existingParagraphId})");
        }
    }

    // -------------------------------------------------------------------------
    // Page PropertyItem persistence (Icon, SubmenuType, etc.)
    // -------------------------------------------------------------------------

    /// <summary>
    /// DW's SavePage forces MenuText = item Title for non-template pages, reading the item
    /// AS IT IS AT SAVE TIME. Pages save BEFORE their item fields are written, so a Title
    /// change in the YAML updates the item but leaves the page's MenuText at the OLD Title
    /// (found live: zip roundtrip editing the Posts title — item updated, tree label stale).
    /// Re-save once after the item-field write when the on-target MenuText drifted from the
    /// DTO; DW re-derives MenuText from the now-updated item Title during that save.
    /// </summary>
    private void ResyncMenuTextAfterItemWrite(int pageId, SerializedPage dto)
    {
        if (_isDryRun)
            return;
        try
        {
            var page = Services.Pages.GetPage(pageId);
            if (page is null || string.Equals(page.MenuText, dto.MenuText, StringComparison.Ordinal))
                return;
            page.MenuText = dto.MenuText;
            Services.Pages.SavePage(page, skipLanguages: true);
            Log($"  Re-synced MenuText after item-field write: '{dto.MenuText}' (page ID={pageId})");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Could not re-sync MenuText for page {pageId}: {ex.Message}");
        }
    }

    private void SavePropertyItemFields(Page page, Dictionary<string, object> propertyFields, IReadOnlySet<string>? excludeFields = null)
    {
        if (propertyFields.Count == 0)
            return;

        if (string.IsNullOrEmpty(page.PropertyItemId))
        {
            Log($"  Page {page.UniqueId} has no PropertyItemId — cannot write property fields");
            return;
        }

        var propItem = page.PropertyItem;
        if (propItem == null)
        {
            Log($"  WARNING: Could not load PropertyItem for page {page.UniqueId}");
            return;
        }

        var contentFields = propertyFields
            .Where(kvp => !ItemSystemFields.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        // Source-wins: null out property fields not present in serialized data
        foreach (var fieldName in propItem.Names)
        {
            if (!ItemSystemFields.Contains(fieldName) && !contentFields.ContainsKey(fieldName))
            {
                // Skip guard: do NOT null out fields that were intentionally excluded from serialization (FILT-03)
                if (excludeFields != null && excludeFields.Contains(fieldName))
                    continue;
                contentFields[fieldName] = null;
            }
        }

        if (contentFields.Count == 0)
            return;

        // Engine issue #6: ButtonData binds only as an object — same reshape as SaveItemFields.
        ButtonDataFieldLookup.Apply(propItem.SystemName, contentFields, _log);

        propItem.DeserializeFrom(contentFields);
        using (var propItemContext = new Dynamicweb.Content.Items.ItemContext())
            propItem.Save(propItemContext);
    }

    // -------------------------------------------------------------------------
    // GridRow visual property helpers
    // -------------------------------------------------------------------------

    private static void ApplyGridRowVisualProperties(GridRow row, SerializedGridRow dto)
    {
        if (!string.IsNullOrEmpty(dto.Container))
            row.Container = dto.Container;
        row.ContainerWidth = dto.ContainerWidth;
        row.BackgroundImage = dto.BackgroundImage ?? string.Empty;
        row.ColorSchemeId = dto.ColorSchemeId ?? string.Empty;
        row.TopSpacing = dto.TopSpacing;
        row.BottomSpacing = dto.BottomSpacing;
        row.GapX = dto.GapX;
        row.GapY = dto.GapY;
        row.MobileLayout = dto.MobileLayout ?? string.Empty;
        if (!string.IsNullOrEmpty(dto.VerticalAlignment) &&
            Enum.TryParse<Dynamicweb.Content.Styles.VerticalAlignment>(dto.VerticalAlignment, true, out var va))
            row.VerticalAlignment = va;
        row.FlexibleColumns = dto.FlexibleColumns ?? string.Empty;
    }

    // -------------------------------------------------------------------------
    // Item field persistence via ItemService
    // -------------------------------------------------------------------------

    private static readonly HashSet<string> ItemSystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "ItemInstanceType", "Sort", "GlobalRecordPageGuid", "MasterParagraphGuid"
    };

    /// <summary>
    /// Saves Item fields using ItemService.GetItem + DeserializeFrom + Save.
    /// The paragraph.Item[key] = value approach does not persist to the ItemType table.
    /// Implements source-wins: fields present in the item type definition but absent
    /// from the serialized YAML are explicitly set to null so stale target data is cleared.
    /// </summary>
    private void SaveItemFields(string? itemType, string itemId, Dictionary<string, object> fields, IReadOnlySet<string>? excludeFields = null)
    {
        if (string.IsNullOrEmpty(itemType))
            return;

        var itemEntry = Services.Items.GetItem(itemType, itemId);
        if (itemEntry == null)
        {
            Log($"WARNING: Could not load ItemEntry for type={itemType}, id={itemId}");
            return;
        }

        // Authored payload fields (present in the YAML), minus system fields. Captured before
        // the null-out pass so the derive-on-save repair (below) compares only against values
        // the payload actually asserted.
        var authoredFields = fields
            .Where(kvp => !ItemSystemFields.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        var contentFields = new Dictionary<string, object?>(authoredFields);

        // Source-wins: null out item fields not present in the serialized data.
        // Without this, stale target values (e.g. invalid button data) survive sync.
        foreach (var fieldName in itemEntry.Names)
        {
            if (!ItemSystemFields.Contains(fieldName) && !contentFields.ContainsKey(fieldName))
            {
                // Skip guard: do NOT null out fields that were intentionally excluded from serialization (FILT-03)
                if (excludeFields != null && excludeFields.Contains(fieldName))
                    continue;
                contentFields[fieldName] = null;
            }
        }

        if (contentFields.Count == 0)
            return;

        // Engine issue #6: ButtonData binds only as an object. Reshape the round-tripped JSON
        // string (and the null a source-wins clear produces) BEFORE the write, or the save
        // silently no-ops and the stale target button survives.
        var buttonDataFields = ButtonDataFieldLookup.For(itemType, _log);
        ButtonDataFieldLookup.Apply(itemType, contentFields, _log);

        itemEntry.DeserializeFrom(contentFields);
        using (var itemSaveContext = new Dynamicweb.Content.Items.ItemContext())
            itemEntry.Save(itemSaveContext);

        // Derive-on-save repair (LRN-hosted-publish-05): the platform recomputes some fields on
        // save (e.g. Swift LogoWidth from the image's intrinsic size), overwriting the authored
        // value in the same save. Re-read and re-write any authored non-empty field whose
        // persisted value diverged. Skipped on dry-run (no save happened).
        //
        // ButtonData fields are excluded: the repair compares string projections, and a
        // structured value never projects back to its authored JSON string, so every run would
        // "repair" it — re-writing the string shape that issue #6 exists to avoid.
        if (!_isDryRun)
        {
            var repairCandidates = buttonDataFields.Count == 0
                ? authoredFields
                : authoredFields
                    .Where(kvp => !buttonDataFields.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            RepairDerivedItemFields(itemType, itemId, repairCandidates);
        }
    }


    /// <summary>
    /// Post-write verify/repair pass for item/paragraph content fields (LRN-hosted-publish-05).
    /// Re-reads the just-saved item and, for every authored non-empty field whose persisted
    /// value differs from the authored value, re-writes the authored value in a second save.
    /// The decision logic is in <see cref="DerivedFieldRepair.Compute"/>; this method supplies
    /// the DB read + second save. No-op when nothing diverged.
    /// </summary>
    private void RepairDerivedItemFields(string itemType, string itemId, IReadOnlyDictionary<string, object?> authoredFields)
    {
        if (authoredFields.Count == 0)
            return;

        var saved = Services.Items.GetItem(itemType, itemId);
        if (saved == null)
            return;

        var savedNames = new HashSet<string>(saved.Names);
        var persisted = new Dictionary<string, object?>();
        foreach (var key in authoredFields.Keys)
            persisted[key] = savedNames.Contains(key) ? saved[key] : null;

        var repairs = DerivedFieldRepair.Compute(authoredFields, persisted);
        if (repairs.Count == 0)
            return;

        saved.DeserializeFrom(repairs);
        using (var repairContext = new Dynamicweb.Content.Items.ItemContext())
            saved.Save(repairContext);

        foreach (var repair in repairs)
            Log($"Derive-on-save repair: {itemType} id={itemId} field '{repair.Key}' re-written to " +
                $"authored value '{repair.Value}' (platform had overwritten it on save).");
    }

    // -------------------------------------------------------------------------
    // Page property assignment helper (shared by INSERT and UPDATE paths)
    // -------------------------------------------------------------------------

    private static void ApplyPageProperties(Page page, SerializedPage dto)
    {
        // Flat scalars
        page.NavigationTag = dto.NavigationTag;
        page.ShortCut = dto.ShortCut;
        page.Hidden = dto.Hidden;
        page.Allowclick = dto.Allowclick;
        page.Allowsearch = dto.Allowsearch;
        page.ShowInSitemap = dto.ShowInSitemap;
        page.ShowInLegend = dto.ShowInLegend;
        page.SslMode = dto.SslMode;
        page.ColorSchemeId = dto.ColorSchemeId;
        page.ExactUrl = dto.ExactUrl;
        page.ContentType = dto.ContentType;
        page.TopImage = dto.TopImage;
        page.PermissionType = dto.PermissionType;

        // DisplayMode -- parse from string, skip if not parseable
        if (!string.IsNullOrEmpty(dto.DisplayMode) &&
            Enum.TryParse<Dynamicweb.Content.DisplayMode>(dto.DisplayMode, true, out var dm))
            page.DisplayMode = dm;

        // ActiveFrom/ActiveTo -- only set when DTO has non-null values
        // (DW defaults to DateTime.Now / DateHelper.MaxDate() -- do not overwrite)
        if (dto.ActiveFrom.HasValue)
            page.ActiveFrom = dto.ActiveFrom.Value;
        if (dto.ActiveTo.HasValue)
            page.ActiveTo = dto.ActiveTo.Value;

        // SEO sub-object
        if (dto.Seo != null)
        {
            page.MetaTitle = dto.Seo.MetaTitle;
            page.MetaCanonical = dto.Seo.MetaCanonical;
            page.Description = dto.Seo.Description;
            page.Keywords = dto.Seo.Keywords;
            page.Noindex = dto.Seo.Noindex;
            page.Nofollow = dto.Seo.Nofollow;
            page.Robots404 = dto.Seo.Robots404;
        }

        // URL settings sub-object
        if (dto.UrlSettings != null)
        {
            page.UrlDataProviderTypeName = dto.UrlSettings.UrlDataProviderTypeName;
            page.UrlDataProviderParameters = XmlFormatter.CompactWithMerge(dto.UrlSettings.UrlDataProviderParameters, page.UrlDataProviderParameters);
            page.UrlIgnoreForChildren = dto.UrlSettings.UrlIgnoreForChildren;
            page.UrlUseAsWritten = dto.UrlSettings.UrlUseAsWritten;
        }

        // Visibility sub-object
        if (dto.Visibility != null)
        {
            page.HideForPhones = dto.Visibility.HideForPhones;
            page.HideForTablets = dto.Visibility.HideForTablets;
            page.HideForDesktops = dto.Visibility.HideForDesktops;
        }

        // NavigationSettings -- ONLY create when UseEcomGroups is true (per research pitfall 3)
        if (dto.NavigationSettings != null && dto.NavigationSettings.UseEcomGroups)
        {
            page.NavigationSettings = new PageNavigationSettings
            {
                UseEcomGroups = true,
                Groups = dto.NavigationSettings.Groups,
                ShopID = dto.NavigationSettings.ShopID,
                MaxLevels = dto.NavigationSettings.MaxLevels,
                ProductPage = dto.NavigationSettings.ProductPage,
                NavigationProvider = dto.NavigationSettings.NavigationProvider,
                IncludeProducts = dto.NavigationSettings.IncludeProducts
            };
            if (Enum.TryParse<EcommerceNavigationParentType>(
                dto.NavigationSettings.ParentType, true, out var pt))
                page.NavigationSettings.ParentType = pt;
        }
    }

    // -------------------------------------------------------------------------
    // Merge-mode field-level fill helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// D-05: applies the DTO's flat scalars (MenuText, UrlName, Active, Sort, ItemType,
    /// LayoutTemplate, LayoutApplyToSubPages, IsFolder, TreeSection) to the existing page
    /// only when the target value is unset per <see cref="MergePredicate"/>. Returns filled
    /// count; increments <paramref name="left"/> for each target-set skip.
    /// D-10 tradeoff: false/0/empty count as unset — documented in 39-CONTEXT.md.
    /// </summary>
    private static int MergePageScalars(Page existingPage, SerializedPage dto, ref int left)
    {
        int filled = 0;

        if (MergePredicate.IsUnsetForMerge(existingPage.MenuText)) { existingPage.MenuText = dto.MenuText; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.UrlName))  { existingPage.UrlName = dto.UrlName;  filled++; } else left++;
        // D-10 tradeoff: false counts as unset for Active.
        if (MergePredicate.IsUnsetForMerge(existingPage.Active))   { existingPage.Active = dto.IsActive; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.Sort))     { existingPage.Sort = dto.SortOrder;  filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.ItemType)) { existingPage.ItemType = dto.ItemType ?? string.Empty; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.LayoutTemplate)) { existingPage.LayoutTemplate = dto.Layout ?? string.Empty; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.LayoutApplyToSubPages)) { existingPage.LayoutApplyToSubPages = dto.LayoutApplyToSubPages; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.IsFolder)) { existingPage.IsFolder = dto.IsFolder; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(existingPage.TreeSection)) { existingPage.TreeSection = dto.TreeSection ?? string.Empty; filled++; } else left++;

        return filled;
    }

    /// <summary>
    /// D-04: per-property merge for Page properties and sub-object DTOs (Seo, UrlSettings,
    /// Visibility, NavigationSettings). Mirrors the structure of <see cref="ApplyPageProperties"/>
    /// but gates every assignment through <see cref="MergePredicate.IsUnsetForMerge(object?, System.Type)"/>.
    /// Returns filled count; increments <paramref name="left"/> for each target-set skip.
    /// </summary>
    private static int ApplyPagePropertiesWithMerge(Page page, SerializedPage dto, ref int left)
    {
        int filled = 0;

        // Flat scalars (the ~30 Phase-23 properties)
        if (MergePredicate.IsUnsetForMerge(page.NavigationTag))  { page.NavigationTag = dto.NavigationTag; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.ShortCut))       { page.ShortCut = dto.ShortCut; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.Hidden))         { page.Hidden = dto.Hidden; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.Allowclick))     { page.Allowclick = dto.Allowclick; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.Allowsearch))    { page.Allowsearch = dto.Allowsearch; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.ShowInSitemap))  { page.ShowInSitemap = dto.ShowInSitemap; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.ShowInLegend))   { page.ShowInLegend = dto.ShowInLegend; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.SslMode))        { page.SslMode = dto.SslMode; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.ColorSchemeId))  { page.ColorSchemeId = dto.ColorSchemeId; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.ExactUrl))       { page.ExactUrl = dto.ExactUrl; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.ContentType))    { page.ContentType = dto.ContentType; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.TopImage))       { page.TopImage = dto.TopImage; filled++; } else left++;
        if (MergePredicate.IsUnsetForMerge(page.PermissionType)) { page.PermissionType = dto.PermissionType; filled++; } else left++;

        // DisplayMode -- parse from string, only fill when target DisplayMode is at enum default.
        if (!string.IsNullOrEmpty(dto.DisplayMode) &&
            Enum.TryParse<Dynamicweb.Content.DisplayMode>(dto.DisplayMode, true, out var dm))
        {
            if (MergePredicate.IsUnsetForMerge(page.DisplayMode, typeof(Dynamicweb.Content.DisplayMode))) { page.DisplayMode = dm; filled++; } else left++;
        }

        // ActiveFrom / ActiveTo: gated by MergePredicate (DateTime.MinValue = unset).
        if (dto.ActiveFrom.HasValue)
        {
            if (MergePredicate.IsUnsetForMerge(page.ActiveFrom)) { page.ActiveFrom = dto.ActiveFrom.Value; filled++; } else left++;
        }
        if (dto.ActiveTo.HasValue)
        {
            if (MergePredicate.IsUnsetForMerge(page.ActiveTo)) { page.ActiveTo = dto.ActiveTo.Value; filled++; } else left++;
        }

        // SEO sub-object — D-04: per-property merge.
        if (dto.Seo != null)
        {
            if (MergePredicate.IsUnsetForMerge(page.MetaTitle))     { page.MetaTitle = dto.Seo.MetaTitle; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.MetaCanonical)) { page.MetaCanonical = dto.Seo.MetaCanonical; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.Description))   { page.Description = dto.Seo.Description; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.Keywords))      { page.Keywords = dto.Seo.Keywords; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.Noindex))       { page.Noindex = dto.Seo.Noindex; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.Nofollow))      { page.Nofollow = dto.Seo.Nofollow; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.Robots404))     { page.Robots404 = dto.Seo.Robots404; filled++; } else left++;
        }

        // URL settings sub-object — D-04.
        if (dto.UrlSettings != null)
        {
            if (MergePredicate.IsUnsetForMerge(page.UrlDataProviderTypeName)) { page.UrlDataProviderTypeName = dto.UrlSettings.UrlDataProviderTypeName; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.UrlDataProviderParameters))
            {
                page.UrlDataProviderParameters = XmlFormatter.CompactWithMerge(dto.UrlSettings.UrlDataProviderParameters, page.UrlDataProviderParameters);
                filled++;
            }
            else left++;
            if (MergePredicate.IsUnsetForMerge(page.UrlIgnoreForChildren)) { page.UrlIgnoreForChildren = dto.UrlSettings.UrlIgnoreForChildren; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.UrlUseAsWritten))      { page.UrlUseAsWritten = dto.UrlSettings.UrlUseAsWritten; filled++; } else left++;
        }

        // Visibility sub-object — D-04.
        if (dto.Visibility != null)
        {
            if (MergePredicate.IsUnsetForMerge(page.HideForPhones))   { page.HideForPhones = dto.Visibility.HideForPhones; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.HideForTablets))  { page.HideForTablets = dto.Visibility.HideForTablets; filled++; } else left++;
            if (MergePredicate.IsUnsetForMerge(page.HideForDesktops)) { page.HideForDesktops = dto.Visibility.HideForDesktops; filled++; } else left++;
        }

        // NavigationSettings — Pitfall 5: if the whole sub-object is null on target but
        // YAML has one, construct it fresh; otherwise per-property D-04 merge.
        if (dto.NavigationSettings != null && dto.NavigationSettings.UseEcomGroups)
        {
            if (page.NavigationSettings == null)
            {
                page.NavigationSettings = new PageNavigationSettings
                {
                    UseEcomGroups = true,
                    Groups = dto.NavigationSettings.Groups,
                    ShopID = dto.NavigationSettings.ShopID,
                    MaxLevels = dto.NavigationSettings.MaxLevels,
                    ProductPage = dto.NavigationSettings.ProductPage,
                    NavigationProvider = dto.NavigationSettings.NavigationProvider,
                    IncludeProducts = dto.NavigationSettings.IncludeProducts
                };
                if (Enum.TryParse<EcommerceNavigationParentType>(
                    dto.NavigationSettings.ParentType, true, out var pt))
                    page.NavigationSettings.ParentType = pt;
                filled++;
            }
            else
            {
                // Per-property merge for the nested settings.
                if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.Groups))             { page.NavigationSettings.Groups = dto.NavigationSettings.Groups; filled++; } else left++;
                if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.ShopID))             { page.NavigationSettings.ShopID = dto.NavigationSettings.ShopID; filled++; } else left++;
                if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.MaxLevels))          { page.NavigationSettings.MaxLevels = dto.NavigationSettings.MaxLevels; filled++; } else left++;
                if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.ProductPage))        { page.NavigationSettings.ProductPage = dto.NavigationSettings.ProductPage; filled++; } else left++;
                if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.NavigationProvider)) { page.NavigationSettings.NavigationProvider = dto.NavigationSettings.NavigationProvider; filled++; } else left++;
                if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.IncludeProducts))    { page.NavigationSettings.IncludeProducts = dto.NavigationSettings.IncludeProducts; filled++; } else left++;

                if (Enum.TryParse<EcommerceNavigationParentType>(dto.NavigationSettings.ParentType, true, out var pt2))
                {
                    if (MergePredicate.IsUnsetForMerge(page.NavigationSettings.ParentType, typeof(EcommerceNavigationParentType))) { page.NavigationSettings.ParentType = pt2; filled++; } else left++;
                }
            }
        }

        return filled;
    }

    /// <summary>
    /// D-02: field-level merge for ItemFields. Reads current target values via
    /// <c>ItemEntry.SerializeTo</c>, fills only entries where the target string is
    /// NULL or empty (D-02 string rule), overlays onto the current dict to prevent
    /// sibling clearing (Pitfall 7 defense), then <c>DeserializeFrom + Save</c>.
    /// Returns filled count; increments <paramref name="left"/> for each skip.
    /// </summary>
    private int MergeItemFields(
        string? itemType,
        string itemId,
        Dictionary<string, object> yamlFields,
        IReadOnlySet<string>? excludeFields,
        ref int left)
    {
        if (string.IsNullOrEmpty(itemType)) return 0;

        var itemEntry = Services.Items.GetItem(itemType, itemId);
        if (itemEntry == null)
        {
            Log($"WARNING: Could not load ItemEntry for type={itemType}, id={itemId}");
            return 0;
        }

        var currentDict = new Dictionary<string, object?>();
        itemEntry.SerializeTo(currentDict);

        int filled = 0;
        var filledFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in yamlFields)
        {
            if (ItemSystemFields.Contains(kvp.Key)) continue;
            if (excludeFields != null && excludeFields.Contains(kvp.Key)) continue;

            currentDict.TryGetValue(kvp.Key, out var currentVal);
            if (MergePredicate.IsUnsetForMerge(currentVal?.ToString()))
            {
                currentDict[kvp.Key] = kvp.Value;   // overlay filled onto current (Pitfall 7)
                filledFields.Add(kvp.Key);
                filled++;
            }
            else
            {
                left++;
            }
        }

        if (filled == 0) return 0;

        // Engine issue #6: same object-shape requirement as SaveItemFields, scoped to the
        // entries this merge actually filled — untouched entries keep the value the target
        // already holds.
        ButtonDataFieldLookup.Apply(itemType, currentDict, _log, onlyFields: filledFields);

        itemEntry.DeserializeFrom(currentDict);
        using (var itemSaveContext = new Dynamicweb.Content.Items.ItemContext())
            itemEntry.Save(itemSaveContext);
        return filled;
    }

    /// <summary>
    /// D-03: field-level merge for PropertyItem fields (Icon, SubmenuType, etc.).
    /// Same shape as <see cref="MergeItemFields"/> — live-read current target values,
    /// overlay only unset entries, save once.
    /// </summary>
    private int MergePropertyItemFields(
        Page page,
        Dictionary<string, object> propertyFields,
        IReadOnlySet<string>? excludeFields,
        ref int left)
    {
        if (propertyFields.Count == 0) return 0;
        if (string.IsNullOrEmpty(page.PropertyItemId))
        {
            Log($"  Page {page.UniqueId} has no PropertyItemId — cannot merge property fields");
            return 0;
        }

        var propItem = page.PropertyItem;
        if (propItem == null)
        {
            Log($"  WARNING: Could not load PropertyItem for page {page.UniqueId}");
            return 0;
        }

        var currentDict = new Dictionary<string, object?>();
        propItem.SerializeTo(currentDict);

        int filled = 0;
        var filledFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in propertyFields)
        {
            if (ItemSystemFields.Contains(kvp.Key)) continue;
            if (excludeFields != null && excludeFields.Contains(kvp.Key)) continue;

            currentDict.TryGetValue(kvp.Key, out var currentVal);
            if (MergePredicate.IsUnsetForMerge(currentVal?.ToString()))
            {
                currentDict[kvp.Key] = kvp.Value;
                filledFields.Add(kvp.Key);
                filled++;
            }
            else
            {
                left++;
            }
        }

        if (filled == 0) return 0;

        // Engine issue #6: reshape only the entries this merge filled.
        ButtonDataFieldLookup.Apply(propItem.SystemName, currentDict, _log, onlyFields: filledFields);

        propItem.DeserializeFrom(currentDict);
        using (var propItemContext = new Dynamicweb.Content.Items.ItemContext())
            propItem.Save(propItemContext);
        return filled;
    }

    /// <summary>
    /// Per-field dry-run diff for the Merge-fill path. Emits
    /// <c>"  would fill [col=X]: target=&lt;unset&gt; -&gt; fill='...'"</c> lines
    /// only where the fill would actually fire on a live run. No DW-API writes.
    /// </summary>
    /// <remarks>
    /// Dry-run log output includes YAML field values. Do not enable dry-run in
    /// logs that flow to untrusted parties — Phase 39 threat model T-39-01-03.
    /// </remarks>
    private void LogMergeFillDryRun(SerializedPage dto, Page existing, IReadOnlySet<string>? excludeFields, WriteContext ctx)
    {
        var diffs = new List<string>();

        void Consider(string col, string? target, object? fillValue)
        {
            if (MergePredicate.IsUnsetForMerge(target))
                diffs.Add($"  would fill [col={col}]: target=<unset> -> fill='{fillValue}'");
        }

        void ConsiderInt(string col, int target, object fillValue)
        {
            if (MergePredicate.IsUnsetForMerge(target))
                diffs.Add($"  would fill [col={col}]: target=<unset> -> fill='{fillValue}'");
        }

        void ConsiderBool(string col, bool target, object fillValue)
        {
            if (MergePredicate.IsUnsetForMerge(target))
                diffs.Add($"  would fill [col={col}]: target=<unset> -> fill='{fillValue}'");
        }

        // Flat scalars
        Consider("MenuText", existing.MenuText, dto.MenuText);
        Consider("UrlName", existing.UrlName, dto.UrlName);
        ConsiderBool("Active", existing.Active, dto.IsActive);
        ConsiderInt("Sort", existing.Sort, dto.SortOrder);
        Consider("ItemType", existing.ItemType, dto.ItemType);
        Consider("LayoutTemplate", existing.LayoutTemplate, dto.Layout);
        Consider("TreeSection", existing.TreeSection, dto.TreeSection);

        // SEO sub-object
        if (dto.Seo != null)
        {
            Consider("MetaTitle", existing.MetaTitle, dto.Seo.MetaTitle);
            Consider("MetaCanonical", existing.MetaCanonical, dto.Seo.MetaCanonical);
            Consider("Description", existing.Description, dto.Seo.Description);
            Consider("Keywords", existing.Keywords, dto.Seo.Keywords);
        }

        // ItemFields — live-read current target
        if (!string.IsNullOrEmpty(existing.ItemType))
        {
            var itemEntry = Services.Items.GetItem(existing.ItemType, existing.ItemId);
            if (itemEntry != null)
            {
                var currentDict = new Dictionary<string, object?>();
                itemEntry.SerializeTo(currentDict);
                foreach (var kvp in dto.Fields)
                {
                    if (ItemSystemFields.Contains(kvp.Key)) continue;
                    if (excludeFields != null && excludeFields.Contains(kvp.Key)) continue;
                    currentDict.TryGetValue(kvp.Key, out var curr);
                    if (MergePredicate.IsUnsetForMerge(curr?.ToString()))
                        diffs.Add($"  would fill [col={kvp.Key}]: target=<unset> -> fill='{kvp.Value}'");
                }
            }
        }

        Log($"[DRY-RUN] Merge-fill: page {dto.PageUniqueId} (ID={existing.ID}) - {diffs.Count} would-fills");
        foreach (var d in diffs) Log(d);

        if (diffs.Count == 0) ctx.Skipped++;
        else ctx.Updated++;
    }

    // -------------------------------------------------------------------------
    // Multi-language: master-link restore + language-layer validation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Restores master links captured as GUID references during serialization:
    /// Page.MasterPageId + MasterType (language-layer pages), Paragraph.MasterParagraphID
    /// (language-layer paragraphs) and Paragraph.GlobalRecordPageID (global paragraphs).
    /// Runs in the post-write pass so both sides of each link exist on target. Unresolved
    /// masters warn — order master-area predicates before their language layers.
    /// </summary>
    private void RestoreMasterLinks(
        List<SerializedPage> pages,
        Dictionary<Guid, int> pageGuidCache,
        Dictionary<Guid, int> paragraphGuidCache)
    {
        var (pageUpdates, pageUnresolved) = MasterLinkRestorer.ComputePageLinkUpdates(pages, pageGuidCache);
        int pagesLinked = 0;
        foreach (var update in pageUpdates)
        {
            try
            {
                var page = Services.Pages.GetPage(update.TargetPageId);
                if (page == null) continue;

                var needsSave = false;
                if (page.MasterPageId != update.TargetMasterPageId)
                {
                    page.MasterPageId = update.TargetMasterPageId;
                    needsSave = true;
                }
                if (!string.IsNullOrEmpty(update.MasterType)
                    && Enum.TryParse<MasterType>(update.MasterType, ignoreCase: true, out var masterType)
                    && page.MasterType != masterType)
                {
                    page.MasterType = masterType;
                    needsSave = true;
                }
                if (needsSave)
                {
                    Services.Pages.SavePage(page, skipLanguages: true);
                    pagesLinked++;
                }
            }
            catch (Exception ex)
            {
                Log($"WARNING: Could not restore master link for page ID {update.TargetPageId}: {ex.Message}");
            }
        }

        var (paraUpdates, paraUnresolved) = MasterLinkRestorer.ComputeParagraphLinkUpdates(pages, paragraphGuidCache, pageGuidCache);
        int paragraphsLinked = 0;
        foreach (var update in paraUpdates)
        {
            try
            {
                var para = Services.Paragraphs.GetParagraph(update.TargetParagraphId);
                if (para == null) continue;

                var needsSave = false;
                if (update.TargetMasterParagraphId.HasValue && para.MasterParagraphID != update.TargetMasterParagraphId.Value)
                {
                    para.MasterParagraphID = update.TargetMasterParagraphId.Value;
                    needsSave = true;
                }
                if (update.TargetGlobalRecordPageId.HasValue && para.GlobalRecordPageID != update.TargetGlobalRecordPageId.Value)
                {
                    para.GlobalRecordPageID = update.TargetGlobalRecordPageId.Value;
                    needsSave = true;
                }
                if (needsSave)
                {
                    Services.Paragraphs.SaveParagraph(para);
                    paragraphsLinked++;
                }
            }
            catch (Exception ex)
            {
                Log($"WARNING: Could not restore master link for paragraph ID {update.TargetParagraphId}: {ex.Message}");
            }
        }

        if (pagesLinked > 0 || paragraphsLinked > 0)
            Log($"Master links restored: {pagesLinked} page(s), {paragraphsLinked} paragraph(s)");

        foreach (var miss in pageUnresolved)
            Log($"WARNING: Master page {miss.MasterGuid} for language page {miss.OwnerGuid} not found on target — " +
                "order the master area's predicate before its language layers and re-run.");
        foreach (var miss in paraUnresolved)
            Log($"WARNING: Master reference {miss.MasterGuid} ({miss.Kind}) for paragraph {miss.OwnerGuid} not found on target.");
    }

    /// <summary>
    /// When the area being deserialized is a language layer (AreaMasterAreaID > 0 in its
    /// serialized properties), verify the master area and the referenced ecom language exist
    /// on target. Warnings only — the write proceeds either way (area IDs are stable across
    /// environments, so the link self-heals once the master is deserialized).
    /// </summary>
    private void ValidateLanguageLayerArea(int areaId, Dictionary<string, object> properties)
    {
        var masterAreaId = ReadIntProperty(properties, "AreaMasterAreaID");
        if (masterAreaId > 0)
        {
            try
            {
                if (Services.Areas.GetArea(masterAreaId) == null)
                    Log($"WARNING: Area {areaId} is a language layer of master area {masterAreaId}, " +
                        "which does not exist on target yet. Deserialize the master area first.");
                else
                    Log($"Area {areaId} is a language layer of master area {masterAreaId} (master present on target).");
            }
            catch { /* DW runtime unavailable — skip validation */ }
        }

        var ecomLanguageId = ReadStringProperty(properties, "AreaEcomLanguageID");
        if (!string.IsNullOrEmpty(ecomLanguageId))
        {
            try
            {
                var cb = new CommandBuilder();
                cb.Add("SELECT COUNT(*) FROM [EcomLanguages] WHERE [LanguageID] = {0}", ecomLanguageId);
                var count = Convert.ToInt32(Database.ExecuteScalar(cb) ?? 0);
                if (count == 0)
                    Log($"WARNING: Area {areaId} references ecom language '{ecomLanguageId}' which does not exist " +
                        "on target. Add an EcomLanguages predicate or create the language before going live.");
            }
            catch { /* Ecom not installed or DW runtime unavailable — skip validation */ }
        }
    }

    private static int ReadIntProperty(Dictionary<string, object> properties, string key)
    {
        var match = properties.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
        return match.Key != null && int.TryParse(match.Value?.ToString(), out var value) ? value : 0;
    }

    private static string? ReadStringProperty(Dictionary<string, object> properties, string key)
    {
        var match = properties.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
        return match.Key != null ? match.Value?.ToString() : null;
    }

    // -------------------------------------------------------------------------
    // Phase 2: Internal link resolution
    // -------------------------------------------------------------------------

    private void ResolveLinksInArea(int areaId, InternalLinkResolver resolver, HashSet<int>? onlyTargetPageIds = null, bool resolveAreaItemFields = true)
    {
        // Resolve internal links in area-level ItemType fields (AREA-02) — only for the
        // entry that owns (and just wrote) the area state; see DeserializePredicate.
        var targetArea = Services.Areas.GetArea(areaId);
        if (resolveAreaItemFields && targetArea != null && !string.IsNullOrEmpty(targetArea.ItemType) && !string.IsNullOrEmpty(targetArea.ItemId))
        {
            ResolveLinksInItemFields(targetArea.ItemType, targetArea.ItemId, resolver);
        }

        // Re-read the area's pages and scan their item fields for internal links.
        // onlyTargetPageIds restricts the pass to pages the current entry wrote — pages
        // written by earlier entries/modes already hold rewritten TARGET ids and must not
        // be re-interpreted as source ids.
        var allPages = Services.Pages.GetPagesByAreaID(areaId)
            .Where(p => onlyTargetPageIds is null || onlyTargetPageIds.Contains(p.ID));
        foreach (var page in allPages)
        {
            // Resolve item fields (link fields, button fields, rich text HTML)
            ResolveLinksInItemFields(page.ItemType, page.ItemId, resolver);

            // Resolve PropertyItem fields (Icon, SubmenuType, etc.)
            ResolveLinksInPropertyItem(page, resolver);

            // Resolve ShortCut link (PAGE-02) -- e.g., "Default.aspx?ID=42" -> "Default.aspx?ID=99"
            bool pageNeedsResave = false;
            if (!string.IsNullOrEmpty(page.ShortCut))
            {
                resolver.CurrentLocator = $"shortcut|{page.ID}";
                var resolved = resolver.ResolveLinks(page.ShortCut);
                resolver.CurrentLocator = null;
                if (resolved != page.ShortCut)
                {
                    page.ShortCut = resolved;
                    pageNeedsResave = true;
                }
            }

            // Resolve NavigationSettings.ProductPage link (ECOM-02)
            if (page.NavigationSettings?.ProductPage != null)
            {
                resolver.CurrentLocator = $"navsettings|{page.ID}";
                var resolved = resolver.ResolveLinks(page.NavigationSettings.ProductPage);
                resolver.CurrentLocator = null;
                if (resolved != page.NavigationSettings.ProductPage)
                {
                    page.NavigationSettings.ProductPage = resolved;
                    pageNeedsResave = true;
                }
            }

            if (pageNeedsResave)
                Services.Pages.SavePage(page, skipLanguages: true);

            // Resolve paragraph item fields
            var paragraphs = Services.Paragraphs.GetParagraphsByPageId(page.ID);
            foreach (var para in paragraphs)
            {
                ResolveLinksInItemFields(para.ItemType, para.ItemId, resolver);

                // Resolve internal links embedded in ModuleSettings XML (e.g.,
                // UserAuthentication's <RedirectToSpecificPage>Default.aspx?Id=NNN</...>).
                if (!string.IsNullOrEmpty(para.ModuleSettings))
                {
                    resolver.CurrentLocator = $"modulesettings|{para.ID}";
                    var resolvedSettings = resolver.ResolveLinks(para.ModuleSettings);
                    resolver.CurrentLocator = null;
                    if (resolvedSettings != para.ModuleSettings)
                    {
                        para.ModuleSettings = resolvedSettings ?? string.Empty;
                        Services.Paragraphs.SaveParagraph(para);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Rewrites cross-environment internal links inside a serialized item's fields, returning
    /// ONLY the fields whose value changed. An item's system fields (<see cref="ItemSystemFields"/>
    /// — <c>Id</c>, <c>ItemInstanceType</c>, <c>Sort</c>, ...) are NEVER resolved: the resolver
    /// remaps bare numeric page ids source→target, and an item's own numeric <c>Id</c> (its
    /// primary key) would be rewritten as though it were a page link. Persisting a remapped
    /// <c>Id</c> makes the subsequent <c>item.Save()</c> write the row under a DIFFERENT id,
    /// which silently overwrites a neighbouring item's fields (the Title "smear") and orphans
    /// the original row. This mirrors <see cref="SaveItemFields"/>, which has always excluded
    /// <see cref="ItemSystemFields"/>.
    /// </summary>
    internal static Dictionary<string, object?> ResolveLinkFields(
        IEnumerable<KeyValuePair<string, object?>> fields,
        InternalLinkResolver resolver,
        Func<string, string> locatorForField)
    {
        var changed = new Dictionary<string, object?>();
        foreach (var kvp in fields)
        {
            if (ItemSystemFields.Contains(kvp.Key))
                continue;
            if (kvp.Value is string strValue && strValue.Length > 0)
            {
                resolver.CurrentLocator = locatorForField(kvp.Key);
                var resolved = resolver.ResolveLinks(strValue);
                resolver.CurrentLocator = null;
                if (resolved != strValue)
                    changed[kvp.Key] = resolved;
            }
        }
        return changed;
    }

    private void ResolveLinksInItemFields(string? itemType, string? itemId, InternalLinkResolver resolver)
    {
        if (string.IsNullOrEmpty(itemType) || string.IsNullOrEmpty(itemId))
            return;

        // Read a FRESH (uncached) copy from the database rather than Services.Items.GetItem.
        // GetItem serves Dynamicweb's 15-minute item cache, which — during a bulk deserialize
        // that adds a language-paired page — can hand back a STALE/aliased Item whose fields
        // belong to a neighbouring id. The old code round-tripped that cached snapshot back to
        // disk (SerializeTo -> DeserializeFrom -> Save), overwriting the correct row with the
        // stale one: the silent Title "smear" and the orphaned items. Reading straight from the
        // repository (SelectById bypasses the cache) guarantees we rewrite links on the row's
        // real content, and the Save afterwards evicts the poisoned cache entry for this id.
        var repository = Dynamicweb.Content.Items.ItemManager.Storage.Open(itemType);
        var item = repository.SelectById(itemId);
        if (item == null)
            return;

        var fields = new Dictionary<string, object?>();
        item.SerializeTo(fields);

        // Only the fields whose links actually changed are written back — never re-persist the
        // full snapshot (keeps the write minimal and side-effect free). System fields (Id, Sort,
        // ...) are excluded so item identity is never rewritten (see ResolveLinkFields).
        var changedFields = ResolveLinkFields(fields, resolver, key => $"item|{itemType}|{itemId}|{key}");

        if (changedFields.Count > 0)
        {
            if (_isDryRun)
            {
                Log($"[DRY-RUN] Would resolve links in {itemType}/{itemId}");
                return;
            }
            // Engine issue #6: the resolver rewrites "SelectedValue": "N" INSIDE ButtonData JSON,
            // so a resolved button comes back here as a string. Writing that string back binds
            // to nothing and the remap is silently lost — reshape it first.
            ButtonDataFieldLookup.Apply(itemType, changedFields, _log);

            item.DeserializeFrom(changedFields);
            using (var itemSaveContext = new Dynamicweb.Content.Items.ItemContext())
                item.Save(itemSaveContext);
        }
    }

    private void ResolveLinksInPropertyItem(Page page, InternalLinkResolver resolver)
    {
        if (string.IsNullOrEmpty(page.PropertyItemId))
            return;

        var propItem = page.PropertyItem;
        if (propItem == null)
            return;

        var fields = new Dictionary<string, object?>();
        propItem.SerializeTo(fields);

        // Same identity-safe resolution as ResolveLinksInItemFields: system fields (Id, ...) are
        // never resolved, so the property item's key can't be remapped and corrupt a neighbour.
        var changedFields = ResolveLinkFields(fields, resolver, key => $"propitem|{page.ID}|{key}");

        if (changedFields.Count > 0)
        {
            if (_isDryRun)
            {
                Log($"[DRY-RUN] Would resolve links in PropertyItem of page {page.UniqueId}");
                return;
            }
            // Engine issue #6: same object-shape requirement as ResolveLinksInItemFields.
            ButtonDataFieldLookup.Apply(propItem.SystemName, changedFields, _log);

            propItem.DeserializeFrom(changedFields);
            using (var propItemContext = new Dynamicweb.Content.Items.ItemContext())
            propItem.Save(propItemContext);
        }
    }

    // -------------------------------------------------------------------------
    // Dry-run diff logging
    // -------------------------------------------------------------------------

    private void LogDryRunPageUpdate(SerializedPage dto, Page? existing, WriteContext ctx)
    {
        if (existing == null)
        {
            Log($"[DRY-RUN] UPDATE page {dto.PageUniqueId} (could not load existing for diff)");
            ctx.Updated++;
            return;
        }

        var diffs = new List<string>();

        if (dto.MenuText != existing.MenuText)
            diffs.Add($"MenuText: '{existing.MenuText}' -> '{dto.MenuText}'");
        if (dto.UrlName != existing.UrlName)
            diffs.Add($"UrlName: '{existing.UrlName}' -> '{dto.UrlName}'");
        if (dto.IsActive != existing.Active)
            diffs.Add($"Active: {existing.Active} -> {dto.IsActive}");
        if (dto.SortOrder != existing.Sort)
            diffs.Add($"Sort: {existing.Sort} -> {dto.SortOrder}");

        // Field-level diffs for ItemType fields
        foreach (var kvp in dto.Fields)
        {
            var currentVal = existing.Item?[kvp.Key]?.ToString();
            var newVal = kvp.Value?.ToString();
            if (currentVal != newVal)
                diffs.Add($"Fields[{kvp.Key}]: '{currentVal}' -> '{newVal}'");
        }

        // PropertyFields diffs (e.g. Icon, SubmenuType)
        if (existing.PropertyItem != null && dto.PropertyFields.Count > 0)
        {
            var existingPropFields = new Dictionary<string, object?>();
            existing.PropertyItem.SerializeTo(existingPropFields);

            foreach (var kvp in dto.PropertyFields)
            {
                if (ItemSystemFields.Contains(kvp.Key)) continue;
                existingPropFields.TryGetValue(kvp.Key, out var currentVal);
                var currentStr = currentVal?.ToString();
                var newStr = kvp.Value?.ToString();
                if (currentStr != newStr)
                    diffs.Add($"PropertyFields[{kvp.Key}]: '{currentStr}' -> '{newStr}'");
            }
        }
        else if (existing.PropertyItem == null && dto.PropertyFields.Count > 0)
        {
            // No existing PropertyItem but YAML has property fields — log all as new
            foreach (var kvp in dto.PropertyFields)
            {
                if (ItemSystemFields.Contains(kvp.Key)) continue;
                diffs.Add($"PropertyFields[{kvp.Key}]: '' -> '{kvp.Value}'");
            }
        }

        if (dto.Permissions.Count > 0)
            diffs.Add($"Would apply {dto.Permissions.Count} permission(s)");

        if (diffs.Count == 0)
        {
            Log($"[DRY-RUN] SKIP {dto.PageUniqueId} (unchanged)");
            ctx.Skipped++;
        }
        else
        {
            Log($"[DRY-RUN] UPDATE {dto.PageUniqueId}:\n  " + string.Join("\n  ", diffs));
            ctx.Updated++;
        }
    }

    private void LogDryRunParagraphUpdate(SerializedParagraph dto, Paragraph existing, WriteContext ctx)
    {
        var diffs = new List<string>();

        if (dto.SortOrder != existing.Sort)
            diffs.Add($"Sort: {existing.Sort} -> {dto.SortOrder}");
        if (dto.Header != existing.Header)
            diffs.Add($"Header: '{existing.Header}' -> '{dto.Header}'");
        if (dto.ItemType != existing.ItemType)
            diffs.Add($"ItemType: '{existing.ItemType}' -> '{dto.ItemType}'");

        // Field-level diffs for ItemType fields
        foreach (var kvp in dto.Fields)
        {
            string? currentVal;
            if (kvp.Key == "Text")
                currentVal = existing.Text;
            else
                currentVal = existing.Item?[kvp.Key]?.ToString();

            var newVal = kvp.Value?.ToString();
            if (currentVal != newVal)
                diffs.Add($"Fields[{kvp.Key}]: '{currentVal}' -> '{newVal}'");
        }

        if (StrategyFor(dto.Ownership) != ConflictStrategy.DestinationWins && dto.Permissions.Count > 0)
            diffs.Add($"Would apply {dto.Permissions.Count} permission(s)");

        if (diffs.Count == 0)
        {
            Log($"[DRY-RUN] SKIP {dto.ParagraphUniqueId} (unchanged)");
            ctx.Skipped++;
        }
        else
        {
            Log($"[DRY-RUN] UPDATE {dto.ParagraphUniqueId}:\n  " + string.Join("\n  ", diffs));
            ctx.Updated++;
        }
    }
}

/// <summary>
/// What a Content entry does when its target area is not on the target (AREA-04).
/// One enum for both run kinds so the dry run and the real run answer the same question
/// with the same code — see <see cref="ContentDeserializer.ResolveMissingAreaAction"/>.
/// </summary>
internal enum MissingAreaAction
{
    /// <summary>Real run: create the area from the serialized YAML, then write into it.</summary>
    Create,

    /// <summary>
    /// Dry run: report the create the real run would perform and continue the walk as if the
    /// area existed. Zero DB writes; every page below reports as a [DRY-RUN] CREATE.
    /// </summary>
    SimulateCreate,

    /// <summary>
    /// Neither run can create the area (no serialized properties to create it from). Warn
    /// and skip the entry — the same answer in both runs.
    /// </summary>
    Skip
}
