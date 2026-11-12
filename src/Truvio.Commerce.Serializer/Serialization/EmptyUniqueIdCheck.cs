using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R13 (invariant I9): every serialized page, grid row and paragraph must carry
/// a non-empty unique id. Deserialize matches documents by that GUID and leaves
/// <c>00000000-0000-0000-0000-000000000000</c> out of every lookup, so a document without one is
/// never found on the host and is inserted again by every deploy, whatever its mode or lifecycle.
/// Serialize therefore refuses such a tree and names each document, instead of shipping it.
/// </summary>
internal static class EmptyUniqueIdCheck
{
    /// <summary>One line per page, grid row or paragraph whose unique id is <see cref="Guid.Empty"/>.</summary>
    public static List<string> Find(IEnumerable<SerializedPage> pages, string areaLabel)
    {
        var problems = new List<string>();
        foreach (var page in pages)
            Walk(page, areaLabel, "", problems);
        return problems;
    }

    /// <summary>The exception serialize throws for <paramref name="problems"/>.</summary>
    public static InvalidOperationException ToException(string predicateName, IReadOnlyList<string> problems) =>
        new(
            $"Serialize refused predicate '{predicateName}': {problems.Count} document(s) have an empty unique id " +
            "(00000000-0000-0000-0000-000000000000). A document without a GUID can never be matched on the target, " +
            "so every deploy would insert it again:\n" +
            string.Join("\n", problems.Select(p => "  - " + p)) +
            "\nGive each one a GUID on the source (for example UPDATE [Paragraph] SET [ParagraphUniqueId] = NEWID() " +
            "WHERE [ParagraphID] = <id>), then serialize again.");

    private static void Walk(SerializedPage page, string areaLabel, string parentPath, List<string> problems)
    {
        var path = parentPath + "/" + page.MenuText;
        var pageLabel = $"page ID={page.SourcePageId?.ToString() ?? "?"} '{path}' in {areaLabel}";

        if (page.PageUniqueId == Guid.Empty)
            problems.Add($"{pageLabel}: PageUniqueId is empty");

        foreach (var row in page.GridRows)
        {
            if (row.Id == Guid.Empty)
                problems.Add($"grid row sort {row.SortOrder} on {pageLabel}: GridRowUniqueId is empty");
            foreach (var column in row.Columns)
                foreach (var paragraph in column.Paragraphs)
                    CheckParagraph(paragraph, pageLabel, problems);
        }

        foreach (var paragraph in page.Paragraphs)
            CheckParagraph(paragraph, pageLabel, problems);

        foreach (var child in page.Children)
            Walk(child, areaLabel, path, problems);
    }

    private static void CheckParagraph(SerializedParagraph paragraph, string pageLabel, List<string> problems)
    {
        if (paragraph.ParagraphUniqueId == Guid.Empty)
            problems.Add($"paragraph ID={paragraph.SourceParagraphId?.ToString() ?? "?"} on {pageLabel}: ParagraphUniqueId is empty");
    }
}
