using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R2: the grid-row columns a Merge may fill, detached from Dynamicweb's
/// <c>GridRow</c> so the fill rule is testable without a host. A field is filled when the target
/// is empty (null or empty text, a null number) and the YAML carries a value; a set target value
/// is left and counted. Sort is not part of it: Merge never re-sorts an existing row.
/// </summary>
internal sealed class MergeableGridRowFields
{
    public string? DefinitionId { get; set; }
    public string? ItemType { get; set; }
    public string? Container { get; set; }
    public int? ContainerWidth { get; set; }
    public string? BackgroundImage { get; set; }
    public string? ColorSchemeId { get; set; }
    public int? TopSpacing { get; set; }
    public int? BottomSpacing { get; set; }
    public int? GapX { get; set; }
    public int? GapY { get; set; }
    public string? MobileLayout { get; set; }
    public string? FlexibleColumns { get; set; }

    /// <summary>Fills from <paramref name="dto"/>; returns the filled count and adds kept ones to <paramref name="left"/>.</summary>
    public int Fill(SerializedGridRow dto, ref int left)
    {
        var fill = new FillCounter();
        DefinitionId = fill.Text(DefinitionId, dto.DefinitionId);
        ItemType = fill.Text(ItemType, dto.ItemType);
        Container = fill.Text(Container, dto.Container);
        ContainerWidth = fill.Number(ContainerWidth, dto.ContainerWidth);
        BackgroundImage = fill.Text(BackgroundImage, dto.BackgroundImage);
        ColorSchemeId = fill.Text(ColorSchemeId, dto.ColorSchemeId);
        TopSpacing = fill.Number(TopSpacing, dto.TopSpacing);
        BottomSpacing = fill.Number(BottomSpacing, dto.BottomSpacing);
        GapX = fill.Number(GapX, dto.GapX);
        GapY = fill.Number(GapY, dto.GapY);
        MobileLayout = fill.Text(MobileLayout, dto.MobileLayout);
        FlexibleColumns = fill.Text(FlexibleColumns, dto.FlexibleColumns);
        left += fill.Left;
        return fill.Filled;
    }
}

/// <summary>
/// Content ownership R2: the paragraph columns a Merge may fill, detached from Dynamicweb's
/// <c>Paragraph</c>. Same rule as <see cref="MergeableGridRowFields"/>. Module settings are filled
/// element by element (<see cref="XmlMergeHelper"/>) and only when the target runs the same module
/// or none yet. Grid row, column and sort are not part of it: Merge never moves a paragraph.
/// </summary>
internal sealed class MergeableParagraphFields
{
    public string? Header { get; set; }
    public string? Template { get; set; }
    public string? ColorSchemeId { get; set; }
    public string? ItemType { get; set; }
    public string? Container { get; set; }
    public string? ModuleSystemName { get; set; }
    public string? ModuleSettings { get; set; }

    /// <summary>Fills from <paramref name="dto"/>; returns the filled count and adds kept ones to <paramref name="left"/>.</summary>
    public int Fill(SerializedParagraph dto, ref int left)
    {
        var fill = new FillCounter();
        Header = fill.Text(Header, dto.Header);
        Template = fill.Text(Template, dto.Template);
        ColorSchemeId = fill.Text(ColorSchemeId, dto.ColorSchemeId);
        ItemType = fill.Text(ItemType, dto.ItemType);
        Container = fill.Text(Container, dto.Container);

        var sameModule = string.IsNullOrEmpty(ModuleSystemName)
                         || string.Equals(ModuleSystemName, dto.ModuleSystemName, StringComparison.OrdinalIgnoreCase);
        ModuleSystemName = fill.Text(ModuleSystemName, dto.ModuleSystemName);

        if (sameModule && !string.IsNullOrWhiteSpace(dto.ModuleSettings))
        {
            var source = XmlFormatter.Compact(dto.ModuleSettings);
            if (string.IsNullOrWhiteSpace(ModuleSettings))
            {
                ModuleSettings = source;
                fill.Filled++;
            }
            else
            {
                var (merged, fills) = XmlMergeHelper.MergeWithDiagnostics(ModuleSettings, source);
                if (fills.Count > 0 && !string.Equals(merged, ModuleSettings, StringComparison.Ordinal))
                {
                    ModuleSettings = merged;
                    fill.Filled++;
                }
                else
                {
                    fill.Left++;
                }
            }
        }

        left += fill.Left;
        return fill.Filled;
    }
}

internal sealed class FillCounter
{
    public int Filled;
    public int Left;

    public string? Text(string? target, string? source)
    {
        if (string.IsNullOrEmpty(source)) return target;
        if (MergePredicate.IsUnsetForMerge(target)) { Filled++; return source; }
        Left++;
        return target;
    }

    public int? Number(int? target, int? source)
    {
        if (source is null) return target;
        if (target is null) { Filled++; return source; }
        Left++;
        return target;
    }
}
