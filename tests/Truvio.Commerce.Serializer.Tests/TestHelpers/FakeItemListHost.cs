using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;

namespace Truvio.Commerce.Serializer.Tests.TestHelpers;

/// <summary>
/// Content ownership R7: an in-memory host for the item list writer. Items are field maps keyed
/// by item type and id, lists are ordered member ids, and the two field writers behave like the
/// deserializer's: <see cref="Save"/> is source-wins with the null-out of absent fields,
/// <see cref="Fill"/> writes only fields the item left empty. New items are numbered by a counter
/// per item type, as Dynamicweb's <c>ItemTypeId</c> does, and an item save empties
/// <c>ItemInstanceType</c>, as Dynamicweb does for an item type without a base. The entry map is
/// a dictionary. Every write is journalled.
/// </summary>
internal sealed class FakeItemListHost : IItemListHost, IItemListSource
{
    public sealed class FakeList
    {
        public required string ItemType { get; init; }
        public List<string> Members { get; } = new();
        public bool ChangedOnLanguage { get; set; }
    }

    private static readonly HashSet<string> SystemFields = new(StringComparer.OrdinalIgnoreCase) { "Id", "Sort", "ItemInstanceType" };

    private readonly Dictionary<string, Dictionary<string, ItemListFieldInfo>> _listFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _itemTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unregistered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, object?>> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
    private int _nextListId = 100;

    public Dictionary<int, FakeList> Lists { get; } = new();

    /// <summary>The entry map, by entry key.</summary>
    public Dictionary<string, ItemListEntryMapping> EntryMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every write the writer made, in order.</summary>
    public List<string> Journal { get; } = new();

    public List<string> Log { get; } = new();

    private static string Key(string itemType, string itemId) => $"{itemType}|{itemId}";

    // -- arrangement ----------------------------------------------------------

    public FakeItemListHost WithItemType(string itemType)
    {
        _itemTypes.Add(itemType);
        return this;
    }

    /// <summary>The item type's fields stay declared, but the type is not registered on the host.</summary>
    public FakeItemListHost WithoutItemType(string itemType)
    {
        _unregistered.Add(itemType);
        return this;
    }

    /// <summary>Sets the item type's id counter (<c>ItemTypeId.Current</c>).</summary>
    public FakeItemListHost WithCounter(string itemType, int current)
    {
        _counters[itemType] = current;
        return this;
    }

    public FakeItemListHost WithListField(string ownerItemType, string field, string memberItemType, string itemSource = "ListItem")
    {
        _itemTypes.Add(ownerItemType);
        _itemTypes.Add(memberItemType);
        if (!_listFields.TryGetValue(ownerItemType, out var fields))
            _listFields[ownerItemType] = fields = new Dictionary<string, ItemListFieldInfo>(StringComparer.OrdinalIgnoreCase);
        fields[field] = new ItemListFieldInfo(memberItemType, itemSource);
        return this;
    }

