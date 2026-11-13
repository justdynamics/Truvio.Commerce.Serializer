namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: the entry map, the Serializer's own table on a host. One row per list
/// entry this engine created there, keyed by the document entry's key
/// (<see cref="ItemListIdentity.EntryKey"/>: owner GUID, field and the entry's id in the
/// document), with the host id the entry got from the host's counter and the source id.
/// </summary>
/// <remarks>
/// <para>Why a table of its own: an entry row (<c>ItemType_&lt;type&gt;</c>) has the key
/// <c>Id</c>, the platform's <c>Sort</c> and <c>ItemInstanceType</c>, and otherwise only the item
/// type's own fields; <c>ItemList</c> (<c>ItemListId</c>, <c>ItemListItemType</c>,
/// <c>ItemListIsChangedOnLanguage</c>) and <c>ItemListRelation</c> (<c>ItemListRelationId</c>,
/// <c>ItemListRelationItemListId</c>, <c>ItemListRelationItemId</c>,
/// <c>ItemListRelationSort</c>) have no free column either. Pages and paragraphs carry a GUID
/// column of the platform's; entries do not.</para>
/// <para>Dynamicweb never reads the table. It is created the first time an entry is recorded
/// (reads treat a missing table as an empty map), and it travels with a backup or bacpac of the
/// database, so a copied host keeps matching what was delivered to it. A row whose entry is gone
/// matches nothing: the planner only accepts a mapped id that is a member of the owner's list.</para>
/// </remarks>
public static class ItemListEntryMap
{
    /// <summary>The table's name.</summary>
    public const string Table = "TruvioSerializerItemListEntry";

    /// <summary>1 when the table exists, else 0.</summary>
    public const string ExistsSql =
        "SELECT CASE WHEN OBJECT_ID(N'[dbo].[" + Table + "]', N'U') IS NULL THEN 0 ELSE 1 END";

    /// <summary>Creates the table when it does not exist; safe to run again.</summary>
    public const string CreateSql =
        "IF OBJECT_ID(N'[dbo].[" + Table + "]', N'U') IS NULL BEGIN " +
        "CREATE TABLE [dbo].[" + Table + "] (" +
        "[EntryKey] nvarchar(64) NOT NULL CONSTRAINT [PK_" + Table + "] PRIMARY KEY, " +
        "[ItemType] nvarchar(255) NOT NULL, " +
        "[ItemId] nvarchar(255) NOT NULL, " +
        "[OwnerKey] nvarchar(450) NOT NULL, " +
        "[Field] nvarchar(255) NOT NULL, " +
        "[SourceId] nvarchar(255) NOT NULL, " +
        "[RecordedUtc] datetime2 NOT NULL CONSTRAINT [DF_" + Table + "_RecordedUtc] DEFAULT SYSUTCDATETIME()); " +
        "CREATE INDEX [IX_" + Table + "_Item] ON [dbo].[" + Table + "] ([ItemType], [ItemId]); END";

    /// <summary>Records a key, or moves it to another entry: {0} key, {1} item type, {2} item id, {3} owner key, {4} field, {5} source id.</summary>
    public const string UpsertSql =
        "UPDATE [" + Table + "] SET [ItemType] = {1}, [ItemId] = {2}, [OwnerKey] = {3}, [Field] = {4}, [SourceId] = {5}, " +
        "[RecordedUtc] = SYSUTCDATETIME() WHERE [EntryKey] = {0}; " +
        "IF @@ROWCOUNT = 0 INSERT INTO [" + Table + "] ([EntryKey], [ItemType], [ItemId], [OwnerKey], [Field], [SourceId]) " +
        "VALUES ({0}, {1}, {2}, {3}, {4}, {5})";
}
