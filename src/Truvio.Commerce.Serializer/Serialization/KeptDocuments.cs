namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R1: the host documents one Content entry left untouched because their header
/// says <c>onExisting: keep</c> and they already existed, plus the paragraphs the entry did write.
/// The post-write passes (link resolution, master links) consult it so a kept document is never
/// rewritten after the fact: an editor's link must not be re-read as a source id (gap G10).
/// </summary>
internal sealed class KeptDocuments
{
    /// <summary>Target ids of existing pages whose page document was kept.</summary>
    public HashSet<int> PageIds { get; } = new();

    /// <summary>Target ids of existing paragraphs that were kept.</summary>
    public HashSet<int> ParagraphIds { get; } = new();

    /// <summary>Target ids of paragraphs this entry created or updated.</summary>
    public HashSet<int> WrittenParagraphIds { get; } = new();

    /// <summary>True when the entry owns area state and kept the existing area row and item.</summary>
    public bool AreaKept { get; set; }

    /// <summary>
    /// Whether the page's own fields (item, property item, shortcut, product page) are
    /// link-resolved: not when the page was kept.
    /// </summary>
    public bool ResolvePageFields(int pageId) => !PageIds.Contains(pageId);

    /// <summary>
    /// Whether a paragraph on <paramref name="pageId"/> is link-resolved. A kept paragraph never
    /// is. On a kept page only paragraphs this entry wrote are (an anchor, or a missing one it
    /// created), because every other paragraph there belongs to the host's editors. On any other
    /// page every paragraph is, as before.
    /// </summary>
    public bool ResolveParagraph(int pageId, int paragraphId)
    {
        if (ParagraphIds.Contains(paragraphId))
            return false;
        if (PageIds.Contains(pageId))
            return WrittenParagraphIds.Contains(paragraphId);
        return true;
    }
}
