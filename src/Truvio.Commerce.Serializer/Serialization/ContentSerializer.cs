using Dynamicweb.Content;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Orchestrates the DW-to-disk serialization pipeline:
/// traverses the DW page tree, applies predicate filtering, maps to DTOs via ContentMapper,
/// resolves cross-references via ReferenceResolver, and writes YAML via FileSystemStore.
/// </summary>
public class ContentSerializer
{
    private readonly SerializerConfiguration _configuration;
    private readonly IContentStore _store;
    private readonly ReferenceResolver _referenceResolver;
    private readonly ContentMapper _mapper;
    private readonly PermissionMapper _permissionMapper;
    private readonly ContentPredicateSet _predicateSet;
    private readonly Action<string>? _log;
    private readonly bool _lenientLinkSweep;

    /// <summary>
    /// Test seam: which AREA does a page id live in, in the SOURCE database? Production
    /// default asks DW. Returns null when the page does not exist (source orphan), the
    /// area id when it does, and -1 when the lookup itself failed — the sweep treats -1
    /// as "unknown, stay fatal" so infrastructure errors never downgrade a fatal.
    /// </summary>
    internal Func<int, int?> GetPageAreaIdInSource { get; set; } = pageId =>
    {
        try
        {
            return Services.Pages.GetPage(pageId)?.AreaId;
        }
        catch
        {
            return -1;
        }
    };

    /// <param name="lenientLinkSweep">
    /// When true, unresolvable internal links found by the <see cref="BaselineLinkSweeper"/>
    /// are logged as warnings instead of failing the run. Used by ad-hoc subtree exports
    /// (tree right-click "Serialize subtree"), where references out of the exported subtree
    /// are expected — they resolve against the target DB at import time. Baseline runs keep
    /// the default fatal semantics (D-22).
    /// </param>
    public ContentSerializer(SerializerConfiguration configuration, IContentStore? store = null, Action<string>? log = null,
        bool lenientLinkSweep = false)
    {
        _configuration = configuration;
        _store = store ?? new FileSystemStore();
        _referenceResolver = new ReferenceResolver();
        _mapper = new ContentMapper(_referenceResolver);
        _permissionMapper = new PermissionMapper(log);
        _predicateSet = new ContentPredicateSet(configuration);
        _log = log;
        _lenientLinkSweep = lenientLinkSweep;
    }

    private void Log(string message) => _log?.Invoke(message);

    /// <summary>
    /// Serializes all predicates defined in the configuration to disk.
    /// Clears the reference resolver cache between predicates.
    /// Logs a count summary of pages, grid rows, and paragraphs after all predicates are processed.
    /// After all predicates complete (Phase 37-05 / TEMPLATE-01), scans the full serialized
    /// page tree for template references and emits <c>templates.manifest.yml</c> so the
    /// deserialize side can validate every cshtml / grid-row / item-type file exists on the
    /// target environment. Then runs <see cref="BaselineLinkSweeper"/> (D-22 pass 1) over the
    /// in-memory tree — any unresolvable <c>Default.aspx?ID=N</c> reference throws so the
    /// baseline is never committed with orphan links.
    /// </summary>
    public void Serialize()
    {
        int totalPages = 0, totalGridRows = 0, totalParagraphs = 0;
        var allSerializedPages = new List<SerializedPage>();

        // Serialize every Content predicate handed to us, regardless of Mode. Callers
        // pre-filter by mode (SerializeCommand filters config.Predicates by the
        // requested mode before SerializeAll; ContentProvider passes exactly one predicate;
        // PackageBuilder builds a single Replace predicate). A `Mode ==
        // SerializerMode.Replace` filter here would silently produce ZERO YAML for Merge-mode
        // Content predicates: the orchestrator dispatches the predicate, this loop skips it,
        // and the run still reports success.
        foreach (var predicate in _configuration.Predicates.Where(p =>
                     string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase)))
        {
            var area = SerializePredicate(predicate);
            _referenceResolver.Clear();

            if (area != null)
            {
                CountItems(area.Pages, ref totalPages, ref totalGridRows, ref totalParagraphs);
                allSerializedPages.AddRange(area.Pages);
            }
        }

