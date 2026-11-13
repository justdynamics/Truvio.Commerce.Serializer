using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// The dry run of a Replace on an existing page or paragraph: what the real write would change,
/// compared in the form the write and the document hold values, so an identical target reports
/// nothing and every real difference is a line. Pure; the deserializer supplies the host side.
/// </summary>
/// <remarks>
/// What the comparison accounts for (each measured on the CABP clean room, owner decision D of
/// 2026-11-13):
/// <list type="bullet">
/// <item><c>GlobalRecordPageGuid</c> and <c>MasterParagraphGuid</c> are not item fields: the
/// document carries the GUID of the page or paragraph, the host holds its id in
/// <c>Paragraph.GlobalRecordPageID</c> / <c>MasterParagraphID</c>. They are compared as those
/// links (<see cref="GuidLink"/>), never against the item.</item>
/// <item>A paragraph's <c>Text</c> in the document is its item's <c>Text</c> field when the
/// item has one (a module paragraph's own text otherwise). The write hands the document's
/// fields to the item API, which ignores a field the item type does not declare, so only the
/// item's fields are compared, and the item's own value is the host side.</item>
/// <item>System fields (<c>Id</c>, <c>Sort</c>, <c>ItemInstanceType</c>) are not written.</item>
/// <item>Values compare as a document carries them (<see cref="ItemListFieldDiff.Canonical"/>):
/// <c>True</c>/<c>true</c>, a number and its text, CRLF and LF, a blank button and nothing.</item>
/// <item>A null and an empty value are the same; a header or menu text that is null in the
/// document and empty on the host is no change.</item>
/// <item>Links in the document's fields hold source ids; they are compared after the same
/// resolution the real run's link pass applies (the caller passes the resolved fields).</item>
/// <item>A field the host item has and the document does not carry is nulled by the write and
/// is reported, unless it is excluded or owned by the document's item lists.</item>
/// <item>Permissions are a change only when the set differs from the host's explicit ones.</item>
/// </list>
/// </remarks>
public static class DryRunDiff
{
    /// <summary>Two scalar texts that the write treats as the same (null and empty are equal).</summary>
    public static bool SameText(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The diff lines of a source-wins item field write: document fields the item has whose value
    /// differs, and item fields the document does not carry that hold a value. One line per field:
    /// <c>Fields[Name]: 'host' -> 'document'</c>.
    /// </summary>
    /// <param name="documentFields">The document's fields as the write receives them, links resolved.</param>
    /// <param name="hostFields">The item's fields on the host; null when the owner has no item.</param>
    /// <param name="notWritten">Fields the write does not touch: system fields, exclusions, list fields.</param>
    public static IReadOnlyList<string> ItemFields(
        IReadOnlyDictionary<string, object> documentFields,
        IReadOnlyDictionary<string, object?>? hostFields,
        Func<string, bool> notWritten)
    {
        if (hostFields is null)
            return Array.Empty<string>();
        return ItemListFieldDiff.Overwrite(documentFields, hostFields, notWritten, onlyFieldsTheHostHas: true)
            .Select(line => $"Fields[{line[..line.IndexOf(':')]}]{line[line.IndexOf(':')..]}")
            .ToList();
    }

    /// <summary>
    /// A link the document carries as a GUID (<c>GlobalRecordPageGuid</c>,
    /// <c>MasterParagraphGuid</c>) against the id the host holds. Null when the write leaves it
    /// as it is: the document has no such link (the write never clears one), or the GUID names the
    /// host's current target.
    /// </summary>
    /// <param name="field">The document field, for the line.</param>
    /// <param name="documentGuid">The GUID the document carries; null or empty when it carries none.</param>
    /// <param name="hostId">The id the host holds (0 for none).</param>
    /// <param name="targetIdOf">The host id of a GUID; null when the host has no such page or paragraph.</param>
    public static string? GuidLink(string field, object? documentGuid, int hostId, Func<Guid, int?> targetIdOf)
    {
        var text = documentGuid?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!Guid.TryParse(text, out var guid))
            return $"Fields[{field}]: '{hostId}' -> '{text}' (not a GUID)";
        var target = targetIdOf(guid);
        if (target is null)
            return $"Fields[{field}]: '{hostId}' -> '{guid}' (not on the target yet)";
        return target.Value == hostId ? null : $"Fields[{field}]: '{hostId}' -> '{target.Value}' ({guid})";
    }

    /// <summary>
    /// True when the document's permissions equal the host's explicit ones (owner, type, sub name
    /// and level; order and case of names do not matter). The write replaces the host's set with
    /// the document's, so equal sets are no change.
    /// </summary>
    public static bool SamePermissions(IReadOnlyCollection<SerializedPermission> document, IReadOnlyCollection<SerializedPermission> host)
    {
        static string Key(SerializedPermission p) =>
            $"{p.OwnerType?.ToLowerInvariant()}|{p.Owner?.ToLowerInvariant()}|{(p.SubName ?? string.Empty).ToLowerInvariant()}|{p.Level?.ToLowerInvariant()}";
        var a = document.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var b = host.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    /// <summary>
    /// The 1-based position of a grid row among its page's rows in (Sort, ID) order: the number
    /// the serialize side gives it. 0 when the row is not among them.
    /// </summary>
    public static int GridRowPosition(IEnumerable<(int Id, int Sort)> pageRows, int rowId)
    {
        var ordered = pageRows.OrderBy(r => r.Sort).ThenBy(r => r.Id).Select(r => r.Id).ToList();
        return ordered.IndexOf(rowId) + 1;
    }
}
