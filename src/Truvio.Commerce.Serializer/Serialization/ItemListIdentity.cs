using System.Security.Cryptography;
using System.Text;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: the identity of an item list entry across hosts.
/// </summary>
/// <remarks>
/// <para>An entry is an item row, and an item row's only key is its <c>Id</c> in the item type's
/// table: there is no GUID and no column the platform leaves alone. A number is handed out by the
/// host's own counter (<c>ItemTypeId</c>), so source entry 5 and an unrelated host entry 5 are
/// different things; a text id (<c>en-tier-1</c>) is chosen by whoever built the content.</para>
/// <para>An entry is therefore identified <b>inside the list of its owner</b>: the paragraph or
/// page (by GUID), the item field, and for a nested list the entry it hangs under, plus the id the
/// document gives it (the <i>source id</i>). That triple is the entry's <see cref="EntryKey"/>.</para>
/// <para>A delivered entry that the host does not have yet is always created under a <b>new
/// number from the target host's own counter</b>, never under the source's number and never under
/// a text id: Dynamicweb 10.29.8 cannot create a language version of a paragraph whose list
/// entries have text ids. The key is recorded beside it in the Serializer's own table
/// (<c>TruvioSerializerItemListEntry</c>, see <see cref="DwItemListHost"/>), so the next delivery
/// finds it again, and so a serialize on that host writes the source id back into the document
/// (the id every host agrees on).</para>
/// <para>An entry already on the host under the document's id (delivered as table rows before
/// R7, or the host the document came from) is matched by that id and keeps it, number or text.</para>
/// </remarks>
public static class ItemListIdentity
{
    /// <summary>Prefix of an entry key (<see cref="EntryKey"/>).</summary>
    public const string KeyPrefix = "sl-";

    private const int KeyHexLength = 24;

    /// <summary>Owner key of a paragraph's item.</summary>
    public static string ParagraphOwner(Guid paragraphUniqueId) => $"paragraph:{paragraphUniqueId:D}";

    /// <summary>Owner key of a page's item.</summary>
    public static string PageOwner(Guid pageUniqueId) => $"page:{pageUniqueId:D}";

    /// <summary>
    /// Owner key of an entry, for the lists that entry carries itself. <paramref name="entryId"/>
    /// is the entry's id in the document, which is the same on every host.
    /// </summary>
    public static string EntryOwner(string ownerKey, string field, string entryId) =>
        $"{ownerKey}/{field.ToLowerInvariant()}/{entryId.ToLowerInvariant()}";

    /// <summary>
    /// The stable key of a document entry: derived from the owner, the field and the entry's id in
    /// the document. The same on every run and every host; never an item id.
    /// </summary>
    public static string EntryKey(string ownerKey, string field, string sourceId)
    {
        var input = $"{ownerKey.ToLowerInvariant()}|{field.ToLowerInvariant()}|{sourceId.ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return KeyPrefix + Convert.ToHexString(hash).ToLowerInvariant()[..KeyHexLength];
    }

    /// <summary>
    /// True when the id is one a host counter hands out (digits only). Every entry this engine
    /// creates gets one.
    /// </summary>
    public static bool IsHostAllocated(string id) =>
        id.Length > 0 && id.All(c => c is >= '0' and <= '9');

    /// <summary>Item ids compare without case, as the item tables do.</summary>
    public static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One row of the entry map: a list entry this engine created on the host, under the host's own
/// number, for a document entry.
/// </summary>
/// <param name="EntryKey"><see cref="ItemListIdentity.EntryKey"/> of the document entry.</param>
/// <param name="ItemType">The entry's item type.</param>
/// <param name="ItemId">The entry's id on this host (a number from the host's counter).</param>
/// <param name="OwnerKey">The owner key the entry key was derived from.</param>
/// <param name="Field">The list field.</param>
/// <param name="SourceId">The entry's id in the document.</param>
public sealed record ItemListEntryMapping(string EntryKey, string ItemType, string ItemId, string OwnerKey, string Field, string SourceId);