        // Phase 37-05 / TEMPLATE-01: emit templates.manifest.yml listing every cshtml /
        // grid-row / item-type reference in the baseline, with per-reference source-page
        // attribution. The manifest lives at the output root (alongside area folders) so
        // ContentDeserializer can find it without knowing per-predicate subfolders.
        try
        {
            var scanner = new TemplateReferenceScanner();
            var refs = scanner.Scan(allSerializedPages);
            var templateManifest = new TemplateAssetManifest();
            // An inline-scope run sees only its subtree: fold its references into the manifest
            // the full run wrote instead of shrinking it to the subtree.
            if (_configuration.Predicates.Any(p => p.IsInlineScope))
                refs = MergeTemplateReferences(templateManifest.Read(_configuration.OutputDirectory), refs);
            templateManifest.Write(_configuration.OutputDirectory, refs);
            Log($"Wrote {TemplateAssetManifest.ManifestFileName} with {refs.Count} template reference(s)");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Failed to write template manifest: {ex.Message}");
        }

        // Phase 37-05 / LINK-02 pass 1 (D-22): sweep the in-memory tree for Default.aspx?ID=N
        // references that don't resolve to a SerializedPage.SourcePageId in the same tree.
        // Orphan references are fatal — a baseline with broken links fails at runtime on the
        // target environment and cannot be committed to git. 2026-04-20 follow-up: per-mode
        // AcknowledgedOrphanPageIds allows known-broken source data (that cannot be cleaned
        // upstream in time) to pass as warnings. Any unresolvable NOT in the acknowledged set
        // still fails.
        var sweeper = new BaselineLinkSweeper();
        var sweepResult = sweeper.Sweep(allSerializedPages);
        // Per-predicate ack list is the single source of truth. Aggregate across both modes'
        // predicates (filtered by Mode off the flat predicate list) so the sweep receives the union.
        var replaceAck = _configuration.Predicates
            .Where(p => p.Mode == SerializerMode.Replace)
            .SelectMany(p => p.AcknowledgedOrphanPageIds)
            .ToList();
        var mergeAck = _configuration.Predicates
            .Where(p => p.Mode == SerializerMode.Merge)
            .SelectMany(p => p.AcknowledgedOrphanPageIds)
            .ToList();
        Log($"Link sweep: {sweepResult.ResolvedCount} internal link(s) verified, " +
            $"{sweepResult.Unresolved.Count} unresolvable " +
            $"(ack replace={replaceAck.Count}, merge={mergeAck.Count})");
        if (sweepResult.Unresolved.Count > 0)
        {
            if (_lenientLinkSweep)
            {
                // Ad-hoc subtree export: out-of-subtree references are expected and resolve
                // against the target DB at import time. Warn and continue.
                foreach (var u in sweepResult.Unresolved)
                    Log($"WARNING: reference out of exported subtree, ID {u.UnresolvablePageId} in {u.SourcePageIdentifier} / {u.FieldName}: {u.Context}");
                Log($"Serialization complete: {totalPages} pages, {totalGridRows} grid rows, {totalParagraphs} paragraphs serialized.");
                return;
            }

            var acknowledged = new HashSet<int>(replaceAck.Concat(mergeAck));
            var (accepted, fatal) = sweepResult.Unresolved
                .GroupBy(u => acknowledged.Contains(u.UnresolvablePageId))
                .Aggregate(
                    (Accepted: new List<UnresolvedLink>(), Fatal: new List<UnresolvedLink>()),
                    (acc, grp) =>
                    {
                        if (grp.Key) acc.Accepted.AddRange(grp);
                        else acc.Fatal.AddRange(grp);
                        return acc;
                    });

            foreach (var u in accepted)
                Log($"WARNING: acknowledged orphan ID {u.UnresolvablePageId} in {u.SourcePageIdentifier} / {u.FieldName}: {u.Context}");

            if (fatal.Count > 0)
            {
                // A reference leaving THIS predicate's tree is not broken when another content
                // predicate in the same configuration (either mode) ships the target page —
                // e.g. replace pages linking into an excluded subtree that arrives via a merge
                // predicate. Check the on-disk config before declaring the baseline broken.
                var (shipsViaSibling, trulyFatal) = PartitionBySiblingPredicateCoverage(fatal);
                foreach (var (u, via) in shipsViaSibling)
                    Log($"Deferred link: ID {u.UnresolvablePageId} in {u.SourcePageIdentifier} / {u.FieldName} " +
                        $"is outside this predicate but ships via {via} in the same configuration — resolved on target during that pass.");

                // Classify the remaining unresolvables by what the SOURCE database says about
                // their target page:
                //   - Page does not exist → source-side broken link, equally broken before and
                //     after a sync. Auto-acknowledge with a warning (every Swift demo database
                //     ships its own set of dangling ids — a hand-maintained
                //     AcknowledgedOrphanPageIds list can never cover them all).
                //   - Page exists in an area NO Content predicate covers → the link crosses the
                //     boundary the user drew around the sync (e.g. Swift header linking to a
                //     second demo website). Out of scope by configuration, not a baseline
                //     defect: warn and ship as-is.
                //   - Page exists in a COVERED area but ships through no predicate → genuine
                //     coverage gap inside the synced scope; a working source link would land
                //     broken on the target. Fatal.
                var coveredAreaIds = _configuration.Predicates
                    .Where(p => string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.AreaId)
                    .Where(id => id > 0)
                    .ToHashSet();
                var (sourceOrphans, outOfScope, stillFatal) =
                    PartitionUnresolved(trulyFatal, GetPageAreaIdInSource, coveredAreaIds);

                foreach (var u in sourceOrphans)
                    Log($"WARNING: source orphan auto-acknowledged — page ID {u.UnresolvablePageId} does not exist " +
                        $"in the source database; reference in {u.SourcePageIdentifier} / {u.FieldName} ({u.Context}) " +
                        "ships as-is (it is equally broken in the source).");

                foreach (var u in outOfScope)
                    Log($"WARNING: cross-area reference out of sync scope — page ID {u.UnresolvablePageId} lives in " +
                        $"an area no Content predicate covers; reference in {u.SourcePageIdentifier} / {u.FieldName} " +
                        $"({u.Context}) ships as-is and resolves only where the target environment has that content.");

                if (stillFatal.Count > 0)
                {
                    var lines = stillFatal.Select(u =>
                        $"  - ID {u.UnresolvablePageId} in {u.SourcePageIdentifier} / {u.FieldName}: {u.Context}");
                    throw new InvalidOperationException(
                        $"Baseline link sweep found {stillFatal.Count} unresolvable reference(s) to pages that exist " +
                        "inside the synced scope but are shipped by no predicate:\n" +
                        string.Join("\n", lines) +
                        "\nFix the source baseline: include the referenced pages in a predicate path, or remove the references. " +
                        "Known-broken source refs may be listed under AcknowledgedOrphanPageIds on the owning Content predicate.");
                }
            }
        }

