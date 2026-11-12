using Dynamicweb.Content;
using Dynamicweb.Content.UI.Models;
using Dynamicweb.Content.UI.Screens;
using Dynamicweb.CoreUI;
using Dynamicweb.CoreUI.Displays.Information;
using Dynamicweb.CoreUI.Layout;
using Dynamicweb.CoreUI.Screens;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using static Dynamicweb.CoreUI.Displays.Widgets.CardInfo;

namespace Truvio.Commerce.Serializer.AdminUI.Injectors;

/// <summary>
/// Mode alerts for the content EDITING surfaces. Predicate mode is a per-page property —
/// every grid row and paragraph inherits its page's mode — so rather than per-element
/// badges, each editing screen (visual editor, paragraph dialog, grid-row dialog) gets one
/// screen-level alert for the owning page:
///
///   replace → warning: edits here are overwritten by the next replace run;
///   merge   → info: starter content, local edits are preserved;
///   keep    → info: seeded once, never written again.
///
/// The verdict is for the document the screen edits (the page, its body, one grid row or one
/// paragraph) and comes from <see cref="ModeAlertVerdict"/>, which reads the header serialize
/// stamps on it: predicate keys, bodyOnExisting and ownershipOverrides (content ownership R4).
///
/// Uses <see cref="ScreenLayout.Alert"/>, DW's native screen alert slot. The replace warning
/// shows by default — at the moment of editing, "will my change survive?" is exactly the
/// question being answered — and is gated by the showReplaceIndicators setting (default on).
/// The merge info alert is gated by the same showMergeIndicators setting as the tree's flower
/// icons: with broad merge coverage it would appear on nearly every editing screen and dull
/// the replace warning's signal.
///
/// Field-level carve-outs (e.g. the cart page's eCom_CartV2 settings) render on their own
/// line: a clickable chip in the screen's header info bar per carved-out type, opening the
/// read-only "Stays local" SlideOver with the exact exclusion list. The alert itself stays
/// a single verdict sentence. Screens that don't ship an info bar of their own (the visual
/// editor) get one created — an inline exception sentence in the alert would not be
/// clickable (the Alert component renders a single escaped span).
/// </summary>
internal static class ModeAlert
{
    public static void Apply(UiComponentBase content, int pageId,
        ModeAlertScope scope = ModeAlertScope.Page, int elementId = 0)
    {
        try
        {
            if (pageId <= 0 || !content.TryGet<ScreenLayout>(out var layout) || layout is null || layout.Alert is not null)
                return;

            var evaluators = TreeNodeDecorator.TryCreateEvaluators();
            if (evaluators is null)
                return;

            var page = Services.Pages.GetPage(pageId);
            if (page is null)
                return;

            // Content ownership R3: matched by the same context serialize uses.
            var checkPath = TreeNodeDecorator.GetPredicateContext(page);

            // Every managing predicate of both modes: the indicator settings decide whether the
            // verdict is shown, never which predicates it is decided on.
            var managing = new List<ProviderPredicateDefinition>();
            if (evaluators.Replace is not null)
                managing.AddRange(evaluators.Replace.GetManagingPredicates(checkPath, page.AreaId));
            if (evaluators.Merge is not null)
                managing.AddRange(evaluators.Merge.GetManagingPredicates(checkPath, page.AreaId));

            // Content ownership R1 and R4: the verdict is the header serialize stamps on the
            // document this screen edits (predicate keys, bodyOnExisting, ownershipOverrides),
            // not the predicate's own onExisting.
            var verdict = ModeAlertVerdict.Resolve(managing, BuildTarget(page, checkPath, scope, elementId),
                evaluators.ShowReplaceIndicators, evaluators.ShowMergeIndicators);
            var names = string.Join("', '", verdict.PredicateNames);

            switch (verdict.Kind)
            {
                case ModeAlertKind.Seeded:
                    // Created once and never written again, whatever its mode. Say that, not "overwritten".
                    layout.Alert = new Alert
                    {
                        Type = AlertType.Info,
                        Icon = Dynamicweb.CoreUI.Icons.Icon.Flower,
                        Value = $"Seeded content from '{names}' (onExisting: keep). " +
                                "It was created once; deploys never overwrite, move or re-link it. Edits on this environment are kept."
                    };
                    return;

                case ModeAlertKind.Overwritten:
                    AddCarveOutChips(layout, TreeNodeDecorator.GetFieldCarveOuts(page, evaluators), "stay local");
                    var lastReplaceNote = evaluators.LastReplaceUtc is DateTime lastReplaceUtc
                        ? $" Last replace received: {lastReplaceUtc.ToLocalTime():dd MMM yyyy HH:mm}."
                        : "";
                    var driftNote = TreeNodeDecorator.IsEditedSinceLastReplace(page, evaluators.LastReplaceUtc)
                        ? " This page changed on this environment after that replace run — the next replace run will overwrite those changes."
                        : "";
                    layout.Alert = new Alert
                    {
                        Type = AlertType.Warning,
                        Icon = Dynamicweb.CoreUI.Icons.Icon.Sync,
                        Value = $"This page is replace-managed by '{names}'. " +
                                "Content here is overwritten by the next replace run — make lasting changes in the source environment." +
                                lastReplaceNote + driftNote
                    };
                    return;

                case ModeAlertKind.Filled:
                    AddCarveOutChips(layout, TreeNodeDecorator.GetFieldCarveOuts(page, evaluators), "never filled");
                    layout.Alert = new Alert
                    {
                        Type = AlertType.Info,
                        Icon = Dynamicweb.CoreUI.Icons.Icon.Flower,
                        Value = $"Starter content merge-managed by '{names}'. " +
                                "Edits on this environment are preserved; only fields left empty are filled by the next merge run."
                    };
                    return;
            }
        }
        catch
        {
            // Alerts are best-effort; never break an editing screen over config issues.
        }
    }

