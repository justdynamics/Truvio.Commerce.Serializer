using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Phase 38.1 W6 (D-38.1-14): shared tree-walk helper used by
/// <see cref="BaselineLinkSweeper"/> and
/// <see cref="Truvio.Commerce.Serializer.Serialization.InternalLinkResolver"/>
/// to visit every <see cref="SerializedParagraph"/> in a page tree.
/// Replaces two divergent private recursive walkers that each peer
/// maintained separately (checker warning W6 in Phase 38).
/// </summary>
internal static class ParagraphIdCollector
{
    /// <summary>
    /// Invokes <paramref name="visitor"/> for every paragraph reachable
    /// via pages → GridRows → Columns → Paragraphs (recursing through
    /// page Children). The visitor is free to accumulate into a
    /// HashSet, a Dictionary, emit diagnostics, etc.
    /// </summary>
    public static void Visit(
        IEnumerable<SerializedPage> pages,
        Action<SerializedParagraph> visitor)
    {
        foreach (var p in pages)
        {
            foreach (var row in p.GridRows)
                foreach (var col in row.Columns)
                    foreach (var para in col.Paragraphs)
                        visitor(para);
            // Foundry #1315: paragraphs placed directly on the page (GridRowId 0) are in no
            // column — without this they are invisible to the paragraph-id map and to the
            // baseline link sweep, exactly as they were invisible to the serializer.
            foreach (var para in p.Paragraphs)
                visitor(para);
            Visit(p.Children, visitor);
        }
    }
}