        Log($"Serialization complete: {totalPages} pages, {totalGridRows} grid rows, {totalParagraphs} paragraphs serialized.");
    }

    /// <summary>
    /// Split unresolvable references by what the source database says about their target:
    /// non-existent target → source orphan (warn); target in an area no Content predicate
    /// covers → out of sync scope (warn); target inside the synced scope → genuine coverage
    /// gap (fatal). An area lookup of -1 means "lookup failed" and stays fatal.
    /// </summary>
    internal static (List<UnresolvedLink> SourceOrphans, List<UnresolvedLink> OutOfScope, List<UnresolvedLink> StillFatal)
        PartitionUnresolved(IEnumerable<UnresolvedLink> links, Func<int, int?> getPageAreaIdInSource, IReadOnlySet<int> coveredAreaIds)
    {
        var sourceOrphans = new List<UnresolvedLink>();
        var outOfScope = new List<UnresolvedLink>();
        var stillFatal = new List<UnresolvedLink>();
        foreach (var u in links)
        {
            var areaId = getPageAreaIdInSource(u.UnresolvablePageId);
            if (areaId is null)
                sourceOrphans.Add(u);
            else if (areaId > 0 && !coveredAreaIds.Contains(areaId.Value))
                outOfScope.Add(u);
            else
                stillFatal.Add(u);
        }
        return (sourceOrphans, outOfScope, stillFatal);
    }

    /// <summary>
    /// Union of two template reference lists keyed by (kind, path), case-insensitive, with the
    /// referencing pages of duplicates combined. Used by inline-scope runs so the manifest keeps
    /// every reference of the full run.
    /// </summary>
    internal static List<TemplateReference> MergeTemplateReferences(
        IEnumerable<TemplateReference>? existing, IEnumerable<TemplateReference> current) =>
        (existing ?? Enumerable.Empty<TemplateReference>())
            .Concat(current)
            .GroupBy(r => (Kind: r.Kind.ToLowerInvariant(), Path: r.Path.ToLowerInvariant()))
            .Select(g => g.First() with
            {
                ReferencedBy = g.SelectMany(r => r.ReferencedBy)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList()
            })
            .ToList();

    // -------------------------------------------------------------------------
    // Private pipeline
    // -------------------------------------------------------------------------

    private SerializedArea? SerializePredicate(ProviderPredicateDefinition predicate)
    {
        var area = Services.Areas.GetArea(predicate.AreaId);
        if (area == null)
        {
            Log($"Warning: Area with ID {predicate.AreaId} not found. Skipping predicate '{predicate.Name}'.");
            return null;
        }

        Log($"Area found: ID={area.ID}, Name={area.Name}");

        // Build exclude sets from predicate config
        var excludeFields = predicate.ExcludeFields.Count > 0
            ? new HashSet<string>(predicate.ExcludeFields, StringComparer.OrdinalIgnoreCase)
            : null;
        IReadOnlyList<string>? excludeXmlElements = predicate.ExcludeXmlElements.Count > 0
            ? predicate.ExcludeXmlElements
            : null;

        // Get all top-level pages for this area
        var rootPages = Services.Pages.GetRootPagesForArea(predicate.AreaId)
            .OrderBy(p => p.Sort)
            .ToList();

        Log($"Root pages for area {predicate.AreaId}: {rootPages.Count}");
        foreach (var rp in rootPages)
            Log($"  Root page: ID={rp.ID}, MenuText='{rp.MenuText}', Name='{rp.GetDisplayName()}'");

        var serializedPages = new List<SerializedPage>();
        foreach (var rootPage in rootPages)
        {
            var contentPath = "/" + rootPage.MenuText;
            Log($"  Checking predicate for path: '{contentPath}'");
            var serializedPage = SerializePage(rootPage, predicate, contentPath, excludeFields, excludeXmlElements);
            if (serializedPage != null)
                serializedPages.Add(serializedPage);
            else
                Log($"  -> Skipped (predicate excluded or null)");
        }

        // Build area column exclude set (separate from item field excludes)
        var excludeAreaColumns = predicate.ExcludeAreaColumns.Count > 0
            ? new HashSet<string>(predicate.ExcludeAreaColumns, StringComparer.OrdinalIgnoreCase)
            : null;

        Log($"Serialized pages: {serializedPages.Count}");
        // Exclusion dicts are top-level on SerializerConfiguration. ContentSerializer is
        // replace-scoped, so Replace-mode runs always read these dicts; Merge-mode runs are
        // dispatched through the orchestrator + ContentDeserializer.
        var serializedArea = DocumentHeader.Stamp(
            _mapper.MapArea(area, serializedPages, excludeFields, _configuration.ExcludeFieldsByItemType, excludeAreaColumns),
            predicate.Mode);
        _store.WriteTree(serializedArea, _configuration.OutputDirectory);
        return serializedArea;
    }

    private SerializedPage? SerializePage(Page page, ProviderPredicateDefinition predicate, string contentPath, HashSet<string>? excludeFields = null, IReadOnlyList<string>? excludeXmlElements = null)
    {
        // Check predicate inclusion BEFORE loading children (short-circuit optimization).
        // Language-layer pages carry translated MenuTexts, so their own path never matches a
        // predicate authored against the master area — match them in master-path space instead.
        var checkPath = GetPredicateCheckPath(page, contentPath);
        if (!_predicateSet.ShouldInclude(checkPath, predicate.AreaId))
        {
            // Deep-rooted predicate: this page is an ANCESTOR of the predicate's root
            // (e.g. "/Navigation" above "/Navigation/Footer Navigation/Help and info").
            // Without pass-through the walk would prune here and the subtree would
            // silently serialize NOTHING. Emit a structural stub — scalars only — and
            // keep walking; branches with no included descendants are dropped.
            if (IsAncestorOfPredicateRoot(checkPath, predicate))
            {
                var stubChildren = new List<SerializedPage>();
                foreach (var child in Services.Pages.GetPagesByParentID(page.ID).OrderBy(c => c.Sort))
                {
                    var stubChild = SerializePage(child, predicate, contentPath + "/" + child.MenuText, excludeFields, excludeXmlElements);
                    if (stubChild != null)
                        stubChildren.Add(stubChild);
                }
                if (stubChildren.Count == 0)
                    return null;

                Log($"  Ancestor pass-through: '{checkPath}' (page ID={page.ID}) — structural stub above predicate root '{predicate.Path}'");
                return _mapper.MapPage(page, new List<SerializedGridRow>(), stubChildren,
                        new List<SerializedPermission>(), excludeFields, excludeXmlElements,
                        _configuration.ExcludeFieldsByItemType, _configuration.ExcludeXmlElementsByType)
                    with { IsStructuralStub = true };
            }

            Log($"  Predicate excluded: '{checkPath}'");
            return null;
        }
        Log($"  Predicate included: '{checkPath}' (page ID={page.ID})");

        // Fetch grid rows and paragraphs for this page.
        // DW allows multiple rows on the same page to share Sort (default is 0; manual
        // ordering can collide). When that happens, DW falls back to an implicit tiebreaker
        // (row creation order / ID) that is not preserved across DB boundaries — deserialize
        // creates new target rows with different IDs, so the tie resolves differently and
        // visual order flips. Sort by (Sort, ID) to stably match source display order, then
        // renumber SortOrder sequentially (1..N) so the YAML carries canonical order.
        var gridRows = Services.Grids.GetGridRowsByPageId(page.ID)
            .OrderBy(gr => gr.Sort)
            .ThenBy(gr => gr.ID)
            .ToList();

        var allParagraphs = Services.Paragraphs.GetParagraphsByPageId(page.ID)
            .ToList();

        // Map each grid row with its paragraphs grouped into columns
        var serializedGridRows = new List<SerializedGridRow>();
        for (int i = 0; i < gridRows.Count; i++)
        {
            var gridRow = gridRows[i];
            var rowParagraphs = allParagraphs
                .Where(p => p.GridRowId == gridRow.ID)
                .ToList();

            var columns = _mapper.BuildColumns(rowParagraphs,
                p => _permissionMapper.MapPermissions(p, "Paragraph"),
                excludeFields, excludeXmlElements,
                _configuration.ExcludeFieldsByItemType, _configuration.ExcludeXmlElementsByType);
            var rowPermissions = _permissionMapper.MapPermissions(gridRow.ID, "GridRow");
            var serializedGridRow = _mapper.MapGridRow(gridRow, columns, rowPermissions) with { SortOrder = i + 1 };
            serializedGridRows.Add(serializedGridRow);
        }

        // Recursively process child pages
        var childPages = Services.Pages.GetPagesByParentID(page.ID)
            .OrderBy(c => c.Sort)
            .ToList();

        var serializedChildren = new List<SerializedPage>();
        foreach (var child in childPages)
        {
            var childContentPath = contentPath + "/" + child.MenuText;
            var serializedChild = SerializePage(child, predicate, childContentPath, excludeFields, excludeXmlElements);
            if (serializedChild != null)
                serializedChildren.Add(serializedChild);
        }

        var permissions = _permissionMapper.MapPermissions(page.ID, "Page");
        return _mapper.MapPage(page, serializedGridRows, serializedChildren, permissions, excludeFields, excludeXmlElements,
            _configuration.ExcludeFieldsByItemType, _configuration.ExcludeXmlElementsByType);
    }

    /// <summary>
    /// For each fatal unresolved link, checks whether the referenced source page is covered by
    /// ANY content predicate (either mode) in the on-disk Serializer.config.json. Covered links
    /// ship in the same run via that sibling predicate and are deferred (logged, non-fatal);
    /// the rest stay fatal. Conservative on any failure: the link remains fatal.
    /// </summary>
    private static (List<(UnresolvedLink Link, string ShipsVia)> Deferred, List<UnresolvedLink> Fatal)
        PartitionBySiblingPredicateCoverage(List<UnresolvedLink> fatal)
    {
        var deferred = new List<(UnresolvedLink, string)>();

        List<ProviderPredicateDefinition>? contentPredicates = null;
        try
        {
            var configPath = ConfigPathResolver.FindConfigFile();
            if (configPath != null)
            {
                // Expand includeLanguageLayers predicates the same way the serialize pass does:
                // a link to a language-layer page ships via the synthetic per-layer predicate,
                // which exists only after expansion — matching the raw config would leave every
                // layer-page link fatal.
                contentPredicates = LanguageLayerExpander.Expand(
                        ConfigLoader.Load(configPath).Predicates,
                        LanguageLayerExpander.GetLanguageAreaIdsFromDw)
                    .Where(p => string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }
        catch { /* no config readable (unit tests, ad-hoc runs) — keep everything fatal */ }

        if (contentPredicates is null || contentPredicates.Count == 0)
            return (deferred, fatal);

        var trulyFatal = new List<UnresolvedLink>(fatal.Count);
        foreach (var u in fatal)
        {
            try
            {
                var page = Services.Pages.GetPage(u.UnresolvablePageId);
                if (page is null)
                {
                    // The sweeper dual-checks ButtonEditor SelectedValue and #anchors against
                    // paragraph ids — an id that is no page may be a paragraph on a page that
                    // ships via a sibling predicate (e.g. a language-layer copy anchoring its
                    // master's paragraph). Coverage then follows the owning page.
                    var paragraph = Services.Paragraphs.GetParagraph(u.UnresolvablePageId);
                    if (paragraph is not null && paragraph.PageID > 0)
                        page = Services.Pages.GetPage(paragraph.PageID);
                }
                // Layer pages are matched in master-path space (synthetic predicates keep the
                // master's Path), so rebuild the check path via the master chain when present.
                var match = page is null
                    ? null
                    : contentPredicates.FirstOrDefault(p =>
                        new ContentPredicate(p).ShouldInclude(
                            GetPredicateCheckPath(page, ContentPathBuilder.BuildContentPath(page)), page.AreaId));
                if (match is not null)
                    deferred.Add((u, $"'{match.Name}' ({match.Mode})"));
                else
                    trulyFatal.Add(u);
            }
            catch
            {
                trulyFatal.Add(u);
            }
        }
        return (deferred, trulyFatal);
    }

    /// <summary>
    /// True when the predicate's root path lies strictly below this page's path — the page
    /// is on the ancestor chain of a deep-rooted predicate and must pass the walk through.
    /// </summary>
    internal static bool IsAncestorOfPredicateRoot(string checkPath, ProviderPredicateDefinition predicate)
        => predicate.Path != "/"
           && !string.Equals(checkPath, predicate.Path, StringComparison.OrdinalIgnoreCase)
           && ContentPredicate.IsUnderPath(predicate.Path, checkPath);

    /// <summary>
    /// Predicate paths are authored in the master area's path space. For a language-layer page
    /// (MasterPageId > 0) the path is rebuilt from the master page's parent chain; pages without
    /// a master link (master-area pages, or layer-only pages) use their own path.
    /// </summary>
    private static string GetPredicateCheckPath(Page page, string ownPath)
    {
        if (page.MasterPageId <= 0)
            return ownPath;

        try
        {
            var master = Services.Pages.GetPage(page.MasterPageId);
            if (master == null)
                return ownPath;

            var segments = new List<string>();
            var current = master;
            while (current != null)
            {
                segments.Insert(0, current.MenuText ?? string.Empty);
                current = current.ParentPageId > 0 ? Services.Pages.GetPage(current.ParentPageId) : null;
            }
            return "/" + string.Join("/", segments);
        }
        catch
        {
            return ownPath;
        }
    }

    private static void CountItems(IEnumerable<SerializedPage> pages, ref int pageCount, ref int gridRowCount, ref int paragraphCount)
    {
        foreach (var page in pages)
        {
            pageCount++;
            gridRowCount += page.GridRows.Count;
            paragraphCount += page.GridRows.Sum(gr => gr.Columns.Sum(c => c.Paragraphs.Count));
            CountItems(page.Children, ref pageCount, ref gridRowCount, ref paragraphCount);
        }
    }
}
