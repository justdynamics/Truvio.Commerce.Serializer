using Dynamicweb.Content;
using Dynamicweb.Content.UI.Models;
using Dynamicweb.Content.UI.Screens;
using Dynamicweb.CoreUI;
using Dynamicweb.CoreUI.Displays.Information;
using Dynamicweb.CoreUI.Layout;
using Dynamicweb.CoreUI.Screens;
using Truvio.Commerce.Serializer.Configuration;
using static Dynamicweb.CoreUI.Displays.Widgets.CardInfo;

namespace Truvio.Commerce.Serializer.AdminUI.Injectors;

/// <summary>
/// Mode alerts for the content EDITING surfaces. Predicate mode is a per-page property —
/// every grid row and paragraph inherits its page's mode — so rather than per-element
/// badges, each editing screen (visual editor, paragraph dialog, grid-row dialog) gets one
/// screen-level alert for the owning page:
///
///   replace → warning: edits here are overwritten by the next replace run;
///   merge   → info: starter content, local edits are preserved.
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
    public static void Apply(UiComponentBase content, int pageId)
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

            var checkPath = TreeNodeDecorator.GetPredicateCheckPath(page);

            var replaceNames = evaluators.ShowReplaceIndicators
                ? evaluators.Replace?.GetManagingPredicateNames(checkPath, page.AreaId)
                : null;
            if (replaceNames is { Count: > 0 })
            {
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
                    Value = $"This page is replace-managed by '{string.Join("', '", replaceNames)}'. " +
                            "Content here is overwritten by the next replace run — make lasting changes in the source environment." +
                            lastReplaceNote + driftNote
                };
                return;
            }

            if (!evaluators.ShowMergeIndicators)
                return;

            var mergeNames = evaluators.Merge?.GetManagingPredicateNames(checkPath, page.AreaId);
            if (mergeNames is { Count: > 0 })
            {
                AddCarveOutChips(layout, TreeNodeDecorator.GetFieldCarveOuts(page, evaluators), "never filled");
                layout.Alert = new Alert
                {
                    Type = AlertType.Info,
                    Icon = Dynamicweb.CoreUI.Icons.Icon.Flower,
                    // Content ownership R2: true for the page, its rows and its paragraphs now.
                    // An unchecked box or a 0 counts as empty (MergePredicate), so say so.
                    Value = $"Starter content merge-managed by '{string.Join("', '", mergeNames)}'. " +
                            "Edits on this environment are preserved: the next merge run fills only fields left empty " +
                            "(an unchecked box or a 0 counts as empty) and does not move, re-sort or overwrite the page, its rows or its paragraphs."
                };
            }
        }
        catch
        {
            // Alerts are best-effort; never break an editing screen over config issues.
        }
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
        => ModeAlert.Apply(content, screen?.Model?.Id ?? 0);
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
        => ModeAlert.Apply(content, screen?.Model?.PageID ?? 0);
}

/// <summary>Mode alert on the grid-row edit dialog.</summary>
public sealed class SerializerGridRowEditAlertInjector : ScreenInjector<GridRowEditScreen>
{
    public override void OnAfter(GridRowEditScreen screen, UiComponentBase content)
        => ModeAlert.Apply(content, screen?.Model?.PageId ?? 0);
}
