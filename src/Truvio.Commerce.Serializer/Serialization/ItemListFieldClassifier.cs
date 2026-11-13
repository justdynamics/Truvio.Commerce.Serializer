using System.Xml.Linq;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: what an item field declared with Dynamicweb's item relation list editor
/// holds. The stored value is an <c>ItemList</c> id; the list's members are items of
/// <see cref="MemberItemType"/>.
/// </summary>
/// <param name="MemberItemType">System name of the item type of the list's entries.</param>
/// <param name="ItemSource">
/// The editor's "Item source": <c>ListItem</c> (inline, the list owns its entries), <c>Page</c>,
/// <c>Paragraph</c> or <c>PageParagraphs</c> (the list points at the items of existing pages and
/// paragraphs). Empty means <c>ListItem</c>, as in Dynamicweb.
/// </param>
public sealed record ItemListFieldInfo(string MemberItemType, string ItemSource)
{
    public const string InlineSource = "ListItem";

    /// <summary>
    /// True when the list owns its entries. Only such a list travels inside its document: the
    /// entries of a list of pages or paragraphs are those pages' and paragraphs' own items, which
    /// their own documents carry.
    /// </summary>
    public bool IsInline =>
        string.IsNullOrWhiteSpace(ItemSource) ||
        ItemSource.Trim().Equals(InlineSource, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Content ownership R7: answers "is this item field an item list, and of which item type?"
/// from the editor declaration. Dependency-free and pure so the rule is unit-tested; the
/// Dynamicweb metadata half is <see cref="ItemListFieldLookup"/> (the same split as
/// <see cref="ReferenceFieldClassifier"/> / <see cref="ReferenceFieldLookup"/>).
/// </summary>
public static class ItemListFieldClassifier
{
    private const string EditorShortName = "ItemRelationListEditor";
    private const string ItemTypeParameter = "Item type";
    private const string ItemSourceParameter = "Item source";

    /// <summary>
    /// True for Dynamicweb's item relation list editor. An assembly-qualified name
    /// ("Type, Assembly") is accepted; only the type part is examined.
    /// </summary>
    public static bool IsItemListEditor(string? editorTypeName)
    {
        if (string.IsNullOrWhiteSpace(editorTypeName)) return false;

        var typeName = editorTypeName.Split(',')[0].Trim();
        var lastDot = typeName.LastIndexOf('.');
        var shortName = lastDot >= 0 ? typeName[(lastDot + 1)..] : typeName;
        return shortName.Equals(EditorShortName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the editor's configuration (the <c>&lt;Parameters&gt;</c> XML Dynamicweb stores with
    /// the field). Null when the editor is not the item relation list editor, the configuration
    /// cannot be read, or it names no item type: such a field is not an item list this engine
    /// can carry, and it keeps travelling as a plain value.
    /// </summary>
    public static ItemListFieldInfo? FromEditorConfiguration(string? editorTypeName, string? configurationXml)
    {
        if (!IsItemListEditor(editorTypeName) || string.IsNullOrWhiteSpace(configurationXml))
            return null;

        try
        {
            return FromParameters(XElement.Parse(configurationXml).DescendantsAndSelf("Parameter"));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The item list fields of an item-type definition as Dynamicweb writes it to
    /// <c>Files/System/Items/ItemType_*.xml</c>, by field system name (case-insensitive).
    /// </summary>
    public static IReadOnlyDictionary<string, ItemListFieldInfo> FromItemTypeXml(string xml)
    {
        var result = new Dictionary<string, ItemListFieldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in XDocument.Parse(xml).Descendants("field"))
        {
            var systemName = (string?)field.Attribute("systemName");
            if (string.IsNullOrWhiteSpace(systemName)) continue;

            var editor = field.Element("editor");
            if (!IsItemListEditor((string?)editor?.Attribute("type"))) continue;

            var info = FromParameters(editor!.Descendants("Parameter"));
            if (info is not null)
                result[systemName] = info;
        }
        return result;
    }

    private static ItemListFieldInfo? FromParameters(IEnumerable<XElement> parameters)
    {
        string? memberItemType = null;
        var itemSource = string.Empty;
        foreach (var parameter in parameters)
        {
            var name = ((string?)parameter.Attribute("name"))?.Trim();
            var value = ((string?)parameter.Attribute("value"))?.Trim();
            if (string.Equals(name, ItemTypeParameter, StringComparison.OrdinalIgnoreCase))
                memberItemType = value;
            else if (string.Equals(name, ItemSourceParameter, StringComparison.OrdinalIgnoreCase))
                itemSource = value ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(memberItemType)
            ? null
            : new ItemListFieldInfo(memberItemType, itemSource);
    }
}
