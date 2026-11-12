using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;

namespace Truvio.Commerce.Serializer.Tests.TestHelpers;

/// <summary>
/// Content ownership R7: an in-memory host for the item list writer. Items are field maps keyed
/// by item type and id, lists are ordered member ids, and the two field writers behave like the
/// deserializer's: <see cref="Save"/> is source-wins with the null-out of absent fields,
/// <see cref="Fill"/> writes only fields the item left empty. Every write is journalled.
/// </summary>
internal sealed class FakeItemListHost : IItemListHost
{
    public sealed class FakeList
    {
        public required string ItemType { get; init; }
        public List<string> Members { get; } = new();
        public bool ChangedOnLanguage { get; set; }
    }

    private readonly Dictionary<string, Dictionary<string, ItemListFieldInfo>> _listFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _itemTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, object?>> _items = new(StringComparer.OrdinalIgnoreCase);
    private int _nextListId = 100;

    public Dictionary<int, FakeList> Lists { get; } = new();

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

    // -- the two field writers -------------------------------------------------

    public void Save(string itemType, string itemId, Dictionary<string, object> fields, IReadOnlySet<string>? protectedFields)
    {
        var item = _items[Key(itemType, itemId)];
        foreach (var name in item.Keys.ToList())
            if (!fields.ContainsKey(name) && protectedFields?.Contains(name) != true)
                item[name] = null;
        foreach (var (name, value) in fields)
            item[name] = value;
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
            Journal.Add($"fill {itemType}/{itemId} ({filled})");
        return filled;
    }

    public ItemListWriter Writer(bool isDryRun = false, Func<string?, IReadOnlySet<string>?>? excludedFieldsFor = null) =>
        new(this, Save, Fill, excludedFieldsFor, Log.Add, isDryRun);

    // -- IItemListHost ----------------------------------------------------------

    public ItemListFieldInfo? GetListField(string ownerItemType, string field) =>
        _listFields.TryGetValue(ownerItemType, out var fields) && fields.TryGetValue(field, out var info) ? info : null;

    public bool ItemTypeExists(string itemType) => _itemTypes.Contains(itemType);

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

    public void CreateItem(string itemType, string itemId)
    {
        _items[Key(itemType, itemId)] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Journal.Add($"create {itemType}/{itemId}");
    }

    public void DeleteItem(string itemType, string itemId)
    {
        // Like Dynamicweb: the item's own lists and their entries go with it.
        if (_items.TryGetValue(Key(itemType, itemId), out var item) && _listFields.TryGetValue(itemType, out var fields))
        {
            foreach (var (field, info) in fields)
            {
                if (item.TryGetValue(field, out var value) && value is int listId && Lists.Remove(listId, out var list))
                    foreach (var memberId in list.Members)
                        DeleteItem(info.MemberItemType, memberId);
            }
        }
        _items.Remove(Key(itemType, itemId));
        Journal.Add($"delete {itemType}/{itemId}");
    }

    public void SaveMembers(int listId, IReadOnlyList<string> orderedItemIds)
    {
        var list = Lists[listId];
        list.Members.Clear();
        list.Members.AddRange(orderedItemIds);
        Journal.Add($"members of list {listId}: {string.Join(",", orderedItemIds)}");
    }

    public bool GetChangedOnLanguage(int listId) => Lists[listId].ChangedOnLanguage;

    public void SetChangedOnLanguage(int listId, bool changed)
    {
        Lists[listId].ChangedOnLanguage = changed;
        Journal.Add($"list {listId} changedOnLanguage={changed}");
    }
}
