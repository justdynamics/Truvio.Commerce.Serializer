using Dynamicweb.Content.Items;
using Dynamicweb.Content.Items.Activation;
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

    public string CreateItem(string itemType)
    {
        // A new number from the host's own counter (ItemTypeId), through the platform's id
        // generator, so the counter moves past it and Dynamicweb never hands it out again. A
        // counter that lags behind rows inserted without it (the SqlTable delivery of entry rows
        // before R7) is moved on until it names a free id.
        var id = IdGenerator.Next(itemType);
        var skipped = 0;
        while (ItemExists(itemType, id))
        {
            if (++skipped > MaxCounterSkips)
                throw new InvalidOperationException(
                    $"Could not create an item of type '{itemType}': the item type's id counter (ItemTypeId) is more than " +
                    $"{MaxCounterSkips} behind the ids its table holds.");
            id = IdGenerator.Next(itemType);
        }
        if (skipped > 0)
            _log?.Invoke($"Item type '{itemType}': its id counter (ItemTypeId) was {skipped} behind the ids the table holds; moved on to {id}.");

        // An item saved with an id the table does not have is inserted under that id.
        var item = new Item(itemType) { Id = id };
        using var context = new ItemContext { SynchronizePages = false };
        item.Save(context);
        if (!ItemExists(itemType, id))
            throw new InvalidOperationException($"Could not create item {itemType}/{id}.");
        return id;
    }

    public void RestoreItem(string itemType, string itemId)
    {
        var item = new Item(itemType) { Id = itemId };
        using var context = new ItemContext { SynchronizePages = false };
        item.Save(context);
        if (!ItemExists(itemType, itemId))
            throw new InvalidOperationException($"Could not put back item {itemType}/{itemId}.");
    }

    /// <summary>The most ids <see cref="CreateItem"/> moves a lagging counter on before it gives up.</summary>
    private const int MaxCounterSkips = 100_000;

    // -- the entry map --------------------------------------------------------------------------

    /// <summary>
    /// The Serializer's own table: one row per list entry this engine created on the host, keyed
    /// by the document entry's key (<see cref="ItemListIdentity.EntryKey"/>). An item row has no
    /// column the platform leaves alone (Id, Sort and ItemInstanceType are the platform's, every
    /// other column is a field of the item type) and the list and relation rows have none either,
    /// so the key lives here. Dynamicweb never reads it; it travels with a database copy.
    /// </summary>
    internal const string EntryMapTable = ItemListEntryMap.Table;

    private bool? _entryMapExists;

    private bool EntryMapExists()
    {
        if (_entryMapExists == true) return true;
        var value = Database.ExecuteScalar(CommandBuilder.Create(ItemListEntryMap.ExistsSql));
        _entryMapExists = Converter.ToInt32(value) == 1;
        return _entryMapExists.Value;
    }

    public IReadOnlyDictionary<string, string> FindMappedEntries(string itemType, IReadOnlyCollection<string> entryKeys)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entryKeys.Count == 0 || !EntryMapExists())
            return found;
        foreach (var chunk in entryKeys.Chunk(500))
        {
            var cb = new CommandBuilder();
            cb.Add("SELECT [EntryKey], [ItemId] FROM [" + EntryMapTable + "] WHERE [ItemType] = {0} AND [EntryKey] IN (", itemType);
            for (var i = 0; i < chunk.Length; i++)
                cb.Add(i == 0 ? "{0}" : ", {0}", chunk[i]);
            cb.Add(")");
            using var reader = Database.CreateDataReader(cb);
            while (reader.Read())
                found[Converter.ToString(reader.GetValue(0))] = Converter.ToString(reader.GetValue(1));
        }
        return found;
    }

    public void RecordEntry(ItemListEntryMapping mapping)
    {
        if (!EntryMapExists())
        {
            Database.ExecuteNonQuery(CommandBuilder.Create(ItemListEntryMap.CreateSql));
            _entryMapExists = true;
        }
        Database.ExecuteNonQuery(CommandBuilder.Create(ItemListEntryMap.UpsertSql,
            mapping.EntryKey, mapping.ItemType, mapping.ItemId, mapping.OwnerKey, mapping.Field, mapping.SourceId));
    }

    public IReadOnlyDictionary<string, ItemListEntryMapping> GetEntryMappings(string itemType, IReadOnlyCollection<string> itemIds)
    {
        var found = new Dictionary<string, ItemListEntryMapping>(StringComparer.OrdinalIgnoreCase);
        if (itemIds.Count == 0 || !EntryMapExists())
            return found;
        foreach (var chunk in itemIds.Chunk(500))
        {
            var cb = new CommandBuilder();
            cb.Add("SELECT [EntryKey], [ItemType], [ItemId], [OwnerKey], [Field], [SourceId] FROM [" + EntryMapTable + "] " +
                   "WHERE [ItemType] = {0} AND [ItemId] IN (", itemType);
            for (var i = 0; i < chunk.Length; i++)
                cb.Add(i == 0 ? "{0}" : ", {0}", chunk[i]);
            cb.Add(") ORDER BY [RecordedUtc] DESC");
            using var reader = Database.CreateDataReader(cb);
            while (reader.Read())
            {
                var row = new ItemListEntryMapping(
                    Converter.ToString(reader.GetValue(0)), Converter.ToString(reader.GetValue(1)),
                    Converter.ToString(reader.GetValue(2)), Converter.ToString(reader.GetValue(3)),
                    Converter.ToString(reader.GetValue(4)), Converter.ToString(reader.GetValue(5)));
                found.TryAdd(row.ItemId, row);
            }
        }
        return found;
    }

    // -- ItemInstanceType -----------------------------------------------------------------------

    private static string ItemTable(string itemType) =>
        "[" + (ItemListTravelPolicy.ItemTypeTablePrefix + itemType).Replace("]", "]]") + "]";

    public string? GetInstanceType(string itemType, string itemId)
    {
        var value = Database.ExecuteScalar(CommandBuilder.Create(
            "SELECT [ItemInstanceType] FROM " + ItemTable(itemType) + " WHERE [Id] = {0}", itemId));
        return value is null ? null : value == DBNull.Value ? string.Empty : Converter.ToString(value);
    }

    public void KeepInstanceType(string itemType, string itemId, string instanceType)
    {
        // Only a value the save emptied is put back; a value Dynamicweb set (a type with a base)
        // is left as it is.
        Database.ExecuteNonQuery(CommandBuilder.Create(
            "UPDATE " + ItemTable(itemType) + " SET [ItemInstanceType] = {0} WHERE [Id] = {1} AND ISNULL([ItemInstanceType], '') = ''",
            instanceType, itemId));
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
