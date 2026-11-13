namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: how an item list is delivered onto a host that may already have it.
/// A list has no lifecycle of its own; it takes the one of the document that owns it.
/// </summary>
public enum ItemListLifecycle
{
    /// <summary>
    /// The document was created, or exists and is overwritten (Replace, or
    /// <c>onExisting: overwrite</c>): the entries the document delivers are written and take the
    /// document's order; an entry only the host has is left in place after them, never deleted.
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

/// <summary>How a document entry was found on the host.</summary>
public enum ItemListMatch
{
    /// <summary>Not found: the entry is created under a new number from the host's counter.</summary>
    None,
    /// <summary>Through the entry map: the host entry this engine created for the document entry before.</summary>
    Key,
    /// <summary>By id: a member of the list has the document's id (delivered as rows before R7, or the host the document came from).</summary>
    Id
}

/// <summary>One entry of the document's list, and what happens to it.</summary>
/// <param name="YamlIndex">Position of the entry in the document's list.</param>
/// <param name="YamlId">The entry's id in the document (its source id).</param>
/// <param name="HostId">The id of the member item on the host; null for an entry to create (the host numbers it).</param>
/// <param name="Action">Create, overwrite or fill.</param>
/// <param name="MatchedBy">How the host entry was found.</param>
public sealed record ItemListEntryPlan(int YamlIndex, string YamlId, string? HostId, ItemListEntryAction Action, ItemListMatch MatchedBy);

/// <summary>The outcome of planning one list.</summary>
/// <param name="Lifecycle">The lifecycle the plan was made for.</param>
/// <param name="Entries">One plan per document entry, in document order.</param>
/// <param name="HostMembers">The list's members on the host before the delivery, in host order.</param>
/// <param name="HostOnly">
/// Host members no document entry matches, in host order. They stay in the list with their
/// items: a delivery never deletes a list entry. The caller reports each one as information.
/// </param>
public sealed record ItemListPlan(
    ItemListLifecycle Lifecycle,
    IReadOnlyList<ItemListEntryPlan> Entries,
    IReadOnlyList<string> HostMembers,
    IReadOnlyList<string> HostOnly)
{
    /// <summary>The number of entries the host does not have yet.</summary>
    public int Creates => Entries.Count(e => e.Action == ItemListEntryAction.Create);

    /// <summary>
    /// The member ids of the list after the delivery, in order. <paramref name="createdIds"/>
    /// gives the host id of each created entry by its <see cref="ItemListEntryPlan.YamlIndex"/>.
    /// Overwrite: the delivered entries in document order, then the host-only ones in host order.
    /// Fill: the host's members in host order, then the created entries in document order.
    /// </summary>
    public IReadOnlyList<string> FinalOrder(IReadOnlyDictionary<int, string>? createdIds = null)
    {
        string IdOf(ItemListEntryPlan e) =>
            e.HostId ?? (createdIds is not null && createdIds.TryGetValue(e.YamlIndex, out var id)
                ? id
                : throw new InvalidOperationException($"Item list entry '{e.YamlId}' has no host id yet."));

        if (Lifecycle == ItemListLifecycle.Fill)
            return HostMembers.Concat(Entries.Where(e => e.Action == ItemListEntryAction.Create).Select(IdOf)).ToList();
        return Entries.Select(IdOf).Concat(HostOnly).ToList();
    }

    /// <summary>True when the delivery leaves the list's membership and order as the host has them.</summary>
    public bool KeepsMembership()
    {
        if (Creates > 0) return false;
        var order = FinalOrder();
        return order.Count == HostMembers.Count
               && order.Zip(HostMembers).All(pair => ItemListIdentity.SameId(pair.First, pair.Second));
    }

    /// <summary>
    /// True when the members the host already has end up in another relative order (an order
    /// change). Entries the delivery adds do not count; they are creates.
    /// </summary>
    public bool ReordersHostMembers()
    {
        if (Lifecycle == ItemListLifecycle.Fill) return false;
        var kept = Entries.Where(e => e.HostId is not null).Select(e => e.HostId!).Concat(HostOnly).ToList();
        return kept.Count == HostMembers.Count
               && !kept.Zip(HostMembers).All(pair => ItemListIdentity.SameId(pair.First, pair.Second));
    }
}

/// <summary>
/// Content ownership R7: decides, for one item list, which document entries match which host
/// members and what the list looks like afterwards. Pure: the host is described by the member
/// ids of the owner's list and the entry map's answer for the document's entries.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>A document entry is looked for only among the members of the owner's own list: first
/// through the entry map (the host entry this engine created for the same owner, field and
/// source id), then by its own id (an entry delivered as rows before R7, or the host the
/// document came from). A member is matched once; a member the map claims is not matched by
/// id. A map row whose item is no longer in the list matches nothing.</item>
/// <item><b>Overwrite</b>: a matched entry is overwritten, an unmatched one is created, and the
/// delivered entries take the document's order. A host member no document entry matches is
/// <b>not</b> removed: it stays in the list with its item, after the delivered entries, in the
/// relative order the host gave it (<see cref="ItemListPlan.HostOnly"/>).</item>
/// <item><b>Fill</b>: a matched entry is filled, an unmatched one is created and appended after
/// the host's entries (in document order), a host-only member stays, and the host's order is
/// kept. Nothing is removed. Delivering the same document again matches every entry and adds
/// none.</item>
/// <item>An entry to create has no id yet: the host numbers it from its own counter, whatever
/// the document's id is (number or text).</item>
/// <item>An entry without an id or the same id twice is an error: nothing is planned.</item>
/// </list>
/// </remarks>
public static class ItemListPlanner
{
    /// <param name="lifecycle">Overwrite or fill.</param>
    /// <param name="field">The list field (for error messages).</param>
    /// <param name="yamlIds">The document's entry ids, in document order.</param>
    /// <param name="hostMemberIds">The list's members on the host, in list order.</param>
    /// <param name="mappedHostId">The entry map's host id for a document id; null when it has none.</param>
    public static ItemListPlan Plan(
        ItemListLifecycle lifecycle,
        string field,
        IReadOnlyList<string> yamlIds,
        IReadOnlyList<string> hostMemberIds,
        Func<string, string?>? mappedHostId = null)
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
        var hostIds = new string?[yamlIds.Count];
        var matchedBy = new ItemListMatch[yamlIds.Count];

