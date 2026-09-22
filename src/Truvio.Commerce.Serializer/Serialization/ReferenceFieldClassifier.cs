namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Engine issue #15: answers "does this item-field EDITOR hold a reference to a page,
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
}
