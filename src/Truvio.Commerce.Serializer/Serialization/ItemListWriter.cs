using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: what the item list writer needs from the host. The production
/// implementation is <see cref="DwItemListHost"/>; tests run the writer against an in-memory one.
/// </summary>
public interface IItemListHost
{
    /// <summary>The item list declaration of a field of an item type; null when the field is not an item list (or the type is unknown).</summary>
    ItemListFieldInfo? GetListField(string ownerItemType, string field);

    /// <summary>True when the item type is registered on the host.</summary>
    bool ItemTypeExists(string itemType);

    /// <summary>The list id the owner item's field holds; 0 when it holds none.</summary>
    int GetListId(string ownerItemType, string ownerItemId, string field);

    /// <summary>The item type of an existing list; null when no list has this id.</summary>
    string? GetListItemType(int listId);

    /// <summary>The member item ids of a list, in list order.</summary>
    IReadOnlyList<string> GetMemberIds(int listId);

    /// <summary>Creates an empty list of the item type and returns its id.</summary>
    int CreateList(string memberItemType);

    /// <summary>Points the owner item's field at a list.</summary>
    void SetListId(string ownerItemType, string ownerItemId, string field, int listId);

    /// <summary>True when an item with this id exists in the item type's table.</summary>
    bool ItemExists(string itemType, string itemId);

    /// <summary>
    /// Creates an empty item under a new id from the host's own counter for the item type
    /// (<c>ItemTypeId</c>), so the counter moves past it and the host never hands that number out
    /// again, and returns the id. Never an id the item type's table already has.
    /// </summary>
    string CreateItem(string itemType);

    /// <summary>
    /// Puts back the item row of a list member whose relation outlived its item, under the id
    /// the relation names.
    /// </summary>
    void RestoreItem(string itemType, string itemId);

    /// <summary>
    /// The entry map: for each entry key (<see cref="ItemListIdentity.EntryKey"/>) the map holds
    /// for this item type, the id of the entry this engine created for it on this host.
    /// Keys the map does not hold are absent. Reads only; an empty map when the host has none yet.
    /// </summary>
    IReadOnlyDictionary<string, string> FindMappedEntries(string itemType, IReadOnlyCollection<string> entryKeys);

    /// <summary>Records (or moves) an entry key to the entry created for it on this host.</summary>
    void RecordEntry(ItemListEntryMapping mapping);

    /// <summary>
    /// The item's <c>ItemInstanceType</c> as stored; null when the item does not exist.
    /// </summary>
    string? GetInstanceType(string itemType, string itemId);

    /// <summary>
    /// Puts an item's stored <c>ItemInstanceType</c> back to <paramref name="instanceType"/> when an
    /// item save emptied it. Dynamicweb writes an empty value for an item type without a base on
    /// every save; an entry delivered as a table row keeps the value it arrived with.
    /// </summary>
    void KeepInstanceType(string itemType, string itemId, string instanceType);

    /// <summary>
    /// The fields of an item as the item API holds them (system fields included), or null when
    /// the item does not exist. Read by the dry run to tell what an overwrite or fill would change.
    /// </summary>
    IReadOnlyDictionary<string, object?>? GetItemFields(string itemType, string itemId);

    /// <summary>
    /// Makes the list's members exactly these ids, in this order, and gives each member item its
    /// position. The writer always passes every member the host had (a delivery never deletes a
    /// list entry), so this only adds and reorders relations.
    /// </summary>
    void SaveMembers(int listId, IReadOnlyList<string> orderedItemIds);

    /// <summary>
    /// Gives each member item of the list its position in the list again (the item's <c>Sort</c>),
    /// without touching the relations. An item save resets it.
    /// </summary>
    void SyncMemberSort(int listId, string memberItemType);

    /// <summary><c>ItemList.IsChangedOnLanguage</c> of a list.</summary>
    bool GetChangedOnLanguage(int listId);

    /// <summary>Sets <c>ItemList.IsChangedOnLanguage</c> of a list.</summary>
    void SetChangedOnLanguage(int listId, bool changed);
}

