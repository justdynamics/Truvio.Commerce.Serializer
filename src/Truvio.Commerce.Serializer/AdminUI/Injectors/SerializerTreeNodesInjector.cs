using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.AdminUI.Queries;
using Truvio.Commerce.Serializer.AdminUI.Screens;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Dynamicweb.Application.UI.TreeNavigation;
using Dynamicweb.Content;
using Dynamicweb.Content.UI;
using Dynamicweb.CoreUI;
using Dynamicweb.CoreUI.Actions;
using Dynamicweb.CoreUI.Actions.Implementations;
using Dynamicweb.CoreUI.Icons;
using Dynamicweb.CoreUI.Navigation;
using Dynamicweb.CoreUI.Screens;

namespace Truvio.Commerce.Serializer.AdminUI.Injectors;

/// <summary>
/// Decorates Content-tree page nodes on tree expansion / section loads (TreeNodesScreen):
/// an annotation icon on pages covered by a replace-mode content predicate, and a right-click
/// "View excluded fields" action on pages with field-level carve-outs.
/// Auto-discovered by DW's AddInManager. The initial full-tree render goes through
/// <see cref="TreeScreen"/> instead — covered by <see cref="SerializerTreeInjector"/>.
/// </summary>
public sealed class SerializerTreeNodesInjector : ScreenInjector<TreeNodesScreen>
{
    public override void OnAfter(TreeNodesScreen screen, UiComponentBase content)
    {
        if (!TreeNodeDecorator.IsContentAreaPath(screen?.Model?.Path))
            return;

        if (!content.TryGet<TreeNodes>(out var tree) || tree is null)
            return;

        var evaluators = TreeNodeDecorator.TryCreateEvaluators();
        foreach (var node in tree.Nodes)
            TreeNodeDecorator.Decorate(node, evaluators);
    }
}

/// <summary>
/// Same decoration for the initial full-tree render (sections + root nodes), which goes
/// through <see cref="TreeScreen"/> rather than <see cref="TreeNodesScreen"/>.
/// </summary>
public sealed class SerializerTreeInjector : ScreenInjector<TreeScreen>
{
    public override void OnAfter(TreeScreen screen, UiComponentBase content)
    {
        if (!TreeNodeDecorator.IsContentAreaPath(screen?.Model?.Path))
            return;

        if (!content.TryGet<Dynamicweb.CoreUI.Navigation.Tree>(out var tree) || tree is null)
            return;

        var evaluators = TreeNodeDecorator.TryCreateEvaluators();
        foreach (var section in tree.Sections)
            foreach (var node in section.Nodes)
                TreeNodeDecorator.Decorate(node, evaluators);
    }
}

/// <summary>
/// Shared node decoration for the two tree screens.
/// </summary>
internal static class TreeNodeDecorator
{
    public static bool IsContentAreaPath(NavigationNodePath? path)
        => path is not null
           && string.Equals(path.First, typeof(ContentArea).FullName, StringComparison.Ordinal);

    /// <summary>
    /// Per-mode coverage evaluators for tree annotations and edit-screen alerts.
    /// <paramref name="ShowMergeIndicators"/> gates every merge-mode cue — the tree's flower
    /// icon AND the merge info alert on editing screens (config: showMergeIndicators, default
    /// off — broad merge coverage drowns the replace/partial cues, which carry the actionable
    /// signal). <paramref name="ShowReplaceIndicators"/> gates every replace-mode cue the same
    /// way (config: showReplaceIndicators, default ON — the replace warnings carry the
    /// actionable signal, but e.g. a source environment can switch them off). The exclusion
    /// dicts ride along so per-page field-level carve-outs (e.g. the cart page's eCom_CartV2
    /// settings) can downgrade a "fully managed" verdict to partial.
    /// </summary>
    internal sealed record CoverageEvaluators(
        ContentCoverageEvaluator? Replace,
        ContentCoverageEvaluator? Merge,
        bool ShowMergeIndicators,
        bool ShowReplaceIndicators,
        IReadOnlyDictionary<string, List<string>> ExcludeFieldsByItemType,
        IReadOnlyDictionary<string, List<string>> ExcludeXmlElementsByType,
        DateTime? LastReplaceUtc);

