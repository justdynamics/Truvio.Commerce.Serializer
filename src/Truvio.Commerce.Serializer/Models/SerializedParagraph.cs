namespace Truvio.Commerce.Serializer.Models;

public record SerializedParagraph
{
    /// <summary>Ownership header (<c>ownership</c>): the mode this document was serialized under.</summary>
    [YamlDotNet.Serialization.YamlMember(Order = -1)]
    public DocumentHeader? Ownership { get; init; }

    public required Guid ParagraphUniqueId { get; init; }
    public int? SourceParagraphId { get; init; }
    public required int SortOrder { get; init; }
    public string? ItemType { get; init; }
    public string? Header { get; init; }
    public string? Template { get; init; }
    public string? ColorSchemeId { get; init; }
    /// <summary>
    /// Foundry #1315: the DW placeholder the paragraph renders into (<c>Paragraph.Container</c>,
    /// e.g. <c>dwcontent</c>). Load-bearing for page-level paragraphs (GridRowId 0), which the
    /// layout renders via <c>Model.Placeholder(...)</c>; empty for grid-placed paragraphs.
    /// </summary>
    public string? Container { get; init; }
    public string? ModuleSystemName { get; init; }
    public string? ModuleSettings { get; init; }
    public Dictionary<string, object> Fields { get; init; } = new();
    /// <summary>
    /// Content ownership R7: the item lists of this document's item, by item field. A field
    /// named here is owned by the document: its list and the list's entries are created,
    /// updated and (under overwrite) removed with the document, by the document's lifecycle.
    /// Null on documents written through 1.0.9-beta, where a list field still travels in
    /// <c>fields</c> as a raw list id.
    /// </summary>
    public Dictionary<string, SerializedItemList>? ItemLists { get; init; }
    /// <summary>Content ownership R7: non-default list options (<c>changedOnLanguage</c>), by item field.</summary>
    public Dictionary<string, SerializedItemListOptions>? ItemListOptions { get; init; }
    public DateTime? CreatedDate { get; init; }
    public DateTime? UpdatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public int? ColumnId { get; init; }
    public List<SerializedPermission> Permissions { get; init; } = new();
}