/// <summary>The item that owns item lists: a paragraph's or page's item, or a list entry for a nested list.</summary>
/// <param name="ItemType">System name of the owner's item type.</param>
/// <param name="ItemId">The owner item's id on the host.</param>
/// <param name="Key">The owner's cross-host key (<see cref="ItemListIdentity"/>).</param>
/// <param name="Label">How log lines and errors name the owner.</param>
public sealed record ItemListOwner(string ItemType, string ItemId, string Key, string Label);

/// <summary>A list entry's item on the host that a delivery created, overwrote or filled.</summary>
public sealed record ItemListMemberRef(string ItemType, string ItemId);

/// <summary>
/// What one delivery did (or, in a dry run, would do) to the lists of one owner, nested lists
/// included. Summed per run into <see cref="ItemListCounts"/>.
/// </summary>
public sealed class ItemListWriteResult
{
    /// <summary>Entries created (dry run: to create).</summary>
    public int Created { get; set; }

    /// <summary>Matched entries written source-wins. In a dry run: matched entries whose fields would change.</summary>
    public int Overwritten { get; set; }

    /// <summary>Matched entries a Fill visited.</summary>
    public int Filled { get; set; }

    /// <summary>Fields a Fill wrote (dry run: would write) into entries the host already had.</summary>
    public int FilledFields { get; set; }

    /// <summary>
    /// Entries on the host that the document does not have. They are left in place, never
    /// deleted, and reported as information: they are not a change and not a warning.
    /// </summary>
    public int HostOnly { get; set; }

    /// <summary>Lists whose existing members end up in another relative order.</summary>
    public int Reordered { get; set; }

    /// <summary>Lists created (dry run: to create).</summary>
    public int ListsCreated { get; set; }

    /// <summary>Lists whose <c>changedOnLanguage</c> flag is (dry run: would be) set to another value.</summary>
    public int OptionsChanged { get; set; }

    /// <summary>
    /// What changes on the host: 0 means the lists are as delivered. In a real run an overwritten
    /// entry is always written and is not counted (only creates, fills, list creates, reorders and
    /// flag changes are); in a dry run an overwrite counts only when a field would change.
    /// Host-only entries are not a change.
    /// </summary>
    public int Changes => Created + Overwritten * (CountsOverwrites ? 1 : 0) + ListsCreated + FilledFields + Reordered + OptionsChanged;

    /// <summary>True for a dry-run result, where <see cref="Overwritten"/> counts only entries with a field diff.</summary>
    public bool CountsOverwrites { get; set; }

    /// <summary>The dry run's per-list and per-entry change lines, in the order they were found.</summary>
    public List<string> DryRunLines { get; } = new();

    /// <summary>
    /// The dry run's information lines: what the real run leaves alone and reports (a host-only
    /// entry). They are not changes: a document with only notes is unchanged.
    /// </summary>
    public List<string> DryRunNotes { get; } = new();
}

/// <summary>
/// Content ownership R7: writes the item lists a document carries onto the host, by the
/// document's lifecycle. The rules are <see cref="ItemListPlanner"/>'s; this class turns a plan
/// into writes through <see cref="IItemListHost"/> and the deserializer's own item field writers,
/// so a list entry's fields are written exactly like a paragraph's (source-wins with the
/// null-out of absent fields, or the Merge fill).
/// </summary>
/// <remarks>
/// <para>An entry the host does not have is created under a new number from the host's own
/// counter, and its key is recorded in the entry map (<see cref="ItemListIdentity"/>), so the
/// next delivery matches it.</para>
/// <para>It fails loudly instead of dropping an entry: a list name that is not an item list
/// field of the owner's item type on this host, a list of pages or paragraphs, an entry item
/// type the host does not have, an entry without an id and a duplicate id all throw, which fails
/// the owning document.</para>
/// </remarks>
public sealed class ItemListWriter
{
    /// <summary>Writes every field of an item, source-wins; <c>protectedFields</c> are not nulled when absent.</summary>
    public delegate void SaveFields(string itemType, string itemId, Dictionary<string, object> fields, IReadOnlySet<string>? protectedFields);

