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

    /// <summary>Creates an item under exactly this id.</summary>
    void CreateItem(string itemType, string itemId);

    /// <summary>Deletes an item, with the lists it carries itself.</summary>
    void DeleteItem(string itemType, string itemId);

    /// <summary>Makes the list's members exactly these ids, in this order, and gives each member item its position.</summary>
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

/// <summary>What one delivery did to the lists of one owner, nested lists included.</summary>
public sealed class ItemListWriteResult
{
    public int Created { get; set; }
    public int Overwritten { get; set; }
    public int Filled { get; set; }
    public int Removed { get; set; }
    public int ListsCreated { get; set; }

    /// <summary>Entries the delivery created or removed, lists it created, and fields it filled: 0 means the host is as it was.</summary>
    public int Changes => Created + Removed + ListsCreated + FilledFields;

    /// <summary>Fields a Fill wrote into entries the host already had.</summary>
    public int FilledFields { get; set; }
}

/// <summary>
/// Content ownership R7: writes the item lists a document carries onto the host, by the
/// document's lifecycle. The rules are <see cref="ItemListPlanner"/>'s; this class turns a plan
/// into writes through <see cref="IItemListHost"/> and the deserializer's own item field writers,
/// so a list entry's fields are written exactly like a paragraph's (source-wins with the
/// null-out of absent fields, or the Merge fill).
/// </summary>
/// <remarks>
/// It fails loudly instead of dropping an entry: a list name that is not an item list field of
/// the owner's item type on this host, a list of pages or paragraphs, an entry item type the
/// host does not have, an entry without an id and a duplicate id all throw, which fails the
/// owning document. An id an unrelated item already uses is reported as a <c>WARNING</c>
/// (strict mode escalates it) and the entry is written under its scoped id.
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
    private readonly Dictionary<string, List<ItemListMemberRef>> _writtenMembers = new(StringComparer.OrdinalIgnoreCase);

    public ItemListWriter(
        IItemListHost host,
        SaveFields saveFields,
        FillFields fillFields,
        Func<string?, IReadOnlySet<string>?>? excludedFieldsFor = null,
        Action<string>? log = null,
        bool isDryRun = false)
    {
        _host = host;
        _saveFields = saveFields;
        _fillFields = fillFields;
        _excludedFieldsFor = excludedFieldsFor ?? (_ => null);
        _log = log;
        _isDryRun = isDryRun;
    }

    private static string OwnerItemKey(string itemType, string itemId) => $"{itemType}|{itemId}";

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
        var result = new ItemListWriteResult();
        if (lists is null || lists.Count == 0)
            return result;

        var written = new List<ItemListMemberRef>();
        ApplyLists(owner, lists, options, lifecycle, result, written, depth: 1);

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
        var listId = _host.GetListId(owner.ItemType, owner.ItemId, field);
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
            _log?.Invoke($"WARNING: {owner.Label}: field '{field}' points at item list {listId} of item type '{hostListType}', " +
                         $"not '{info.MemberItemType}'. The list is left alone and the field gets a list of its own.");
            hasList = false;
        }

        var hostMemberIds = hasList ? _host.GetMemberIds(listId) : Array.Empty<string>();
        var plan = ItemListPlanner.Plan(
            lifecycle, owner.Key, field,
            list.Entries.Select(e => e.Id).ToList(),
            hostMemberIds,
            id => _host.ItemExists(info.MemberItemType, id));

        foreach (var foreignId in plan.ForeignIds)
            _log?.Invoke($"WARNING: {owner.Label}: item list '{field}' entry '{foreignId}' could not keep its id: " +
                         $"an item of type '{info.MemberItemType}' outside this list already uses it on this host. " +
                         $"The entry is written as '{ItemListIdentity.ScopedId(owner.Key, field, foreignId)}'.");

        var creates = plan.Entries.Count(e => e.Action == ItemListEntryAction.Create);
        var matches = plan.Entries.Count - creates;

        if (_isDryRun)
        {
            _log?.Invoke($"[DRY-RUN] Item list '{field}' on {owner.Label} ({lifecycle.ToString().ToLowerInvariant()}): " +
                         $"{creates} to create, {matches} to {(lifecycle == ItemListLifecycle.Fill ? "fill" : "overwrite")}, " +
                         $"{plan.Remove.Count} to remove");
            result.Created += creates;
            result.Removed += plan.Remove.Count;
            return;
        }

        if (!hasList)
        {
            listId = _host.CreateList(info.MemberItemType);
            _host.SetListId(owner.ItemType, owner.ItemId, field, listId);
            result.ListsCreated++;
        }

        var memberExclusions = _excludedFieldsFor(info.MemberItemType);
        var wroteAnEntry = false;
        foreach (var entryPlan in plan.Entries)
        {
            var entry = list.Entries[entryPlan.YamlIndex];
            var nestedLists = entry.ItemLists is { Count: > 0 } ? entry.ItemLists : null;
            var fields = FieldsWithoutLists(entry.Fields, nestedLists);

            // A relation can outlive its item; a matched entry whose item is gone is written in full.
            var exists = _host.ItemExists(info.MemberItemType, entryPlan.TargetId);
            if (!exists)
                _host.CreateItem(info.MemberItemType, entryPlan.TargetId);

            var nestedLifecycle = ItemListLifecycle.Overwrite;
            if (entryPlan.Action == ItemListEntryAction.Fill && exists)
            {
                var filledFields = _fillFields(info.MemberItemType, entryPlan.TargetId, fields, memberExclusions);
                result.FilledFields += filledFields;
                wroteAnEntry |= filledFields > 0;
                result.Filled++;
                nestedLifecycle = ItemListLifecycle.Fill;
            }
            else
            {
                _saveFields(info.MemberItemType, entryPlan.TargetId, fields, Protect(memberExclusions, nestedLists));
                wroteAnEntry = true;
                if (entryPlan.Action == ItemListEntryAction.Create) result.Created++;
                else result.Overwritten++;
            }

            written.Add(new ItemListMemberRef(info.MemberItemType, entryPlan.TargetId));

            if (nestedLists is not null)
            {
                var entryOwner = new ItemListOwner(
                    info.MemberItemType,
                    entryPlan.TargetId,
                    ItemListIdentity.EntryOwner(owner.Key, field, entryPlan.CanonicalId),
                    $"{owner.Label}, item list '{field}' entry '{entry.Id}'");
                ApplyLists(entryOwner, nestedLists, options: null, nestedLifecycle, result, written, depth + 1);
            }
        }

        // Relations first, then the items: a removed entry is never left dangling in the list.
        // An item save resets the item's own Sort; when the relations stay as they are, only
        // that position is put back, so an unchanged list keeps its relation rows.
        if (!hasList || !plan.KeepsMembership(hostMemberIds))
            _host.SaveMembers(listId, plan.FinalOrder);
        else if (wroteAnEntry)
            _host.SyncMemberSort(listId, info.MemberItemType);
        foreach (var removedId in plan.Remove)
        {
            _host.DeleteItem(info.MemberItemType, removedId);
            result.Removed++;
        }

        // The language flag is the list's own state: written with a new or overwritten list,
        // left to the host under fill.
        if (!hasList || lifecycle == ItemListLifecycle.Overwrite)
        {
            var changedOnLanguage = options?.ChangedOnLanguage ?? false;
            if (_host.GetChangedOnLanguage(listId) != changedOnLanguage)
                _host.SetChangedOnLanguage(listId, changedOnLanguage);
        }

        _log?.Invoke($"Item list '{field}' on {owner.Label} ({lifecycle.ToString().ToLowerInvariant()}): " +
                     $"{creates} created, {matches} {(lifecycle == ItemListLifecycle.Fill ? "filled" : "overwritten")}, " +
                     $"{plan.Remove.Count} removed, {plan.FinalOrder.Count} in the list");
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