    public static void Decorate(NavigationNode node, CoverageEvaluators? evaluators)
    {
        if (int.TryParse(node.Id, out var pageId) && pageId > 0)
        {
            var page = Services.Pages.GetPage(pageId);
            if (page is not null)
            {
                IReadOnlyList<FieldCarveOut> carveOuts = Array.Empty<FieldCarveOut>();

                if (evaluators is not null)
                {
                    // Language-layer pages are matched in their master's path space — predicate
                    // paths are authored against the master area (same rule as serialize time).
                    var checkPath = GetPredicateCheckPath(page);

                    var replace = evaluators.ShowReplaceIndicators
                        ? evaluators.Replace?.Evaluate(checkPath, page.AreaId)
                        : null;
                    var merge = evaluators.ShowMergeIndicators
                        ? evaluators.Merge?.Evaluate(checkPath, page.AreaId)
                        : null;

                    // Field-level carve-outs apply only when the page ITSELF is managed —
                    // the "contains managed subtrees" flavour of Partial carries no page
                    // content to carve from.
                    var replaceManagesPage = replace is not null && replace.Coverage != ContentCoverage.None
                        && evaluators.Replace!.GetManagingPredicateNames(checkPath, page.AreaId).Count > 0;
                    var mergeManagesPage = merge is not null && merge.Coverage != ContentCoverage.None
                        && evaluators.Merge!.GetManagingPredicateNames(checkPath, page.AreaId).Count > 0;
                    if (replaceManagesPage || mergeManagesPage)
                        carveOuts = GetFieldCarveOuts(page, evaluators);

                    if (replace is not null && replace.Coverage != ContentCoverage.None)
                    {
                        var explanation = replace.Explanation;
                        var isPartial = replace.Coverage != ContentCoverage.Full;
                        if (replaceManagesPage && carveOuts.Count > 0)
                        {
                            // Cart-page case: path algebra says fully managed, but excluded
                            // fields/settings on this page stay local — show partial.
                            isPartial = true;
                            explanation = $"Partially managed — {explanation}; excluded on this page: "
                                + string.Join("; ", carveOuts.Select(c => c.Label))
                                + ". Right-click > Truvio Serializer to view the excluded fields.";
                        }
                        if (replaceManagesPage && IsEditedSinceLastReplace(page, evaluators.LastReplaceUtc))
                        {
                            explanation += " — changed on this environment after the last replace run; the next replace run will overwrite those changes";
                        }
                        node.Annotations.Add(new ActionNode
                        {
                            Name = explanation,
                            Icon = isPartial ? Icon.SyncSlash : Icon.Sync,
                            Sort = 200
                        });
                    }

                    if (merge is not null && merge.Coverage != ContentCoverage.None)
                    {
                        var explanation = $"{merge.Explanation} (merge fills empty fields once; edits on this environment are preserved)";
                        if (mergeManagesPage && carveOuts.Count > 0)
                            explanation += $" — never filled (excluded by type): {string.Join("; ", carveOuts.Select(c => c.Label))}";
                        node.Annotations.Add(new ActionNode
                        {
                            Name = explanation,
                            Icon = Icon.Flower,
                            Sort = 210
                        });
                    }
                }

                // Context menu: a click-through per carve-out type, so "WHICH 21 settings?" is
                // one right-click away from the icon that raised it.
                var groupNodes = new List<ActionNode>();
                if (carveOuts.Count > 0)
                {
                    // ONE short entry regardless of how many types are carved out — per-type
                    // labels in the context menu bloat it; the detail SlideOver lists them all.
                    groupNodes.Add(new ActionNode
                    {
                        Name = "View excluded fields",
                        Icon = Icon.ListUl,
                        NodeAction = OpenSlideOverAction.To<CarveOutDetailScreen>()
                            .With(new CarveOutDetailQuery { Kind = CarveOutDetailModel.KindPage, PageId = pageId })
                    });
                }

                if (groupNodes.Count > 0)
                {
                    node.ContextActionGroups = node.ContextActionGroups.Append(new ActionGroup
                    {
                        Nodes = groupNodes
                    });
                }
            }
        }

        // OpenTo deep-links and section loads can deliver pre-expanded children.
        foreach (var child in node.Nodes)
            Decorate(child, evaluators);
    }