    /// <summary>Fills the empty fields of an item and returns how many it filled; <c>excludedFields</c> are skipped.</summary>
    public delegate int FillFields(string itemType, string itemId, Dictionary<string, object> fields, IReadOnlySet<string>? excludedFields);

    /// <summary>Lists nested deeper than this are refused; Dynamicweb itself renders three levels.</summary>
    public const int MaxDepth = 5;

    private readonly IItemListHost _host;
    private readonly SaveFields _saveFields;
    private readonly FillFields _fillFields;
    private readonly Func<string?, IReadOnlySet<string>?> _excludedFieldsFor;
    private readonly Action<string>? _log;
    private readonly bool _isDryRun;
    private readonly Func<string, Dictionary<string, object>, Dictionary<string, object>>? _resolveLinksForDiff;
    private readonly Dictionary<string, List<ItemListMemberRef>> _writtenMembers = new(StringComparer.OrdinalIgnoreCase);

    public ItemListWriter(
        IItemListHost host,
        SaveFields saveFields,
        FillFields fillFields,
        Func<string?, IReadOnlySet<string>?>? excludedFieldsFor = null,
        Action<string>? log = null,
        bool isDryRun = false,
        Func<string, Dictionary<string, object>, Dictionary<string, object>>? resolveLinksForDiff = null)
    {
        _resolveLinksForDiff = resolveLinksForDiff;
        _host = host;
        _saveFields = saveFields;
        _fillFields = fillFields;
        _excludedFieldsFor = excludedFieldsFor ?? (_ => null);
        _log = log;
        _isDryRun = isDryRun;
    }

    private static string OwnerItemKey(string itemType, string itemId) => $"{itemType}|{itemId}";

    /// <summary>What every <see cref="Apply"/> of this writer did (dry run: would do), summed.</summary>
    public ItemListCounts Totals { get; private set; } = ItemListCounts.Zero;

    /// <summary>
    /// The list entries (nested ones included) this writer created, overwrote or filled under a
    /// paragraph's or page's item. The link pass resolves exactly these: an entry the delivery
    /// did not write belongs to the host.
    /// </summary>
    public IReadOnlyList<ItemListMemberRef> WrittenMembers(string? ownerItemType, string? ownerItemId) =>
        !string.IsNullOrEmpty(ownerItemType) && !string.IsNullOrEmpty(ownerItemId)
        && _writtenMembers.TryGetValue(OwnerItemKey(ownerItemType, ownerItemId), out var members)
            ? members
            : Array.Empty<ItemListMemberRef>();

    /// <summary>
    /// Delivers the lists of one document onto its item. <paramref name="lists"/> null or empty is
    /// a document without item lists (or one written through 1.0.9-beta): nothing happens.
    /// </summary>
    public ItemListWriteResult Apply(
        ItemListOwner owner,
        IReadOnlyDictionary<string, SerializedItemList>? lists,
        IReadOnlyDictionary<string, SerializedItemListOptions>? options,
        ItemListLifecycle lifecycle)
    {
        var result = new ItemListWriteResult { CountsOverwrites = _isDryRun };
        if (lists is null || lists.Count == 0)
            return result;

        var written = new List<ItemListMemberRef>();
        ApplyLists(owner, lists, options, lifecycle, result, written, depth: 1);
        Totals = Totals.Add(result);

        if (written.Count > 0)
        {
            var key = OwnerItemKey(owner.ItemType, owner.ItemId);
            if (!_writtenMembers.TryGetValue(key, out var all))
                _writtenMembers[key] = all = new List<ItemListMemberRef>();
            all.AddRange(written);
        }
        return result;
    }

    private void ApplyLists(
        ItemListOwner owner,
        IReadOnlyDictionary<string, SerializedItemList> lists,
        IReadOnlyDictionary<string, SerializedItemListOptions>? options,
        ItemListLifecycle lifecycle,
        ItemListWriteResult result,
        List<ItemListMemberRef> written,
        int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException(
                $"{owner.Label}: item lists are nested more than {MaxDepth} levels deep.");

        foreach (var (field, list) in lists.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            SerializedItemListOptions? listOptions = null;
            options?.TryGetValue(field, out listOptions);
            ApplyList(owner, field, list, listOptions, lifecycle, result, written, depth);
        }
    }

