using Dynamicweb.Content;
using Dynamicweb.Data;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Maps Truvio Commerce content objects (Area, Page, GridRow, Paragraph) to the corresponding DTOs.
/// Pure conversion — no traversal, no I/O.
/// </summary>
public class ContentMapper
{
    private readonly ReferenceResolver _resolver;

    public ContentMapper(ReferenceResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Maps a DW Area to a SerializedArea DTO.
    /// </summary>
    public SerializedArea MapArea(Area area, List<SerializedPage> pages,
        IReadOnlySet<string>? excludeFields = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlySet<string>? excludeAreaColumns = null)
    {
        // Merge flat predicate exclusions with per-item-type dictionary for this area's item type
        var effectiveExcludeFields = excludeFieldsByItemType != null && !string.IsNullOrEmpty(area.ItemType)
            ? ExclusionMerger.MergeFieldExclusions(
                excludeFields?.ToList() ?? new List<string>(),
                excludeFieldsByItemType,
                area.ItemType)
            : excludeFields;

        var itemFields = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(area.ItemType) && !string.IsNullOrEmpty(area.ItemId))
        {
            var itemEntry = Services.Items.GetItem(area.ItemType, area.ItemId);
            if (itemEntry != null)
            {
                var dict = new Dictionary<string, object?>();
                itemEntry.SerializeTo(dict);
                foreach (var kvp in dict)
                {
                    if (kvp.Value != null && effectiveExcludeFields?.Contains(kvp.Key) != true)
                        itemFields[kvp.Key] = kvp.Value;
                }
            }
        }

        return new SerializedArea
        {
            AreaId = area.UniqueId,
            Name = area.Name ?? string.Empty,
            SortOrder = area.Sort,
            ItemType = area.ItemType,
            ItemFields = itemFields,
            Properties = ReadAreaProperties(area.ID, effectiveExcludeFields, excludeAreaColumns),
            Pages = pages
        };
    }