    public FakeItemListHost WithItem(string itemType, string itemId, params (string Field, object? Value)[] fields)
    {
        _itemTypes.Add(itemType);
        var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in fields)
            item[field] = value;
        _items[Key(itemType, itemId)] = item;
        return this;
    }

    /// <summary>Gives an owner item an existing list with these member items (created empty when missing).</summary>
    public int WithList(string ownerItemType, string ownerItemId, string field, string memberItemType, params string[] memberIds)
    {
        var listId = _nextListId++;
        var list = new FakeList { ItemType = memberItemType };
        list.Members.AddRange(memberIds);
        Lists[listId] = list;
        foreach (var memberId in memberIds)
            if (!_items.ContainsKey(Key(memberItemType, memberId)))
                WithItem(memberItemType, memberId);
        if (!_items.ContainsKey(Key(ownerItemType, ownerItemId)))
            WithItem(ownerItemType, ownerItemId);
        _items[Key(ownerItemType, ownerItemId)][field] = listId;
        return listId;
    }

    /// <summary>Records an entry map row, as an earlier delivery would have.</summary>
    public FakeItemListHost WithMapping(string ownerKey, string field, string sourceId, string itemType, string itemId)
    {
        var key = ItemListIdentity.EntryKey(ownerKey, field, sourceId);
        EntryMap[key] = new ItemListEntryMapping(key, itemType, itemId, ownerKey, field, sourceId);
        return this;
    }

    // -- inspection -----------------------------------------------------------

    public IReadOnlyDictionary<string, object?>? Item(string itemType, string itemId) =>
        _items.TryGetValue(Key(itemType, itemId), out var item) ? item : null;

    public object? Field(string itemType, string itemId, string field) =>
        Item(itemType, itemId) is { } item && item.TryGetValue(field, out var value) ? value : null;

    public IReadOnlyList<string> Members(string ownerItemType, string ownerItemId, string field)
    {
        var listId = GetListId(ownerItemType, ownerItemId, field);
        return Lists.TryGetValue(listId, out var list) ? list.Members : Array.Empty<string>();
    }

    public int ItemCount(string itemType) =>
        _items.Keys.Count(k => k.StartsWith(itemType + "|", StringComparison.OrdinalIgnoreCase));

    /// <summary>The host id the entry map gives a document entry; null when it has none.</summary>
    public string? MappedId(string ownerKey, string field, string sourceId) =>
        EntryMap.TryGetValue(ItemListIdentity.EntryKey(ownerKey, field, sourceId), out var row) ? row.ItemId : null;

    // -- the two field writers -------------------------------------------------

    public void Save(string itemType, string itemId, Dictionary<string, object> fields, IReadOnlySet<string>? protectedFields)
    {
        var item = _items[Key(itemType, itemId)];
        foreach (var name in item.Keys.ToList())
            if (!SystemFields.Contains(name) && !fields.ContainsKey(name) && protectedFields?.Contains(name) != true)
                item[name] = null;
        foreach (var (name, value) in fields)
            item[name] = value;
        // Dynamicweb writes an empty ItemInstanceType on every save of an item type without a base.
        if (item.ContainsKey("ItemInstanceType"))
            item["ItemInstanceType"] = string.Empty;
        Journal.Add($"save {itemType}/{itemId}");
    }

    public int Fill(string itemType, string itemId, Dictionary<string, object> fields, IReadOnlySet<string>? excludedFields)
    {
        var item = _items[Key(itemType, itemId)];
        var filled = 0;
        foreach (var (name, value) in fields)
        {
            if (excludedFields?.Contains(name) == true) continue;
            item.TryGetValue(name, out var current);
            if (!MergePredicate.IsUnsetForMerge(current?.ToString())) continue;
            item[name] = value;
            filled++;
        }
        if (filled > 0)
        {
            if (item.ContainsKey("ItemInstanceType"))
                item["ItemInstanceType"] = string.Empty;
            Journal.Add($"fill {itemType}/{itemId} ({filled})");
        }
        return filled;
    }

    public ItemListWriter Writer(bool isDryRun = false, Func<string?, IReadOnlySet<string>?>? excludedFieldsFor = null) =>
        new(this, Save, Fill, excludedFieldsFor, Log.Add, isDryRun);

    // -- IItemListHost ----------------------------------------------------------

    public ItemListFieldInfo? GetListField(string ownerItemType, string field) =>
        _listFields.TryGetValue(ownerItemType, out var fields) && fields.TryGetValue(field, out var info) ? info : null;

    public bool ItemTypeExists(string itemType) => _itemTypes.Contains(itemType) && !_unregistered.Contains(itemType);

    public int GetListId(string ownerItemType, string ownerItemId, string field) =>
        Field(ownerItemType, ownerItemId, field) is int listId ? listId : 0;

    public string? GetListItemType(int listId) => Lists.TryGetValue(listId, out var list) ? list.ItemType : null;

    public IReadOnlyList<string> GetMemberIds(int listId) =>
        Lists.TryGetValue(listId, out var list) ? list.Members.ToList() : Array.Empty<string>();

    public int CreateList(string memberItemType)
    {
        var listId = _nextListId++;
        Lists[listId] = new FakeList { ItemType = memberItemType };
        Journal.Add($"create list {listId} ({memberItemType})");
        return listId;
    }

    public void SetListId(string ownerItemType, string ownerItemId, string field, int listId)
    {
        _items[Key(ownerItemType, ownerItemId)][field] = listId;
        Journal.Add($"point {ownerItemType}/{ownerItemId}.{field} at list {listId}");
    }

    public bool ItemExists(string itemType, string itemId) => _items.ContainsKey(Key(itemType, itemId));

    public string CreateItem(string itemType)
    {
        // As the platform's id generator: Current + 1, moved on past ids the table already holds.
        _counters.TryGetValue(itemType, out var current);
        string id;
        do
        {
            current++;
            id = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
        } while (_items.ContainsKey(Key(itemType, id)));
        _counters[itemType] = current;
        _items[Key(itemType, id)] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Journal.Add($"create {itemType}/{id}");
        return id;
    }

    /// <summary>The item type's id counter (<c>ItemTypeId.Current</c>).</summary>
    public int Counter(string itemType) => _counters.TryGetValue(itemType, out var current) ? current : 0;

    public void RestoreItem(string itemType, string itemId)
    {
        _items[Key(itemType, itemId)] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Journal.Add($"restore {itemType}/{itemId}");
    }

    public IReadOnlyDictionary<string, string> FindMappedEntries(string itemType, IReadOnlyCollection<string> entryKeys)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in entryKeys)
            if (EntryMap.TryGetValue(key, out var row) && string.Equals(row.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
                found[key] = row.ItemId;
        return found;
    }

    public void RecordEntry(ItemListEntryMapping mapping)
    {
        EntryMap[mapping.EntryKey] = mapping;
        Journal.Add($"map {mapping.SourceId} -> {mapping.ItemType}/{mapping.ItemId}");
    }

    public IReadOnlyDictionary<string, ItemListEntryMapping> GetEntryMappings(string itemType, IReadOnlyCollection<string> itemIds) =>
        EntryMap.Values
            .Where(r => string.Equals(r.ItemType, itemType, StringComparison.OrdinalIgnoreCase)
                        && itemIds.Contains(r.ItemId, StringComparer.OrdinalIgnoreCase))
            .GroupBy(r => r.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public string? GetInstanceType(string itemType, string itemId) =>
        Item(itemType, itemId) is { } item ? (item.TryGetValue("ItemInstanceType", out var v) ? v?.ToString() ?? string.Empty : string.Empty) : null;

    public void KeepInstanceType(string itemType, string itemId, string instanceType)
    {
        var item = _items[Key(itemType, itemId)];
        if (item.TryGetValue("ItemInstanceType", out var v) && !string.IsNullOrEmpty(v?.ToString()))
            return;
        item["ItemInstanceType"] = instanceType;
        Journal.Add($"keep instance type {itemType}/{itemId}");
    }

    public IReadOnlyDictionary<string, object?>? GetItemFields(string itemType, string itemId) =>
        _items.TryGetValue(Key(itemType, itemId), out var item)
            ? new Dictionary<string, object?>(item, StringComparer.OrdinalIgnoreCase)
            : null;

    public void SaveMembers(int listId, IReadOnlyList<string> orderedItemIds)
    {
        var list = Lists[listId];
        // The writer never drops a member (a delivery never deletes a list entry).
        var dropped = list.Members.Where(m => !orderedItemIds.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        if (dropped.Count > 0)
            throw new InvalidOperationException($"SaveMembers would drop {string.Join(",", dropped)} from list {listId}.");
        list.Members.Clear();
        list.Members.AddRange(orderedItemIds);
        Journal.Add($"members of list {listId}: {string.Join(",", orderedItemIds)}");
    }

    public void SyncMemberSort(int listId, string memberItemType) => Journal.Add($"sort of list {listId}");

    public bool GetChangedOnLanguage(int listId) => Lists[listId].ChangedOnLanguage;

    public void SetChangedOnLanguage(int listId, bool changed)
    {
        Lists[listId].ChangedOnLanguage = changed;
        Journal.Add($"list {listId} changedOnLanguage={changed}");
    }

    // -- IItemListSource ----------------------------------------------------------

    public IReadOnlyDictionary<string, ItemListFieldInfo>? GetListFields(string itemType) =>
        _listFields.TryGetValue(itemType, out var fields) ? fields : new Dictionary<string, ItemListFieldInfo>();

    public ItemListSnapshot? ReadList(int listId)
    {
        if (!Lists.TryGetValue(listId, out var list))
            return null;
        var members = new List<ItemListMember>();
        var dangling = new List<string>();
        foreach (var id in list.Members)
        {
            if (_items.TryGetValue(Key(list.ItemType, id), out var item))
                members.Add(new ItemListMember(id, new Dictionary<string, object?>(item, StringComparer.Ordinal)));
            else
                dangling.Add(id);
        }
        return new ItemListSnapshot(list.ItemType, list.ChangedOnLanguage, members, dangling);
    }
}