    private void ApplyList(
        ItemListOwner owner,
        string field,
        SerializedItemList? list,
        SerializedItemListOptions? options,
        ItemListLifecycle lifecycle,
        ItemListWriteResult result,
        List<ItemListMemberRef> written,
        int depth)
    {
        if (list is null)
            throw new InvalidOperationException(
                $"{owner.Label}: item list '{field}' has no value; write [] for an empty list.");

        var info = _host.GetListField(owner.ItemType, field)
            ?? throw new InvalidOperationException(
                $"{owner.Label}: itemLists names '{field}', which is not an item list field of item type " +
                $"'{owner.ItemType}' on this host. Deliver the item type that declares it, or remove the list from the document.");
        if (!info.IsInline)
            throw new InvalidOperationException(
                $"{owner.Label}: item list '{field}' lists {info.ItemSource} items on this host, not entries of its own; " +
                "only an inline list travels inside its document.");
        var listId = string.IsNullOrEmpty(owner.ItemId) ? 0 : _host.GetListId(owner.ItemType, owner.ItemId, field);
        var hostListType = listId > 0 ? _host.GetListItemType(listId) : null;
        var hasList = hostListType is not null;

        // An empty list for a field that has none is nothing to write. It is also the one case
        // that needs no entry item type: stock Swift declares a list field whose entry type it
        // does not ship, and such a field is always empty.
        if (list.Entries.Count == 0 && !hasList)
            return;

        if (!_host.ItemTypeExists(info.MemberItemType))
            throw new InvalidOperationException(
                $"{owner.Label}: item list '{field}' holds entries of item type '{info.MemberItemType}', which is not registered on this host. " +
                $"Deliver the item type (System/Items/ItemType_{info.MemberItemType}.xml) and recycle before deserializing content.");
        if (hasList && !string.Equals(hostListType, info.MemberItemType, StringComparison.OrdinalIgnoreCase))
        {
            // The field points at a list of another item type: a list id that was reused or
            // delivered for something else. It is not this owner's list; leave it alone.
            // Kept a WARNING (owner decision of 2026-11-13): a strict delivery stops on it, and the
            // Foundry's pre-delivery audit names such fields before a delivery gets here. The dry
            // run reports it as a diff line instead, so a strict dry run still answers.
            var mismatch = $"{owner.Label}: field '{field}' points at item list {listId} of item type '{hostListType}', " +
                           $"not '{info.MemberItemType}'. The list is left alone and the field gets a list of its own.";
            if (_isDryRun)
            {
                result.DryRunLines.Add($"item list '{field}' on {owner.Label}: the field points at list {listId} of item type '{hostListType}'; a new list would be created");
                _log?.Invoke($"[DRY-RUN] {mismatch} The real run reports this as a WARNING.");
            }
            else
            {
                _log?.Invoke($"WARNING: {mismatch}");
            }
            hasList = false;
        }

        var hostMemberIds = hasList ? _host.GetMemberIds(listId) : Array.Empty<string>();

        // The entry map: the host entries this engine created for these document entries before.
        // Asked only when there is a list to find them in.
        var keyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list.Entries)
            if (!string.IsNullOrWhiteSpace(entry.Id))
                keyOf[entry.Id] = ItemListIdentity.EntryKey(owner.Key, field, entry.Id);
        var mapped = hasList && hostMemberIds.Count > 0 && keyOf.Count > 0
            ? _host.FindMappedEntries(info.MemberItemType, keyOf.Values.Distinct().ToList())
            : new Dictionary<string, string>();

        var plan = ItemListPlanner.Plan(
            lifecycle, field,
            list.Entries.Select(e => e.Id).ToList(),
            hostMemberIds,
            id => keyOf.TryGetValue(id, out var key) && mapped.TryGetValue(key, out var hostId) ? hostId : null);

        var creates = plan.Creates;
        var matches = plan.Entries.Count - creates;
        var reordered = hasList && plan.ReordersHostMembers();
        var changedOnLanguage = options?.ChangedOnLanguage ?? false;
        // The language flag is the list's own state: written with a new or overwritten list,
        // left to the host under fill.
        var writesFlag = !hasList || lifecycle == ItemListLifecycle.Overwrite;
        var flagChanges = writesFlag && hasList && _host.GetChangedOnLanguage(listId) != changedOnLanguage;