    /// <summary>
    /// Maps a DW Page to a SerializedPage DTO.
    /// </summary>
    public SerializedPage MapPage(Page page, List<SerializedGridRow> gridRows, List<SerializedPage> children,
        List<SerializedPermission> permissions,
        IReadOnlySet<string>? excludeFields = null, IReadOnlyList<string>? excludeXmlElements = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        // Merge flat predicate exclusions with per-item-type dictionary for this page's item type
        var effectiveExcludeFields = excludeFieldsByItemType != null
            ? ExclusionMerger.MergeFieldExclusions(
                excludeFields?.ToList() ?? new List<string>(),
                excludeFieldsByItemType,
                page.ItemType)
            : excludeFields;
        var effectiveXmlExclusions = excludeXmlElementsByType != null
            ? ExclusionMerger.MergeXmlExclusions(
                (IReadOnlyList<string>?)excludeXmlElements ?? Array.Empty<string>(),
                excludeXmlElementsByType,
                page.UrlDataProviderTypeName)
            : excludeXmlElements;

        var fields = ExtractItemFields(page.Item, effectiveExcludeFields);
        var propertyFields = ExtractPropertyItemFields(page, effectiveExcludeFields);

        // Language-layer pages reference their master page (in the master area) by numeric ID;
        // resolve to GUID so the link survives cross-environment deserialization.
        Guid? masterPageGuid = page.MasterPageId > 0
            ? _resolver.ResolvePageGuid(page.MasterPageId)
            : null;

        return new SerializedPage
        {
            PageUniqueId = page.UniqueId,
            SourcePageId = page.ID,
            // DW10 Page does not have a distinct Name property; MenuText is the navigation/display label.
            Name = page.MenuText ?? string.Empty,
            MenuText = page.MenuText ?? string.Empty,
            UrlName = page.UrlName ?? string.Empty,
            SortOrder = page.Sort,
            IsActive = page.Active,
            ItemType = page.ItemType,
            Layout = page.LayoutTemplate,
            LayoutApplyToSubPages = page.LayoutApplyToSubPages,
            IsFolder = page.IsFolder,
            IsTemplate = page.IsTemplate,
            TreeSection = page.TreeSection,
            NavigationTag = page.NavigationTag,
            ShortCut = page.ShortCut,
            Hidden = page.Hidden,
            Allowclick = page.Allowclick,
            Allowsearch = page.Allowsearch,
            ShowInSitemap = page.ShowInSitemap,
            ShowInLegend = page.ShowInLegend,
            SslMode = page.SslMode,
            ColorSchemeId = page.ColorSchemeId,
            ExactUrl = page.ExactUrl,
            ContentType = page.ContentType,
            TopImage = page.TopImage,
            DisplayMode = page.DisplayMode.ToString(),
            ActiveFrom = page.ActiveFrom,
            ActiveTo = page.ActiveTo,
            PermissionType = page.PermissionType,
            MasterPageGuid = masterPageGuid,
            MasterType = page.MasterPageId > 0 ? page.MasterType.ToString() : null,
            Seo = new SerializedSeoSettings
            {
                MetaTitle = page.MetaTitle,
                MetaCanonical = page.MetaCanonical,
                Description = page.Description,
                Keywords = page.Keywords,
                Noindex = page.Noindex,
                Nofollow = page.Nofollow,
                Robots404 = page.Robots404
            },
            UrlSettings = new SerializedUrlSettings
            {
                UrlDataProviderTypeName = page.UrlDataProviderTypeName,
                UrlDataProviderParameters = ApplyXmlElementFilter(XmlFormatter.PrettyPrint(page.UrlDataProviderParameters), effectiveXmlExclusions),
                UrlIgnoreForChildren = page.UrlIgnoreForChildren,
                UrlUseAsWritten = page.UrlUseAsWritten
            },
            Visibility = new SerializedVisibilitySettings
            {
                HideForPhones = page.HideForPhones,
                HideForTablets = page.HideForTablets,
                HideForDesktops = page.HideForDesktops
            },
            NavigationSettings = MapNavigationSettings(page.NavigationSettings),
            Fields = fields,
            PropertyFields = propertyFields,
            Permissions = permissions,
            GridRows = gridRows,
            Children = children
        };
    }

    /// <summary>
    /// Maps a DW GridRow to a SerializedGridRow DTO.
    /// </summary>
    public SerializedGridRow MapGridRow(GridRow gridRow, List<SerializedGridColumn> columns,
        List<SerializedPermission> permissions)
    {
        var fields = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(gridRow.ItemType) && !string.IsNullOrEmpty(gridRow.ItemId))
        {
            var itemEntry = Services.Items.GetItem(gridRow.ItemType, gridRow.ItemId);
            if (itemEntry != null)
            {
                var dict = new Dictionary<string, object?>();
                itemEntry.SerializeTo(dict);
                foreach (var kvp in dict)
                {
                    if (kvp.Value != null)
                        fields[kvp.Key] = kvp.Value;
                }
            }
        }

