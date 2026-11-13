namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: how an item list is delivered onto a host that may already have it.
/// A list has no lifecycle of its own; it takes the one of the document that owns it.
/// </summary>
public enum ItemListLifecycle
{
    /// <summary>
    /// The document was created, or exists and is overwritten (Replace, or
    /// <c>onExisting: overwrite</c>): the list on the host becomes the list in the document.
    /// </summary>
    Overwrite,

    /// <summary>
    /// The document exists and is filled (Merge, or <c>onExisting: fill</c>): entries the host
    /// has are kept and only their empty fields are filled, missing entries are added, nothing is
    /// removed or reordered.
    /// </summary>
    Fill
}

/// <summary>What a delivery does with one entry of the document's list.</summary>
public enum ItemListEntryAction
{
    /// <summary>The host's list has no such entry: create it with every field.</summary>
    Create,
    /// <summary>The host's list has it: write every field, source wins.</summary>
    Overwrite,
    /// <summary>The host's list has it: fill only the fields the host left empty.</summary>
    Fill
}

/// <summary>One entry of the document's list, and what happens to it.</summary>
/// <param name="YamlIndex">Position of the entry in the document's list.</param>
/// <param name="YamlId">The entry's id in the document.</param>
/// <param name="TargetId">The id of the member item on the host (existing, or to create).</param>
/// <param name="CanonicalId">The id both hosts agree on, used to key the entry's own lists.</param>
/// <param name="Action">Create, overwrite or fill.</param>
public sealed record ItemListEntryPlan(int YamlIndex, string YamlId, string TargetId, string CanonicalId, ItemListEntryAction Action);

/// <summary>The outcome of planning one list.</summary>
/// <param name="Entries">One plan per document entry, in document order.</param>
/// <param name="Remove">Host members to remove with their items (overwrite only).</param>
/// <param name="FinalOrder">The member ids of the list after the delivery, in order.</param>
/// <param name="ForeignIds">
/// Document ids that an item outside this list already uses on the host. The entry is created
/// under its scoped id instead; the caller reports each one.
/// </param>
public sealed record ItemListPlan(
    IReadOnlyList<ItemListEntryPlan> Entries,
    IReadOnlyList<string> Remove,
    IReadOnlyList<string> FinalOrder,
    IReadOnlyList<string> ForeignIds)
{
    /// <summary>True when the delivery leaves the list's membership and order as the host has them.</summary>
    public bool KeepsMembership(IReadOnlyList<string> hostMemberIds) =>
        Remove.Count == 0
        && FinalOrder.Count == hostMemberIds.Count
        && FinalOrder.Zip(hostMemberIds).All(pair => ItemListIdentity.SameId(pair.First, pair.Second));
}

/// <summary>
/// Content ownership R7: decides, for one item list, which document entries match which host
/// members and what the list looks like afterwards. Pure: the host is described by the member
/// ids of the owner's list and one question, "does an item with this id exist outside the list?".
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>A document entry is looked for only among the members of the owner's own list, by
/// <see cref="ItemListIdentity.Matches"/>. A member is matched once.</item>
/// <item><b>Overwrite</b>: a matched entry is overwritten, an unmatched one is created, a host
/// member no document entry matches is removed, and the list takes the document's order. The
/// list equals the source.</item>
/// <item><b>Fill</b>: a matched entry is filled, an unmatched one is created and appended after
/// the host's entries (in document order), a host-only member stays, and the host's order is
/// kept. Nothing is removed. Delivering the same document again matches every entry and adds
/// none.</item>
/// <item>A created entry keeps a text id and gets its scoped id for a number
/// (<see cref="ItemListIdentity.PreferredTargetId"/>). A text id that an item outside the list
/// already uses is not taken over: the entry is created under its scoped id and the id is
/// reported in <see cref="ItemListPlan.ForeignIds"/>.</item>
/// <item>An entry without an id, the same id twice, or two entries that match one host member
/// is an error: nothing is planned.</item>
/// </list>
/// </remarks>
public static class ItemListPlanner
{
    public static ItemListPlan Plan(
        ItemListLifecycle lifecycle,
        string ownerKey,
        string field,
        IReadOnlyList<string> yamlIds,
        IReadOnlyList<string> hostMemberIds,
        Func<string, bool> existsOutsideList)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < yamlIds.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(yamlIds[i]))
                throw new InvalidOperationException(
                    $"Item list '{field}': entry {i + 1} has no id; every entry needs one so it can be matched on the host.");
            if (!seen.Add(yamlIds[i]))
                throw new InvalidOperationException(
                    $"Item list '{field}': the id '{yamlIds[i]}' is used by more than one entry.");
        }

        var hostMembers = hostMemberIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ItemListEntryPlan>(yamlIds.Count);
        var created = new List<string>();
        var foreign = new List<string>();

        for (var i = 0; i < yamlIds.Count; i++)
        {
            var yamlId = yamlIds[i];
            var match = hostMembers.FirstOrDefault(hostId => ItemListIdentity.Matches(yamlId, hostId, ownerKey, field));
            if (match is not null)
            {
                if (!claimed.Add(match))
                    throw new InvalidOperationException(
                        $"Item list '{field}': the entries '{yamlId}' and " +
                        $"'{entries.First(e => ItemListIdentity.SameId(e.TargetId, match)).YamlId}' both match the host entry '{match}'.");
                targetIds.Add(match);
                entries.Add(new ItemListEntryPlan(i, yamlId, match,
                    ItemListIdentity.CanonicalId(yamlId, match, ownerKey, field),
                    lifecycle == ItemListLifecycle.Fill ? ItemListEntryAction.Fill : ItemListEntryAction.Overwrite));
                continue;
            }

            var targetId = ItemListIdentity.PreferredTargetId(yamlId, ownerKey, field);
            if (ItemListIdentity.SameId(targetId, yamlId) && existsOutsideList(targetId))
            {
                foreign.Add(yamlId);
                targetId = ItemListIdentity.ScopedId(ownerKey, field, yamlId);
            }
            if (!targetIds.Add(targetId))
                throw new InvalidOperationException(
                    $"Item list '{field}': the entry '{yamlId}' would be created under '{targetId}', which another entry of the list already takes.");

            created.Add(targetId);
            entries.Add(new ItemListEntryPlan(i, yamlId, targetId, yamlId, ItemListEntryAction.Create));
        }

        if (lifecycle == ItemListLifecycle.Fill)
        {
            return new ItemListPlan(entries, Array.Empty<string>(),
                hostMembers.Concat(created).ToList(), foreign);
        }

        return new ItemListPlan(
            entries,
            hostMembers.Where(hostId => !claimed.Contains(hostId)).ToList(),
            entries.Select(e => e.TargetId).ToList(),
            foreign);
    }
}
