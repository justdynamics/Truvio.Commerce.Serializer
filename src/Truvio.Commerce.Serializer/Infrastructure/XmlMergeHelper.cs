using System.Xml;
using System.Xml.Linq;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Per-element XML merge for Merge-mode deserialization. Fills target XML elements that are
/// absent OR have null/empty/whitespace text, using source XML as the fill value pool.
/// Preserves any element present on target but absent from source (Merge never strips).
/// </summary>
/// <remarks>
/// <para>
/// Element identity is by local-name, except when a child has a <c>name</c> attribute
/// (DW <c>&lt;Parameter name="X"&gt;</c> idiom used by
/// <c>EcomPayments.PaymentGatewayParameters</c> and <c>EcomShippings.ShippingServiceParameters</c>),
/// in which case the <c>name</c> attribute is the identity key. This matches the scheme already
/// used by <see cref="XmlFormatter.CompactWithMerge"/>.
/// </para>
/// <para>
/// Security (T-39-02-05): uses an <see cref="XmlReader"/> with <see cref="DtdProcessing.Prohibit"/>
/// to reject entity-expansion (billion-laughs) payloads. Malformed XML on either side returns
/// the target unchanged.
/// </para>
/// <para>
/// Security (T-39-02-03): merged XML is re-serialized via <see cref="XDocument.ToString()"/>,
/// so any injected SQL-like text inside XML content is escaped as XML text, not executed as SQL.
/// The merged XML value is bound into the enclosing SQL via <c>CommandBuilder {0}</c> placeholders
/// (see <see cref="Providers.SqlTable.SqlTableWriter.UpdateColumnSubset"/>) — no SQL escape path.
/// </para>
/// <para>
/// Dry-run (T-39-02-06): the <see cref="MergeWithDiagnostics"/> variant returns per-element
/// fill descriptions that include source values — do not emit those logs to untrusted sinks.
/// </para>
/// </remarks>
public static class XmlMergeHelper
{
    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreWhitespace = false
    };

    /// <summary>Merge source XML into target XML per D-22/D-23/D-24. See type remarks.</summary>
    public static string? Merge(string? targetXml, string? sourceXml)
        => MergeWithDiagnostics(targetXml, sourceXml).mergedXml;

    /// <summary>
    /// Merge with per-element fill diagnostics for D-19 / D-26 dry-run logging.
    /// Returned <c>fills</c> strings describe each element-level change:
    /// <c>"element=Mail1SenderEmail: &lt;missing&gt; -> 'no-reply@x.com'"</c>.
    /// </summary>
    public static (string? mergedXml, IReadOnlyList<string> fills) MergeWithDiagnostics(
        string? targetXml, string? sourceXml)
    {
        var (mergedXml, fills, _) = MergeWithFilledPaths(targetXml, sourceXml);
        return (mergedXml, fills);
    }

    /// <summary>
    /// <see cref="MergeWithDiagnostics"/> plus WHERE each fill landed: one <see cref="XmlFillPath"/>
    /// per element or attribute the merge took from the source. Everything else in the merged XML
    /// is the target's own. Content ownership R2 uses this to resolve links in the filled parts
    /// only (<see cref="TransformFilled"/>).
    /// </summary>
    public static (string? mergedXml, IReadOnlyList<string> fills, IReadOnlyList<XmlFillPath> filledPaths)
        MergeWithFilledPaths(string? targetXml, string? sourceXml)
    {
        var fills = new List<string>();
        var filledPaths = new List<XmlFillPath>();

        // Pure-null boundary cases — mirror XmlFormatter.CompactWithMerge semantics.
        if (targetXml is null && sourceXml is null) return (null, fills, filledPaths);
        if (string.IsNullOrWhiteSpace(targetXml)) return (sourceXml, fills, filledPaths);
        if (string.IsNullOrWhiteSpace(sourceXml)) return (targetXml, fills, filledPaths);

        var targetDoc = TryParse(targetXml);
        var sourceDoc = TryParse(sourceXml);

        // Defensive parse: if either side fails to parse (malformed or prohibited DTD),
        // return target unchanged — no partial merges (T-39-02-05).
        if (targetDoc?.Root is null || sourceDoc?.Root is null)
            return (targetXml, fills, filledPaths);

        MergeElement(targetDoc.Root, sourceDoc.Root, fills, new List<string>(), filledPaths);

        return (Serialize(targetDoc, targetXml), fills, filledPaths);
    }

    /// <summary>
    /// Applies <paramref name="transform"/> to the text and attribute values inside the parts of
    /// <paramref name="xml"/> that <paramref name="filledPaths"/> name (as returned by
    /// <see cref="MergeWithFilledPaths"/> for this document), and to nothing else. A filled
    /// element is transformed with everything below it; a filled attribute alone. The
    /// <c>name</c> identity attribute is never transformed. Returns <paramref name="xml"/>
    /// unchanged (the same string) when nothing changes, when a path no longer exists or when
    /// the XML does not parse.
    /// </summary>
    public static string? TransformFilled(string? xml, IReadOnlyCollection<XmlFillPath> filledPaths,
        Func<string, string?> transform)
    {
        if (string.IsNullOrWhiteSpace(xml) || filledPaths.Count == 0)
            return xml;

        var doc = TryParse(xml);
        if (doc?.Root is null)
            return xml;

        var changed = false;
        string Apply(string value)
        {
            if (value.Length == 0) return value;
            var result = transform(value) ?? value;
            if (!string.Equals(result, value, StringComparison.Ordinal)) changed = true;
            return result;
        }

        var done = new HashSet<XObject>();
        foreach (var path in filledPaths)
        {
            var element = Find(doc.Root, path.Elements);
            if (element is null)
                continue;

            if (path.Attribute is not null)
            {
                var attribute = element.Attributes().FirstOrDefault(a =>
                    !a.IsNamespaceDeclaration && a.Name.LocalName == path.Attribute);
                if (attribute is not null && done.Add(attribute))
                    attribute.Value = Apply(attribute.Value);
                continue;
            }

            foreach (var node in element.DescendantNodesAndSelf().ToList())
            {
                if (node is XText text)
                {
                    if (done.Add(text))
                        text.Value = Apply(text.Value);
                }
                else if (node is XElement el)
                {
                    foreach (var attribute in el.Attributes())
                        if (!attribute.IsNamespaceDeclaration && !IsIdentityAttribute(attribute) && done.Add(attribute))
                            attribute.Value = Apply(attribute.Value);
                }
            }
        }

        return changed ? Serialize(doc, xml) : xml;
    }

    /// <summary>The element a fill path names, walked the way the merge matches children: by key, first match.</summary>
    private static XElement? Find(XElement root, IReadOnlyList<string> keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            current = current.Elements().FirstOrDefault(e =>
                string.Equals(GetKey(e), key, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }
        return current;
    }

    private static bool IsIdentityAttribute(XAttribute attribute) =>
        attribute.Name.LocalName == "name" && attribute.Name.NamespaceName == "";

    /// <summary>Unformatted XML, with the declaration when the original text had one.</summary>
    private static string Serialize(XDocument doc, string originalXml)
    {
        var hadDeclaration = originalXml.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        return hadDeclaration && doc.Declaration is not null
            ? doc.Declaration + doc.ToString(SaveOptions.DisableFormatting)
            : doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XDocument? TryParse(string xml)
    {
        try
        {
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, SafeSettings);
            return XDocument.Load(xr);
        }
        catch (XmlException) { return null; }
    }

    private static string GetKey(XElement el)
        => el.Attribute("name")?.Value ?? el.Name.LocalName;

    private static bool IsUnsetText(string? text)
        => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// "Unset" leaf per D-22: no child elements AND text is null/empty/whitespace.
    /// Elements with children are never considered unset (the merge walks into them).
    /// </summary>
    private static bool IsUnsetLeafElement(XElement el)
        => !el.HasElements && IsUnsetText(el.Value);

    /// <param name="path">Keys from the root's children down to <paramref name="target"/> (empty for the root).</param>
    private static void MergeElement(XElement target, XElement source, List<string> fills,
        List<string> path, List<XmlFillPath> filledPaths)
    {
        // Attribute-level merge at this level.
        MergeAttributes(target, source, fills, path, filledPaths);

        // Leaf-leaf case: both target and source are leaves — fill target if unset.
        if (!target.HasElements && !source.HasElements)
        {
            if (IsUnsetText(target.Value) && !IsUnsetText(source.Value))
            {
                fills.Add($"element={GetKey(target)}: <empty> -> '{source.Value}'");
                filledPaths.Add(new XmlFillPath(path.ToArray(), null));
                target.Value = source.Value;
            }
            return;
        }

        // Index target children by identity key (case-insensitive). Duplicate keys keep the first —
        // DW <Parameter name="X"> documents never duplicate a name within a single parent.
        var targetByKey = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var tgtChild in target.Elements())
        {
            var k = GetKey(tgtChild);
            if (!targetByKey.ContainsKey(k))
                targetByKey[k] = tgtChild;
        }

        foreach (var srcChild in source.Elements())
        {
            var key = GetKey(srcChild);
            if (targetByKey.TryGetValue(key, out var tgtChild))
            {
                if (IsUnsetLeafElement(tgtChild))
                {
                    // Target leaf is unset (absent/empty/whitespace) — replace with source content.
                    fills.Add($"element={key}: <missing-or-empty> -> '{srcChild.Value}'");
                    filledPaths.Add(new XmlFillPath(path.Append(key).ToArray(), null));
                    tgtChild.ReplaceWith(new XElement(srcChild));
                }
                else
                {
                    // Recurse for non-leaf or set-leaf children.
                    path.Add(key);
                    MergeElement(tgtChild, srcChild, fills, path, filledPaths);
                    path.RemoveAt(path.Count - 1);
                }
            }
            else
            {
                // Element absent on target — add it wholesale (D-22 missing fill).
                fills.Add($"element={key}: <missing> -> '{srcChild.Value}'");
                filledPaths.Add(new XmlFillPath(path.Append(key).ToArray(), null));
                target.Add(new XElement(srcChild));
            }
        }
        // D-24: target-only elements preserved by doing nothing for them.
    }

    private static void MergeAttributes(XElement target, XElement source, List<string> fills,
        List<string> path, List<XmlFillPath> filledPaths)
    {
        foreach (var srcAttr in source.Attributes())
        {
            // Ignore namespace-declaration attributes — XDocument handles those automatically,
            // and we don't want xmlns:* entries showing up in diagnostics.
            if (srcAttr.IsNamespaceDeclaration) continue;

            // Skip the "name" attribute — it's an identity key, not a payload to merge.
            if (srcAttr.Name.LocalName == "name" && srcAttr.Name.NamespaceName == "") continue;

            var tgtAttr = target.Attribute(srcAttr.Name);
            if (tgtAttr is null)
            {
                fills.Add($"attribute={srcAttr.Name.LocalName}: <missing> -> '{srcAttr.Value}'");
                filledPaths.Add(new XmlFillPath(path.ToArray(), srcAttr.Name.LocalName));
                target.SetAttributeValue(srcAttr.Name, srcAttr.Value);
            }
            else if (IsUnsetText(tgtAttr.Value) && !IsUnsetText(srcAttr.Value))
            {
                fills.Add($"attribute={srcAttr.Name.LocalName}: <empty> -> '{srcAttr.Value}'");
                filledPaths.Add(new XmlFillPath(path.ToArray(), srcAttr.Name.LocalName));
                tgtAttr.Value = srcAttr.Value;
            }
        }
    }
}

/// <summary>
/// Where one <see cref="XmlMergeHelper"/> fill landed: the identity keys from the root's children
/// down to the element (empty for the root itself), and the attribute's local name when the fill
/// was an attribute of that element (null: the element and everything below it came from source).
/// A key is the child's <c>name</c> attribute when it has one, else its local name, the identity
/// the merge itself matches children by.
/// </summary>
public sealed record XmlFillPath(IReadOnlyList<string> Elements, string? Attribute);
