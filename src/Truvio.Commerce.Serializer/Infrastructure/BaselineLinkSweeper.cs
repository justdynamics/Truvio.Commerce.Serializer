using System.Text.RegularExpressions;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Phase 37-05 / LINK-02 pass 1 (D-22): describes a single <c>Default.aspx?ID=N</c>
/// or <c>"SelectedValue": "N"</c> reference whose target page is NOT present in the
/// serialized baseline. Emitted by <see cref="BaselineLinkSweeper.Sweep"/> so the
/// serializer can fail with an actionable, source-locatable error.
/// </summary>
public record UnresolvedLink(
    string SourcePageIdentifier,
    string FieldName,
    int UnresolvablePageId,
    string Context);

/// <summary>
/// Result of a <see cref="BaselineLinkSweeper.Sweep"/> run.
/// </summary>
public record SweepResult(IReadOnlyList<UnresolvedLink> Unresolved, int ResolvedCount);

/// <summary>
/// Phase 37-05 / LINK-02 pass 1 (D-22): post-serialize sweep. Walks every page (and
/// its nested children / grid rows / paragraphs) in a freshly-serialized tree,
/// extracts every <c>Default.aspx?ID=N</c> and <c>"SelectedValue": "N"</c>
/// reference, and verifies the target page's <see cref="SerializedPage.SourcePageId"/>
/// is present in the same tree. Unresolved references are returned for the caller
/// to surface as a hard error — a baseline with orphan links is not safe to commit.
/// </summary>
public class BaselineLinkSweeper
{
    private static readonly Regex InternalLinkPattern = new(
        @"(Default\.aspx\?ID=)(\d+)(#(\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SelectedValuePattern = new(
        @"(""SelectedValue"":\s*"")(\d+)("")",
        RegexOptions.Compiled);

    public SweepResult Sweep(List<SerializedPage> allPages)
    {
        var validSourceIds = new HashSet<int>();
        CollectSourceIds(allPages, validSourceIds);

        // Phase 38 B.5 (D-38-09): also collect paragraph source IDs so the sweeper can
        // validate the #Y anchor in Default.aspx?ID=X#Y refs, not just the page X.
        // Phase 38.1 W6 (D-38.1-14): shared ParagraphIdCollector replaces the previous
        // private walker that duplicated InternalLinkResolver's shape.
        var validParagraphIds = new HashSet<int>();
        ParagraphIdCollector.Visit(allPages, para =>
        {
            if (para.SourceParagraphId.HasValue) validParagraphIds.Add(para.SourceParagraphId.Value);
        });

        var unresolved = new List<UnresolvedLink>();
        int resolved = 0;

        foreach (var page in allPages)
            WalkPage(page, PageIdent(page), validSourceIds, validParagraphIds, unresolved, ref resolved);

        return new SweepResult(unresolved, resolved);
    }

    private static string PageIdent(SerializedPage p) =>
        $"page {p.PageUniqueId} ({p.MenuText ?? p.UrlName ?? "unnamed"})";

    private static void CollectSourceIds(IEnumerable<SerializedPage> pages, HashSet<int> acc)
    {
        foreach (var p in pages)
        {
            if (p.SourcePageId.HasValue) acc.Add(p.SourcePageId.Value);
            CollectSourceIds(p.Children, acc);
        }
    }

    private void WalkPage(
        SerializedPage page,
        string ident,
        HashSet<int> validIds,
        HashSet<int> validParagraphIds,
        List<UnresolvedLink> unresolved,
        ref int resolved)
    {
        CheckField(page.ShortCut, ident, "ShortCut", validIds, validParagraphIds, unresolved, ref resolved);

        if (page.NavigationSettings != null)
            CheckField(page.NavigationSettings.ProductPage, ident,
                "NavigationSettings.ProductPage", validIds, validParagraphIds, unresolved, ref resolved);

        foreach (var kvp in page.Fields)
            if (kvp.Value is string s)
                CheckField(s, ident, $"Fields.{kvp.Key}", validIds, validParagraphIds, unresolved, ref resolved);

        foreach (var kvp in page.PropertyFields)
            if (kvp.Value is string s)
                CheckField(s, ident, $"PropertyFields.{kvp.Key}", validIds, validParagraphIds, unresolved, ref resolved);

        // Walk paragraphs
        foreach (var row in page.GridRows)
        {
            foreach (var col in row.Columns)
            {
                foreach (var para in col.Paragraphs)
                {
                    var paraIdent = $"{ident}/paragraph {para.ParagraphUniqueId}";
                    foreach (var kvp in para.Fields)
                        if (kvp.Value is string s)
                            CheckField(s, paraIdent, $"Fields.{kvp.Key}",
                                validIds, validParagraphIds, unresolved, ref resolved);
                }
            }
        }

        // Foundry #1315: paragraphs placed directly on the page (GridRowId 0) are in no column.
        foreach (var para in page.Paragraphs)
        {
            var paraIdent = $"{ident}/paragraph {para.ParagraphUniqueId}";
            foreach (var kvp in para.Fields)
                if (kvp.Value is string s)
                    CheckField(s, paraIdent, $"Fields.{kvp.Key}",
                        validIds, validParagraphIds, unresolved, ref resolved);
        }

        foreach (var c in page.Children)
            WalkPage(c, PageIdent(c), validIds, validParagraphIds, unresolved, ref resolved);
    }

    private static void CheckField(
        string? value,
        string sourceIdent,
        string fieldName,
        HashSet<int> validIds,
        HashSet<int> validParagraphIds,
        List<UnresolvedLink> unresolved,
        ref int resolved)
    {
        if (string.IsNullOrEmpty(value)) return;

        foreach (Match m in InternalLinkPattern.Matches(value))
        {
            if (!int.TryParse(m.Groups[2].Value, out var pageId)) continue;
            if (!validIds.Contains(pageId))
            {
                unresolved.Add(new UnresolvedLink(sourceIdent, fieldName, pageId, m.Value));
                continue;
            }
            // Page resolved — validate the optional #paragraph anchor.
            // Phase 38 B.5 (D-38-09): paragraph anchor must resolve against the
            // SerializedParagraph.SourceParagraphId set collected above. Both parts
            // (page + anchor) must resolve.
            if (m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out var paraId))
            {
                if (!validParagraphIds.Contains(paraId))
                {
                    unresolved.Add(new UnresolvedLink(sourceIdent, fieldName, paraId, m.Value));
                    continue;
                }
            }
            resolved++;
        }

        // Phase 38.1 B.5.1 (D-38.1-02/03/04): dual-check SelectedValue against
        // both page and paragraph source IDs. ButtonEditor JSON with
        // LinkType=paragraph stores a paragraph ID here, not a page ID.
        foreach (Match m in SelectedValuePattern.Matches(value))
        {
            if (!int.TryParse(m.Groups[2].Value, out var id)) continue;
            if (validIds.Contains(id) || validParagraphIds.Contains(id)) { resolved++; continue; }
            unresolved.Add(new UnresolvedLink(sourceIdent, fieldName, id, m.Value));
        }

        // Note: raw-numeric references (plain "121" strings that should resolve to a page ID)
        // are NOT swept here — too many false positives on ordinary numeric fields (sort orders,
        // widths, etc.). The deserialize-time InternalLinkResolver still handles them via its
        // "entire string is a pure number AND is in the source-to-target map" check.
    }
}