    /// <summary>
    /// The document the screen edits, by the GUIDs ownershipOverrides are keyed with. A
    /// language-layer page matches through its master (the predicate context carries the
    /// master's GUID) and a layer paragraph through its master paragraph. A row or paragraph that
    /// is not saved yet, or cannot be read, is judged as the page's body.
    /// </summary>
    internal static ModeAlertTarget BuildTarget(Page page, ContentPageContext context, ModeAlertScope scope, int elementId)
    {
        var target = new ModeAlertTarget
        {
            Scope = scope is ModeAlertScope.Page ? ModeAlertScope.Page : ModeAlertScope.Body,
            PageUniqueId = page.UniqueId,
            MasterPageGuid = context.HasIdentity && context.PageUniqueId != page.UniqueId ? context.PageUniqueId : null
        };
        if (elementId <= 0)
            return target;

        try
        {
            if (scope == ModeAlertScope.GridRow && Services.Grids.GetGridRowById(elementId) is { } row)
                return target with { Scope = ModeAlertScope.GridRow, GridRowUniqueId = row.UniqueId };

            if (scope == ModeAlertScope.Paragraph && Services.Paragraphs.GetParagraph(elementId) is { } paragraph)
            {
                return target with
                {
                    Scope = ModeAlertScope.Paragraph,
                    ParagraphUniqueId = paragraph.UniqueId,
                    MasterParagraphGuid = paragraph.MasterParagraphID > 0
                        ? Services.Paragraphs.GetParagraph(paragraph.MasterParagraphID)?.UniqueId
                        : null,
                    GridRowUniqueId = paragraph.GridRowId > 0
                        ? Services.Grids.GetGridRowById(paragraph.GridRowId)?.UniqueId ?? Guid.Empty
                        : Guid.Empty
                };
            }
        }
        catch
        {
            // fall through: judged as the page's body
        }
        return target;
    }

    /// <summary>
    /// Renders carve-outs as clickable chips in the screen's header info bar (one per type,
    /// e.g. "eCom_CartV2 — 21 settings stay local — view"); clicking opens the read-only
    /// "Stays local" SlideOver for that type. Screens without an info bar (the visual
    /// editor) get one created — there is no clickable fallback inside the alert text.
    /// </summary>
    private static void AddCarveOutChips(ScreenLayout layout, IReadOnlyList<FieldCarveOut> carveOuts, string verdict)
    {
        if (carveOuts.Count == 0)
            return;

        layout.InfoBar ??= new InfoBar();
        layout.InfoBar.Information ??= new Dictionary<string, InfoValue>();
        foreach (var carveOut in carveOuts)
        {
            var key = layout.InfoBar.Information.ContainsKey(carveOut.TypeName)
                ? $"{carveOut.TypeName} (excluded)"
                : carveOut.TypeName;
            var noun = carveOut.Kind == CarveOutKind.XmlElements ? "setting" : "field";
            var text = carveOut.Count == 1
                ? $"1 {noun} {(verdict == "stay local" ? "stays local" : verdict)} — view"
                : $"{carveOut.Count} {noun}s {verdict} — view";
            layout.InfoBar.Information[key] = new InfoValue(text, TreeNodeDecorator.CreateCarveOutNavigation(carveOut));
        }
    }
}

/// <summary>Mode alert on the visual editor (page canvas).</summary>
public sealed class SerializerVisualEditAlertInjector : ScreenInjector<PageVisualEditScreen>
{
    public override void OnAfter(PageVisualEditScreen screen, UiComponentBase content)
        => ModeAlert.Apply(content, screen?.Model?.Id ?? 0, ModeAlertScope.Body);
}

/// <summary>Mode alert on the page properties editor (General/Layout/SEO/Publication tabs) —
/// page settings are page state and replace/merge governs them exactly like paragraph content.</summary>
public sealed class SerializerPageEditAlertInjector : ScreenInjector<PageEditScreen>
{
    public override void OnAfter(PageEditScreen screen, UiComponentBase content)
        => ModeAlert.Apply(content, screen?.Model?.Id ?? 0);
}

/// <summary>Mode alert on the paragraph edit dialog.</summary>
public sealed class SerializerParagraphEditAlertInjector : ScreenInjector<ParagraphEditScreen>
{
    public override void OnAfter(ParagraphEditScreen screen, UiComponentBase content)
        => ModeAlert.Apply(content, screen?.Model?.PageID ?? 0, ModeAlertScope.Paragraph, screen?.Model?.ID ?? 0);
}

/// <summary>Mode alert on the grid-row edit dialog.</summary>
public sealed class SerializerGridRowEditAlertInjector : ScreenInjector<GridRowEditScreen>
{
    public override void OnAfter(GridRowEditScreen screen, UiComponentBase content)
        => ModeAlert.Apply(content, screen?.Model?.PageId ?? 0, ModeAlertScope.GridRow, screen?.Model?.ID ?? 0);
}
