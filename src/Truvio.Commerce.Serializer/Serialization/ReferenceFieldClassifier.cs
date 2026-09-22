using System.Xml.Linq;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>What a bare number stored in an item field refers to.</summary>
public enum ReferenceKind
{
    /// <summary>A literal: never remapped.</summary>
    None,
    /// <summary>A page id, resolved through the source-to-target page map.</summary>
    Page,
    /// <summary>A paragraph id, resolved through the source-to-target paragraph map.</summary>
    Paragraph
}

/// <summary>
/// Engine issue #32: the reference-typed fields of one item type, split by how the field
/// declares it. <see cref="EditorFields"/> are link/page/paragraph/button editors (#15);
/// <see cref="OptionFields"/> are option lists whose SOURCE yields a page or paragraph id as
/// the stored value, whatever the editor (a <c>RadioButtonListEditor</c> over
/// <c>&lt;options sourceType="ItemType"&gt;</c> with <c>valueField="PageId"</c>).
/// </summary>
public sealed record ItemTypeReferenceFields(
    IReadOnlySet<string> EditorFields,
    IReadOnlyDictionary<string, ReferenceKind> OptionFields);

/// <summary>
/// Engine issue #15: answers "does this item field hold a reference to a page,
/// paragraph or item?" — the only fields on which a bare number may be remapped
/// source→target by <see cref="InternalLinkResolver"/>.
///
/// <para>Dependency-free and pure so the rule itself is unit-tested; the DW metadata half
/// lives in <see cref="ReferenceFieldLookup"/> (same split as
/// <see cref="ButtonDataNormalizer"/> / <see cref="ButtonDataFieldLookup"/>).</para>
///
/// <para>A field the item-type XML declares with any OTHER editor keeps its literal: the
/// defect this guards is <c>ImageAspectRatio</c> (a <c>TextEditor</c> / dropdown field
/// shipping the literal <c>"0"</c>) arriving on the host as the page id <c>8453</c>, because
/// the resolver's raw-numeric short-circuit treated any numeric-looking value as an id.
/// The <c>Default.aspx?ID=N</c> form is unambiguous and is still rewritten everywhere.</para>
///
/// <para>Engine issue #32: the editor is not the only declaration. An option list whose source
/// is an item-type or SQL query with <c>valueField="PageId"</c> / <c>"ParagraphId"</c> stores a
/// page or paragraph id (Swift 2's component selectors), so the field is a reference whatever
/// its editor. Static options, and option sources whose value field is anything else (an item
/// <c>Id</c>, an order context id, a CSS class name), stay literal.</para>
/// </summary>
public static class ReferenceFieldClassifier
{
    /// <summary>
    /// True for the DW editors whose stored value is a page/paragraph/item id:
    /// <c>LinkEditor</c> and any <c>*LinkEditor</c> (subclasses of <c>LinkEditorBase</c>,
    /// e.g. <c>ItemLinkEditor</c>), <c>ButtonEditor</c> (its JSON carries a SelectedValue id)
    /// and any custom <c>*PageEditor</c> / <c>*ParagraphEditor</c>.
    /// </summary>
    /// <param name="editorTypeName">
    /// The editor type name as the item-type XML declares it. An assembly-qualified name
    /// ("Type, Assembly") is accepted — only the type part is examined.
    /// </param>
    public static bool IsReferenceEditor(string? editorTypeName)
    {
        if (string.IsNullOrWhiteSpace(editorTypeName)) return false;

        var typeName = editorTypeName.Split(',')[0].Trim();
        var lastDot = typeName.LastIndexOf('.');
        var shortName = lastDot >= 0 ? typeName[(lastDot + 1)..] : typeName;

        return shortName.EndsWith("LinkEditor", StringComparison.OrdinalIgnoreCase)
            || shortName.EndsWith("PageEditor", StringComparison.OrdinalIgnoreCase)
            || shortName.EndsWith("ParagraphEditor", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("ButtonEditor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Engine issue #32: what the stored value of an option-list field refers to, from its
    /// <c>&lt;options sourceType="…"&gt;</c> and the source's <c>valueField</c>. Only the
    /// <c>ItemType</c> and <c>Sql</c> sources have a value field; <c>PageId</c> yields
    /// <see cref="ReferenceKind.Page"/>, <c>ParagraphId</c> yields
    /// <see cref="ReferenceKind.Paragraph"/> (case-insensitive), anything else is a literal.
    /// </summary>
    public static ReferenceKind ClassifyOptionSource(string? sourceType, string? valueField)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(valueField))
            return ReferenceKind.None;

        var source = sourceType.Trim();
        if (!source.Equals("ItemType", StringComparison.OrdinalIgnoreCase) &&
            !source.Equals("Sql", StringComparison.OrdinalIgnoreCase))
            return ReferenceKind.None;

        var value = valueField.Trim();
        if (value.Equals("PageId", StringComparison.OrdinalIgnoreCase)) return ReferenceKind.Page;
        if (value.Equals("ParagraphId", StringComparison.OrdinalIgnoreCase)) return ReferenceKind.Paragraph;
        return ReferenceKind.None;
    }

    /// <summary>
    /// Classifies every field of an item-type definition as DW writes it to
    /// <c>Files/System/Items/ItemType_*.xml</c>, by the same rule
    /// <see cref="ReferenceFieldLookup"/> applies to live metadata: an option source that
    /// yields a page or paragraph id wins over the editor.
    /// </summary>
    public static ItemTypeReferenceFields FromItemTypeXml(string xml)
    {
        var editorFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionFields = new Dictionary<string, ReferenceKind>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in XDocument.Parse(xml).Descendants("field"))
        {
            var systemName = (string?)field.Attribute("systemName");
            if (string.IsNullOrWhiteSpace(systemName)) continue;

            var options = field.Element("options");
            var kind = ClassifyOptionSource(
                (string?)options?.Attribute("sourceType"),
                (string?)options?.Elements().FirstOrDefault()?.Attribute("valueField"));
            if (kind != ReferenceKind.None)
                optionFields[systemName] = kind;
            else if (IsReferenceEditor((string?)field.Element("editor")?.Attribute("type")))
                editorFields.Add(systemName);
        }

        return new ItemTypeReferenceFields(editorFields, optionFields);
    }
}