    /// <summary>
    /// Drift v1: was this page edited on THIS environment after the last replace run landed?
    /// Timestamp-based (page audit date vs. the newest replace-received log summary) with a
    /// 5-minute grace margin so pages the replace run itself wrote don't read as drifted.
    /// Occasional false positives from system touches are the accepted v1 tradeoff; the
    /// honest alternative is a per-page YAML diff.
    /// </summary>
    internal static bool IsEditedSinceLastReplace(Page page, DateTime? lastReplaceUtc)
    {
        if (lastReplaceUtc is null)
            return false;
        try
        {
            var threshold = lastReplaceUtc.Value.ToLocalTime() + TimeSpan.FromMinutes(5);
            return page.Audit?.LastModifiedAt > threshold;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Click-through to the exact exclusion list behind a carve-out. Opens the read-only
    /// "Stays local" SlideOver (CarveOutDetailScreen) — works from every context including
    /// SlideOver-hosted editors (where navigation actions are suppressed by the frontend)
    /// and needs no Settings permission. Used by the editing-screen header chips.
    /// </summary>
    internal static Dynamicweb.CoreUI.Actions.ActionBase CreateCarveOutNavigation(FieldCarveOut carveOut) =>
        OpenSlideOverAction.To<CarveOutDetailScreen>()
            .With(new CarveOutDetailQuery
            {
                TypeName = carveOut.TypeName,
                Kind = carveOut.Kind == CarveOutKind.XmlElements
                    ? CarveOutDetailModel.KindXmlElements
                    : CarveOutDetailModel.KindItemTypeFields
            });

    /// <summary>
    /// Builds per-mode coverage evaluators from the Content predicates, expanded for
    /// language layers (so language-area pages report coverage like their masters).
    /// Built once per tree render; null when no config / no Content predicates exist.
    /// </summary>
    internal static CoverageEvaluators? TryCreateEvaluators()
    {
        try
        {
            var configPath = ConfigPathResolver.FindConfigFile();
            if (configPath == null)
                return null;

            var config = ConfigLoader.Load(configPath);
            var contentPredicates = config.Predicates
                .Where(p => string.Equals(p.ProviderType, "Content", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (contentPredicates.Count == 0)
                return null;

            ContentCoverageEvaluator? Build(SerializerMode mode, string word)
            {
                var modePredicates = contentPredicates.Where(p => p.Mode == mode).ToList();
                if (modePredicates.Count == 0)
                    return null;
                var expanded = LanguageLayerExpander.Expand(
                    modePredicates, LanguageLayerExpander.GetLanguageAreaIdsFromDw);
                return new ContentCoverageEvaluator(expanded, word);
            }

            var replace = Build(SerializerMode.Replace, "replace");
            var merge = Build(SerializerMode.Merge, "merge");
            return replace is null && merge is null
                ? null
                : new CoverageEvaluators(replace, merge, config.ShowMergeIndicators,
                    config.ShowReplaceIndicators,
                    config.ExcludeFieldsByItemType, config.ExcludeXmlElementsByType,
                    Reporting.LastRunResolver.FindLastReplaceReceivedUtc());
        }
        catch
        {
            // Tree decoration is best-effort; never break the admin tree over config issues.
            return null;
        }
    }

    /// <summary>
    /// Field-level carve-outs for one page: types found on the page (page item type, URL
    /// provider, paragraph item types / module settings) that have non-empty entries in the
    /// global exclusion dicts. Loads the page's paragraphs — call only for pages a predicate
    /// actually manages. Best-effort: an unreadable page reports no carve-outs.
    /// </summary>
    internal static IReadOnlyList<FieldCarveOut> GetFieldCarveOuts(Page page, CoverageEvaluators evaluators)
    {
        if (evaluators.ExcludeFieldsByItemType.Count == 0 && evaluators.ExcludeXmlElementsByType.Count == 0)
            return Array.Empty<FieldCarveOut>();

        try
        {
            var paragraphs = Services.Paragraphs.GetParagraphsByPageId(page.ID)
                .Select(p => ((string?)p.ItemType, (string?)p.ModuleSystemName));
            return FieldExclusionInspector.Describe(
                page.ItemType, page.UrlDataProviderTypeName, paragraphs,
                evaluators.ExcludeFieldsByItemType, evaluators.ExcludeXmlElementsByType);
        }
        catch
        {
            return Array.Empty<FieldCarveOut>();
        }
    }

    /// <summary>
    /// Predicate paths live in the master area's path space; for a language-layer page
    /// (MasterPageId > 0) rebuild the path from the master chain, otherwise use its own path.
    /// </summary>
    internal static string GetPredicateCheckPath(Page page)
    {
        if (page.MasterPageId <= 0)
            return Truvio.Commerce.Serializer.Serialization.ContentPathBuilder.BuildContentPath(page);

        try
        {
            var master = Services.Pages.GetPage(page.MasterPageId);
            return Truvio.Commerce.Serializer.Serialization.ContentPathBuilder.BuildContentPath(master ?? page);
        }
        catch
        {
            return Truvio.Commerce.Serializer.Serialization.ContentPathBuilder.BuildContentPath(page);
        }
    }
}