        // A delivery never deletes a list entry (owner decision of 2026-11-13). An entry only the
        // host has stays in the list with its item and the lists it carries. Under overwrite it
        // sits after the delivered entries, in the relative order the host gave it. Each one is
        // information (owner decision B of 2026-11-13): a log line in the real run, a note in the
        // dry run, never a WARNING, so strict mode does not fail on it.
        result.HostOnly += plan.HostOnly.Count;
        if (lifecycle == ItemListLifecycle.Overwrite)
        {
            foreach (var hostOnlyId in plan.HostOnly)
            {
                var message = $"{owner.Label}: item list '{field}' (list {listId}) has entry '{hostOnlyId}' on this host " +
                              "that the document does not have. It is left in place after the delivered entries: a Replace " +
                              "never deletes a list entry. Remove it on the host, or add it to the source, if it should not stay.";
                if (_isDryRun)
                {
                    result.DryRunNotes.Add($"host-only entry '{hostOnlyId}' of item list '{field}' on {owner.Label}: left in place");
                    _log?.Invoke($"[DRY-RUN] Host-only list entry (information): {message}");
                }
                else
                {
                    _log?.Invoke($"Host-only list entry (information): {message}");
                }
            }
        }
        if (reordered) result.Reordered++;
        if (flagChanges) result.OptionsChanged++;

        if (_isDryRun)
        {
            DryRunList(owner, field, list, info, plan, lifecycle, hasList, listId, reordered, flagChanges, changedOnLanguage, result, depth);
            return;
        }

        if (!hasList)
        {
            listId = _host.CreateList(info.MemberItemType);
            _host.SetListId(owner.ItemType, owner.ItemId, field, listId);
            result.ListsCreated++;
        }

        var memberExclusions = _excludedFieldsFor(info.MemberItemType);
        var createdIds = new Dictionary<int, string>();
        var wroteAnEntry = false;
        foreach (var entryPlan in plan.Entries)
        {
            var entry = list.Entries[entryPlan.YamlIndex];
            var nestedLists = entry.ItemLists is { Count: > 0 } ? entry.ItemLists : null;
            var fields = FieldsWithoutLists(entry.Fields, nestedLists);

            string hostId;
            bool exists;
            if (entryPlan.HostId is null)
            {
                // A new entry: a new number from the host's own counter, never the document's id.
                // The entry map remembers which document entry it is.
                hostId = _host.CreateItem(info.MemberItemType);
                _host.RecordEntry(new ItemListEntryMapping(
                    keyOf[entry.Id], info.MemberItemType, hostId, owner.Key, field, entry.Id));
                createdIds[entryPlan.YamlIndex] = hostId;
                exists = false;
            }
            else
            {
                hostId = entryPlan.HostId;
                // A relation can outlive its item; such an entry is put back and written in full.
                exists = _host.ItemExists(info.MemberItemType, hostId);
                if (!exists)
                    _host.RestoreItem(info.MemberItemType, hostId);
            }

            // An item save empties ItemInstanceType on a type without a base; an entry that
            // arrived as a table row keeps the value it came with (owner decision C).
            var instanceType = exists ? _host.GetInstanceType(info.MemberItemType, hostId) : null;

            var nestedLifecycle = ItemListLifecycle.Overwrite;
            var saved = false;
            if (entryPlan.Action == ItemListEntryAction.Fill && exists)
            {
                var filledFields = _fillFields(info.MemberItemType, hostId, fields, memberExclusions);
                result.FilledFields += filledFields;
                wroteAnEntry |= filledFields > 0;
                saved = filledFields > 0;
                result.Filled++;
                nestedLifecycle = ItemListLifecycle.Fill;
            }
            else
            {
                _saveFields(info.MemberItemType, hostId, fields, Protect(memberExclusions, nestedLists));
                wroteAnEntry = true;
                saved = true;
                if (entryPlan.Action == ItemListEntryAction.Create) result.Created++;
                else result.Overwritten++;
            }

            if (saved && !string.IsNullOrEmpty(instanceType))
                _host.KeepInstanceType(info.MemberItemType, hostId, instanceType);

            written.Add(new ItemListMemberRef(info.MemberItemType, hostId));

            if (nestedLists is not null)
            {
                var entryOwner = new ItemListOwner(
                    info.MemberItemType,
                    hostId,
                    ItemListIdentity.EntryOwner(owner.Key, field, entry.Id),
                    $"{owner.Label}, item list '{field}' entry '{entry.Id}'");
                ApplyLists(entryOwner, nestedLists, options: null, nestedLifecycle, result, written, depth + 1);
            }
        }

