using System.Security.Cryptography;
using System.Text;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: the identity of an item list entry across hosts.
/// </summary>
/// <remarks>
/// <para>An entry is an item row, and an item row's only key is its <c>Id</c> in the item type's
/// table: there is no GUID. That id is unique per item type on one host, not across hosts. A
/// number is handed out by the host's own counter (<c>ItemTypeId</c>), so source entry 5 and an
/// unrelated host entry 5 are different things; a text id (<c>en-tier-1</c>) is chosen by whoever
/// built the content and means the same entry everywhere.</para>
/// <para>So an entry is identified by its id <b>inside the list of its owner</b>: the paragraph
/// or page (by GUID), the item field, and for a nested list the entry it hangs under. A delivery
/// only ever looks for a YAML entry among the members of that one list. When an entry has to be
/// created, a text id is kept as it is, and a number is never written as a number: the entry
/// gets the <see cref="ScopedId"/> of (owner, field, source id), which no host counter produces
/// and which is the same on every run and every host. The same id is the fallback for a text id
/// that an unrelated item on the host already uses.</para>
/// <para>Matching accepts the id itself or the scoped form in either direction, so a delivery
/// from A to B and one from B back to A both find the entry they wrote before.</para>
/// </remarks>
public static class ItemListIdentity
{
    /// <summary>Prefix of an id this engine derived (<see cref="ScopedId"/>).</summary>
    public const string ScopedPrefix = "sl-";

    private const int ScopedHexLength = 24;

    /// <summary>Owner key of a paragraph's item.</summary>
    public static string ParagraphOwner(Guid paragraphUniqueId) => $"paragraph:{paragraphUniqueId:D}";

    /// <summary>Owner key of a page's item.</summary>
    public static string PageOwner(Guid pageUniqueId) => $"page:{pageUniqueId:D}";

    /// <summary>
    /// Owner key of an entry, for the lists that entry carries itself.
    /// <paramref name="canonicalEntryId"/> is <see cref="CanonicalId"/> of the entry, so the key
    /// is the same on the host that numbered the entry and on the host that received it.
    /// </summary>
    public static string EntryOwner(string ownerKey, string field, string canonicalEntryId) =>
        $"{ownerKey}/{field.ToLowerInvariant()}/{canonicalEntryId.ToLowerInvariant()}";

    /// <summary>
    /// True when the id is one a host counter hands out (digits only). Such an id is never
    /// written to another host as it is.
    /// </summary>
    public static bool IsHostAllocated(string id) =>
        id.Length > 0 && id.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// The id an entry is created under when its own id cannot be used on the host: derived from
    /// the owner, the field and the source id, so it is stable and cannot collide with a host
    /// counter or with another owner's entries.
    /// </summary>
    public static string ScopedId(string ownerKey, string field, string sourceId)
    {
        var input = $"{ownerKey.ToLowerInvariant()}|{field.ToLowerInvariant()}|{sourceId.ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return ScopedPrefix + Convert.ToHexString(hash).ToLowerInvariant()[..ScopedHexLength];
    }

    /// <summary>Item ids compare without case, as the item tables do.</summary>
    public static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a member of the owner's list on the host is the YAML entry: the ids are equal,
    /// or one is the scoped form of the other.
    /// </summary>
    public static bool Matches(string yamlId, string hostId, string ownerKey, string field) =>
        SameId(yamlId, hostId)
        || SameId(ScopedId(ownerKey, field, yamlId), hostId)
        || SameId(yamlId, ScopedId(ownerKey, field, hostId));

    /// <summary>
    /// The id both sides of a matched pair agree on: the one the other was derived from, or the
    /// YAML id when neither was derived.
    /// </summary>
    public static string CanonicalId(string yamlId, string hostId, string ownerKey, string field) =>
        SameId(yamlId, ScopedId(ownerKey, field, hostId)) ? hostId : yamlId;

    /// <summary>The id a missing entry is created under, before the host is asked whether it is free.</summary>
    public static string PreferredTargetId(string yamlId, string ownerKey, string field) =>
        IsHostAllocated(yamlId) ? ScopedId(ownerKey, field, yamlId) : yamlId;
}