        // Pass 1: the entry map. Its claim wins over an id that happens to be equal.
        for (var i = 0; i < yamlIds.Count; i++)
        {
            var mapped = mappedHostId?.Invoke(yamlIds[i]);
            if (string.IsNullOrEmpty(mapped)) continue;
            var member = hostMembers.FirstOrDefault(h => ItemListIdentity.SameId(h, mapped));
            if (member is null || !claimed.Add(member)) continue;
            hostIds[i] = member;
            matchedBy[i] = ItemListMatch.Key;
        }

        // Pass 2: the document's own id among the members nobody claimed.
        for (var i = 0; i < yamlIds.Count; i++)
        {
            if (hostIds[i] is not null) continue;
            var member = hostMembers.FirstOrDefault(h => ItemListIdentity.SameId(h, yamlIds[i]));
            if (member is null || !claimed.Add(member)) continue;
            hostIds[i] = member;
            matchedBy[i] = ItemListMatch.Id;
        }

        var entries = new List<ItemListEntryPlan>(yamlIds.Count);
        for (var i = 0; i < yamlIds.Count; i++)
        {
            var action = hostIds[i] is null
                ? ItemListEntryAction.Create
                : lifecycle == ItemListLifecycle.Fill ? ItemListEntryAction.Fill : ItemListEntryAction.Overwrite;
            entries.Add(new ItemListEntryPlan(i, yamlIds[i], hostIds[i], action, matchedBy[i]));
        }

        var hostOnly = hostMembers.Where(hostId => !claimed.Contains(hostId)).ToList();
        return new ItemListPlan(lifecycle, entries, hostMembers, hostOnly);
    }
}
