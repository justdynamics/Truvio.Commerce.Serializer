namespace Truvio.Commerce.Serializer.Models;

public record SerializedGridRow
{
    /// <summary>Ownership header (<c>ownership</c>): the mode this document was serialized under.</summary>
    [YamlDotNet.Serialization.YamlMember(Order = -1)]
    public DocumentHeader? Ownership { get; init; }

    public required Guid Id { get; init; }
    public required int SortOrder { get; init; }
    public string? DefinitionId { get; init; }
    public string? ItemType { get; init; }
    public string? Container { get; init; }
    public int? ContainerWidth { get; init; }
    public string? BackgroundImage { get; init; }
    public string? ColorSchemeId { get; init; }
    public int? TopSpacing { get; init; }
    public int? BottomSpacing { get; init; }
    public int? GapX { get; init; }
    public int? GapY { get; init; }
    public string? MobileLayout { get; init; }
    public string? VerticalAlignment { get; init; }
    public string? FlexibleColumns { get; init; }
    public Dictionary<string, object> Fields { get; init; } = new();
    public List<SerializedPermission> Permissions { get; init; } = new();
    public List<SerializedGridColumn> Columns { get; init; } = new();
}