        // The relations are rewritten only when membership or order changes. FinalOrder always
        // holds every member the host had, so no relation is dropped and no item is deleted.
        // An item save resets the item's own Sort; when the relations stay as they are, only
        // that position is put back, so an unchanged list keeps its relation rows.
        var finalOrder = plan.FinalOrder(createdIds);
        if (!hasList || !plan.KeepsMembership())
            _host.SaveMembers(listId, finalOrder);
        else if (wroteAnEntry)
            _host.SyncMemberSort(listId, info.MemberItemType);

        if (writesFlag && _host.GetChangedOnLanguage(listId) != changedOnLanguage)
            _host.SetChangedOnLanguage(listId, changedOnLanguage);

        _log?.Invoke($"Item list '{field}' on {owner.Label} ({lifecycle.ToString().ToLowerInvariant()}): " +
                     $"{creates} created, {matches} {(lifecycle == ItemListLifecycle.Fill ? "filled" : "overwritten")}, " +
                     $"{plan.HostOnly.Count} host-only left in place{(reordered ? ", order changed" : "")}, " +
                     $"{finalOrder.Count} in the list");
    }

    /// <summary>
    /// The dry run of one list: what the real run would create, change, fill and leave, with the
    /// field diff of every matched entry, nested lists included. Reads only.
    /// </summary>
    private void DryRunList(
        ItemListOwner owner,
        string field,
        SerializedItemList list,
        ItemListFieldInfo info,
        ItemListPlan plan,
        ItemListLifecycle lifecycle,
        bool hasList,
        int listId,
        bool reordered,
        bool flagChanges,
        bool changedOnLanguage,
        ItemListWriteResult result,
        int depth)
    {
        if (!hasList)
        {
            result.ListsCreated++;
            if (changedOnLanguage) result.OptionsChanged++;
        }

        var memberExclusions = _excludedFieldsFor(info.MemberItemType);
        int toCreate = 0, toChange = 0, unchanged = 0;
        foreach (var entryPlan in plan.Entries)
        {
            var entry = list.Entries[entryPlan.YamlIndex];
            var nestedLists = entry.ItemLists is { Count: > 0 } ? entry.ItemLists : null;
            // The dry run compares an entry's fields after the link resolution the real run's link
            // pass applies: the document holds source ids, the host target ids.
            var fields = FieldsWithoutLists(entry.Fields, nestedLists);
            if (_resolveLinksForDiff is not null)
                fields = _resolveLinksForDiff(info.MemberItemType, fields);
            var hostFields = entryPlan.HostId is null
                ? null
                : _host.GetItemFields(info.MemberItemType, entryPlan.HostId);
            var ignored = IgnoredForDiff(info.MemberItemType, memberExclusions, nestedLists);

            var nestedLifecycle = ItemListLifecycle.Overwrite;
            if (hostFields is null)
            {
                // A new entry, or a relation whose item is gone: the real run writes it in full.
                toCreate++;
                result.Created++;
                result.DryRunLines.Add($"create entry '{entry.Id}' in item list '{field}' on {owner.Label}");
            }
            else if (entryPlan.Action == ItemListEntryAction.Fill)
            {
                nestedLifecycle = ItemListLifecycle.Fill;
                var fills = ItemListFieldDiff.Fill(fields, hostFields, ignored);
                result.Filled++;
                result.FilledFields += fills.Count;
                if (fills.Count > 0)
                {
                    toChange++;
                    result.DryRunLines.Add($"fill entry '{entry.Id}' in item list '{field}' on {owner.Label}: {string.Join("; ", fills)}");
                }
                else unchanged++;
            }
            else
            {
                var diffs = ItemListFieldDiff.Overwrite(fields, hostFields, ignored);
                if (diffs.Count > 0)
                {
                    toChange++;
                    result.Overwritten++;
                    result.DryRunLines.Add($"update entry '{entry.Id}' in item list '{field}' on {owner.Label}: {string.Join("; ", diffs)}");
                }
                else unchanged++;
            }

            if (nestedLists is not null)
            {
                // A created entry has no item yet: its lists are all new.
                var entryOwner = new ItemListOwner(
                    info.MemberItemType,
                    hostFields is null ? string.Empty : entryPlan.HostId!,
                    ItemListIdentity.EntryOwner(owner.Key, field, entry.Id),
                    $"{owner.Label}, item list '{field}' entry '{entry.Id}'");
                ApplyLists(entryOwner, nestedLists, options: null, nestedLifecycle, result, new List<ItemListMemberRef>(), depth + 1);
            }
        }

        if (reordered)
            result.DryRunLines.Add($"order of item list '{field}' on {owner.Label} changes to the document's");
        if (flagChanges)
            result.DryRunLines.Add($"item list '{field}' on {owner.Label}: changedOnLanguage -> {changedOnLanguage.ToString().ToLowerInvariant()}");

        _log?.Invoke($"[DRY-RUN] Item list '{field}' on {owner.Label} ({lifecycle.ToString().ToLowerInvariant()}, " +
                     $"{(hasList ? $"list {listId}" : "new list")}): {toCreate} to create, " +
                     $"{toChange} to {(lifecycle == ItemListLifecycle.Fill ? "fill" : "update")}, {unchanged} unchanged, " +
                     $"{plan.HostOnly.Count} host-only left in place{(reordered ? ", order changes" : "")}" +
                     $"{(flagChanges ? ", changedOnLanguage changes" : "")}");
    }

    /// <summary>
    /// Fields the dry-run diff leaves out: the entry's system fields, the excluded ones, its
    /// nested lists and any field the host declares as an item list (its value is a host list id;
    /// the nested lists are diffed as lists).
    /// </summary>
    private Func<string, bool> IgnoredForDiff(
        string memberItemType,
        IReadOnlySet<string>? memberExclusions,
        IReadOnlyDictionary<string, SerializedItemList>? nestedLists)
    {
        var set = new HashSet<string>(ItemListExtractor.EntrySystemFields, StringComparer.OrdinalIgnoreCase);
        if (memberExclusions is not null) set.UnionWith(memberExclusions);
        if (nestedLists is not null) set.UnionWith(nestedLists.Keys);
        return name => set.Contains(name) || _host.GetListField(memberItemType, name) is not null;
    }

    /// <summary>
    /// The fields to hand to the item field writers: without the fields the document's
    /// <c>itemLists</c> owns. A list field's value is a host list id, which only the list writer sets.
    /// </summary>
    public static Dictionary<string, object> FieldsWithoutLists(
        Dictionary<string, object> fields,
        IReadOnlyDictionary<string, SerializedItemList>? lists)
    {
        if (lists is null || lists.Count == 0)
            return fields;
        if (!fields.Keys.Any(key => lists.Keys.Any(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase))))
            return fields;
        return fields
            .Where(kv => !lists.Keys.Any(name => string.Equals(name, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// The fields the source-wins write must not null: the excluded ones plus the fields the
    /// document's <c>itemLists</c> owns (absent from <c>fields</c> by design).
    /// </summary>
    public static IReadOnlySet<string>? Protect(
        IReadOnlySet<string>? excludedFields,
        IReadOnlyDictionary<string, SerializedItemList>? lists)
    {
        if (lists is null || lists.Count == 0)
            return excludedFields;
        var set = new HashSet<string>(lists.Keys, StringComparer.OrdinalIgnoreCase);
        if (excludedFields is not null)
            set.UnionWith(excludedFields);
        return set;
    }
}
