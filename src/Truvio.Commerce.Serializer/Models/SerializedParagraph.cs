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
    public DateTime? CreatedDate { get; init; }
    public DateTime? UpdatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public int? ColumnId { get; init; }
    public List<SerializedPermission> Permissions { get; init; } = new();
}