        return new SerializedGridRow
        {
            Id = gridRow.UniqueId,
            SortOrder = gridRow.Sort,
            DefinitionId = gridRow.DefinitionId,
            ItemType = gridRow.ItemType,
            Container = gridRow.Container,
            ContainerWidth = gridRow.ContainerWidth,
            BackgroundImage = gridRow.BackgroundImage,
            ColorSchemeId = gridRow.ColorSchemeId,
            TopSpacing = gridRow.TopSpacing,
            BottomSpacing = gridRow.BottomSpacing,
            GapX = gridRow.GapX,
            GapY = gridRow.GapY,
            MobileLayout = gridRow.MobileLayout,
            VerticalAlignment = gridRow.VerticalAlignment.ToString(),
            FlexibleColumns = gridRow.FlexibleColumns,
            Fields = fields,
            Permissions = permissions,
            Columns = columns
        };
    }

    /// <summary>
    /// Maps a DW Paragraph to a SerializedParagraph DTO.
    /// Registers the paragraph with the ReferenceResolver and resolves known reference fields to GUIDs.
    /// </summary>
    public SerializedParagraph MapParagraph(Paragraph paragraph,
        List<SerializedPermission> permissions,
        IReadOnlySet<string>? excludeFields = null, IReadOnlyList<string>? excludeXmlElements = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        // Register this paragraph so later cross-references to it can be resolved
        _resolver.RegisterParagraph(paragraph.ID, paragraph.UniqueId);

        // Merge flat predicate exclusions with per-item-type dictionary for this paragraph's item type
        var effectiveExcludeFields = excludeFieldsByItemType != null
            ? ExclusionMerger.MergeFieldExclusions(
                excludeFields?.ToList() ?? new List<string>(),
                excludeFieldsByItemType,
                paragraph.ItemType)
            : excludeFields;
        var effectiveXmlExclusions = excludeXmlElementsByType != null
            ? ExclusionMerger.MergeXmlExclusions(
                (IReadOnlyList<string>?)excludeXmlElements ?? Array.Empty<string>(),
                excludeXmlElementsByType,
                paragraph.ModuleSystemName)
            : excludeXmlElements;

        var fields = ExtractItemFields(paragraph.Item, effectiveExcludeFields);

        // Include paragraph body text if present
        if (!string.IsNullOrEmpty(paragraph.Text))
            fields["Text"] = paragraph.Text;

        // Resolve known numeric reference fields to GUIDs — do NOT serialize raw numeric IDs
        if (paragraph.MasterParagraphID > 0)
        {
            var guid = _resolver.ResolveParagraphGuid(paragraph.MasterParagraphID);
            if (guid.HasValue)
                fields["MasterParagraphGuid"] = guid.Value.ToString();
        }

        if (paragraph.GlobalRecordPageID > 0)
        {
            var guid = _resolver.ResolvePageGuid(paragraph.GlobalRecordPageID);
            if (guid.HasValue)
                fields["GlobalRecordPageGuid"] = guid.Value.ToString();
        }

        return new SerializedParagraph
        {
            ParagraphUniqueId = paragraph.UniqueId,
            SourceParagraphId = paragraph.ID,
            SortOrder = paragraph.Sort,
            ItemType = paragraph.ItemType,
            Header = paragraph.Header,
            Template = paragraph.Template,
            ColorSchemeId = paragraph.ColorSchemeId,
            // Foundry #1315: the placeholder a page-level paragraph renders into.
            Container = paragraph.Container,
            ModuleSystemName = paragraph.ModuleSystemName,
            ModuleSettings = ApplyXmlElementFilter(XmlFormatter.PrettyPrint(paragraph.ModuleSettings), effectiveXmlExclusions),
            Fields = fields,
            Permissions = permissions
        };
    }

    /// <summary>
    /// Groups paragraphs by GridRowColumn to reconstruct column structure.
    /// Returns a single empty column if no paragraphs are provided.
    /// </summary>
    public List<SerializedGridColumn> BuildColumns(IEnumerable<Paragraph> paragraphs,
        Func<int, List<SerializedPermission>> permissionResolver,
        IReadOnlySet<string>? excludeFields = null, IReadOnlyList<string>? excludeXmlElements = null,
        IReadOnlyDictionary<string, List<string>>? excludeFieldsByItemType = null,
        IReadOnlyDictionary<string, List<string>>? excludeXmlElementsByType = null)
    {
        var paragraphList = paragraphs.ToList();

        if (paragraphList.Count == 0)
        {
            return new List<SerializedGridColumn>
            {
                new SerializedGridColumn { Id = 1, Width = 0 }
            };
        }

        var columns = paragraphList
            .GroupBy(p => p.GridRowColumn)
            .OrderBy(g => g.Key)
            .Select(g => new SerializedGridColumn
            {
                Id = g.Key,
                Width = 0, // Column width not available from Paragraph; GridRow definition has this
                Paragraphs = g.OrderBy(p => p.Sort)
                              .Select(p => MapParagraph(p, permissionResolver(p.ID), excludeFields, excludeXmlElements, excludeFieldsByItemType, excludeXmlElementsByType) with { ColumnId = g.Key })
                              .ToList()
            })
            .ToList();

        return columns;
    }

    /// <summary>
    /// Maps DW PageNavigationSettings to DTO. Returns null when UseEcomGroups is false
    /// (DW only populates NavigationSettings when ecommerce navigation is enabled).
    /// </summary>
    private static SerializedNavigationSettings? MapNavigationSettings(
        Dynamicweb.Content.PageNavigationSettings? navSettings)
    {
        if (navSettings == null || !navSettings.UseEcomGroups)
            return null;

        return new SerializedNavigationSettings
        {
            UseEcomGroups = true,
            ParentType = navSettings.ParentType.ToString(),
            Groups = navSettings.Groups,
            ShopID = navSettings.ShopID,
            MaxLevels = navSettings.MaxLevels,
            ProductPage = navSettings.ProductPage,
            NavigationProvider = navSettings.NavigationProvider,
            IncludeProducts = navSettings.IncludeProducts
        };
    }

    // -------------------------------------------------------------------------
    // Area SQL property reading
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads all columns from the [Area] SQL table for a given area.
    /// The DW Area C# class does not expose all 60+ columns as properties,
    /// so direct SQL is the only way to capture the full table state.
    /// Columns already represented by named SerializedArea properties are removed to avoid duplication.
    /// </summary>
    private static Dictionary<string, object> ReadAreaProperties(int areaId, IReadOnlySet<string>? excludeFields, IReadOnlySet<string>? excludeAreaColumns = null)
    {
        var props = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var cb = new CommandBuilder();
        cb.Add("SELECT * FROM [Area] WHERE [AreaID] = {0}", areaId);
        using var reader = Database.CreateDataReader(cb);
        if (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var value = reader.GetValue(i);
                if (value != DBNull.Value
                    && excludeFields?.Contains(name) != true
                    && excludeAreaColumns?.Contains(name) != true)
                    props[name] = value;
            }
        }
        // Remove columns already captured by named DTO properties to avoid duplication
        props.Remove("AreaID");
        props.Remove("AreaName");
        props.Remove("AreaSort");
        props.Remove("AreaItemType");
        props.Remove("AreaItemId");
        props.Remove("AreaUniqueId");
        return props;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Dictionary<string, object> ExtractItemFields(Dynamicweb.Content.Items.Item? item, IReadOnlySet<string>? excludeFields = null)
    {
        var fields = new Dictionary<string, object>();

        if (item == null)
            return fields;

        foreach (var fieldName in item.Names)
        {
            if (excludeFields?.Contains(fieldName) == true)
                continue;

            var value = item[fieldName];
            if (value != null)
                fields[fieldName] = value;
        }

        return fields;
    }

    /// <summary>
    /// Extracts PropertyItem fields (e.g. Icon, SubmenuType) from a page's PropertyItem.
    /// These are separate from the page's own Item fields.
    /// </summary>
    private static Dictionary<string, object> ExtractPropertyItemFields(Page page, IReadOnlySet<string>? excludeFields = null)
    {
        var fields = new Dictionary<string, object>();

        if (string.IsNullOrEmpty(page.PropertyItemId))
            return fields;

        var propItem = page.PropertyItem;
        if (propItem == null)
            return fields;

        var dict = new Dictionary<string, object?>();
        propItem.SerializeTo(dict);
        foreach (var kvp in dict)
        {
            if (kvp.Value != null && excludeFields?.Contains(kvp.Key) != true)
                fields[kvp.Key] = kvp.Value;
        }

        return fields;
    }

    /// <summary>
    /// Applies XML element filtering if excludeXmlElements is configured.
    /// Returns the XML unchanged if no elements to exclude.
    /// </summary>
    private static string? ApplyXmlElementFilter(string? xml, IReadOnlyList<string>? excludeXmlElements)
    {
        if (excludeXmlElements == null || excludeXmlElements.Count == 0)
            return xml;

        return XmlFormatter.RemoveElements(xml, excludeXmlElements);
    }
}
