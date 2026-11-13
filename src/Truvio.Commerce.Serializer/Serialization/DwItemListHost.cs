using Dynamicweb.Content.Items;
using Dynamicweb.Core;
using Dynamicweb.Data;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: the Dynamicweb half of the item list writer and reader. Lists and their
/// relations are read with plain SQL (<c>ItemList</c>, <c>ItemListRelation</c>): Dynamicweb's
/// <c>ItemList.GetItemListById</c> serves a process cache, and an earlier SqlTable entry of the
/// same run may have written those tables behind it. Items are read through the item repository
/// (<c>SelectById</c>, which bypasses the 15-minute item cache, see
/// <c>ContentDeserializer.ResolveLinksInItemFields</c>) and written through the item API, so the
/// host's notifications and cache eviction run as for any other item save.
/// </summary>
internal sealed class DwItemListHost : IItemListHost, IItemListSource
{
    private readonly Action<string>? _log;

    public DwItemListHost(Action<string>? log = null)
    {
        _log = log;
    }

    public ItemListFieldInfo? GetListField(string ownerItemType, string field)
    {
        var fields = ItemListFieldLookup.For(ownerItemType, _log);
        return fields is not null && fields.TryGetValue(field, out var info) ? info : null;
    }

    public IReadOnlyDictionary<string, ItemListFieldInfo>? GetListFields(string itemType) =>
        ItemListFieldLookup.For(itemType, _log);

    public bool ItemTypeExists(string itemType) =>
        ItemManager.Metadata.GetItemType(itemType) != null;

    public int GetListId(string ownerItemType, string ownerItemId, string field)
    {
        using var repository = ItemManager.Storage.Open(ownerItemType);
        var item = repository.SelectById(ownerItemId);
        return item is not null && item.TryGetValue(field, out var value) ? Converter.ToInt32(value) : 0;
    }

    public string? GetListItemType(int listId)
    {
        if (listId <= 0) return null;
        var value = Database.ExecuteScalar(CommandBuilder.Create(
            "SELECT [ItemListItemType] FROM [ItemList] WHERE [ItemListId] = {0}", listId));
        return value is null || value == DBNull.Value ? null : Converter.ToString(value);
    }

    public IReadOnlyList<string> GetMemberIds(int listId)
    {
        var ids = new List<string>();
        if (listId <= 0) return ids;
        using var reader = Database.CreateDataReader(CommandBuilder.Create(
            "SELECT [ItemListRelationItemId] FROM [ItemListRelation] WHERE [ItemListRelationItemListId] = {0} " +
            "ORDER BY [ItemListRelationSort] ASC, [ItemListRelationId] ASC", listId));
        while (reader.Read())
        {
            var id = Converter.ToString(reader.GetValue(0));
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids;
    }

    public int CreateList(string memberItemType)
    {
        var list = new ItemList(memberItemType);
        list.Save();
        if (list.ID <= 0)
            throw new InvalidOperationException($"Could not create an item list of item type '{memberItemType}'.");
        return list.ID;
    }

    public void SetListId(string ownerItemType, string ownerItemId, string field, int listId)
    {
        using var repository = ItemManager.Storage.Open(ownerItemType);
        var item = repository.SelectById(ownerItemId)
            ?? throw new InvalidOperationException(
                $"Could not load item {ownerItemType}/{ownerItemId} to point its field '{field}' at item list {listId}.");
        item.DeserializeFrom(new Dictionary<string, object?> { [field] = listId });
        using var context = new ItemContext();
        item.Save(context);
    }

    public bool ItemExists(string itemType, string itemId)
    {
        using var repository = ItemManager.Storage.Open(itemType);
        return repository.SelectById(itemId) is not null;
    }

    public void CreateItem(string itemType, string itemId)
    {
        // An item saved with an id the table does not have is inserted under that id.
        var item = new Item(itemType) { Id = itemId };
        using var context = new ItemContext { SynchronizePages = false };
        item.Save(context);
        if (!ItemExists(itemType, itemId))
            throw new InvalidOperationException($"Could not create item {itemType}/{itemId}.");
    }

    public IReadOnlyDictionary<string, object?>? GetItemFields(string itemType, string itemId)
    {
        // Read as the serialize side reads an entry (ReadList), so the dry-run diff compares
        // like with like.
        using var repository = ItemManager.Storage.Open(itemType);
        var item = repository.SelectById(itemId);
        if (item is null)
            return null;
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in item.Names)
            fields[name] = item[name];
        return fields;
    }

    public void SaveMembers(int listId, IReadOnlyList<string> orderedItemIds)
    {
        ItemList.ClearCache(listId);
        var list = ItemList.GetItemListById(listId)
            ?? throw new InvalidOperationException($"Item list {listId} does not exist.");
        // Rewrites the relations in this order and sets each member's Sort; it deletes no item.
        list.SaveRelations(orderedItemIds);
    }

    public void SyncMemberSort(int listId, string memberItemType)
    {
        if (listId <= 0) return;
        // An item type's rows live in the table ItemType_<system name>.
        var table = ItemListTravelPolicy.ItemTypeTablePrefix + memberItemType;
        // The same statement ItemList.SaveRelations ends with, limited to rows that drifted.
        Database.ExecuteNonQuery(CommandBuilder.Create(
            "UPDATE it SET it.[Sort] = ilr.[ItemListRelationSort] FROM [" + table.Replace("]", "]]") + "] it " +
            "JOIN [ItemListRelation] ilr ON it.[Id] = ilr.[ItemListRelationItemId] " +
            "WHERE ilr.[ItemListRelationItemListId] = {0} AND it.[Sort] <> ilr.[ItemListRelationSort]", listId));
    }

    public bool GetChangedOnLanguage(int listId)
    {
        if (listId <= 0) return false;
        var value = Database.ExecuteScalar(CommandBuilder.Create(
            "SELECT [ItemListIsChangedOnLanguage] FROM [ItemList] WHERE [ItemListId] = {0}", listId));
        return value is not null && value != DBNull.Value && Converter.ToBoolean(value);
    }

    public void SetChangedOnLanguage(int listId, bool changed)
    {
        ItemList.ClearCache(listId);
        var list = ItemList.GetItemListById(listId)
            ?? throw new InvalidOperationException($"Item list {listId} does not exist.");
        list.IsChangedOnLanguage = changed;
        list.Save();
    }

    public ItemListSnapshot? ReadList(int listId)
    {
        var itemType = GetListItemType(listId);
        if (string.IsNullOrEmpty(itemType))
            return null;

        var memberIds = GetMemberIds(listId);
        var members = new List<ItemListMember>(memberIds.Count);
        var dangling = new List<string>();

        if (ItemManager.Metadata.GetItemType(itemType) is null)
        {
            // The list's item type is gone from the host: no entry can be read.
            dangling.AddRange(memberIds);
            return new ItemListSnapshot(itemType, GetChangedOnLanguage(listId), members, dangling);
        }

        using var repository = ItemManager.Storage.Open(itemType);
        foreach (var memberId in memberIds)
        {
            var item = repository.SelectById(memberId);
            if (item is null)
            {
                dangling.Add(memberId);
                continue;
            }

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var name in item.Names)
                fields[name] = item[name];
            members.Add(new ItemListMember(item.Id, fields));
        }

        return new ItemListSnapshot(itemType, GetChangedOnLanguage(listId), members, dangling);
    }
}
