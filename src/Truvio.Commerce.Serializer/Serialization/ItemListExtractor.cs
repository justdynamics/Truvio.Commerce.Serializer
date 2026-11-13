using System.Globalization;
using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>One entry of a list as the source has it: the member item's id and all of its fields.</summary>
public sealed record ItemListMember(string Id, IReadOnlyDictionary<string, object?> Fields);

/// <summary>A list as the source has it.</summary>
/// <param name="ItemType">The list's own item type (<c>ItemList.ItemListItemType</c>).</param>
/// <param name="ChangedOnLanguage"><c>ItemList.ItemListIsChangedOnLanguage</c>.</param>
/// <param name="Members">The entries whose item exists, in list order.</param>
/// <param name="DanglingIds">Relation ids whose item no longer exists.</param>
public sealed record ItemListSnapshot(
    string ItemType,
    bool ChangedOnLanguage,
    IReadOnlyList<ItemListMember> Members,
    IReadOnlyList<string> DanglingIds);

/// <summary>
/// Content ownership R7: what the serialize side needs from the source. The production
/// implementation is <see cref="DwItemListHost"/>.
/// </summary>
public interface IItemListSource
{
    /// <summary>The item list fields of an item type; null when its metadata cannot be read.</summary>
    IReadOnlyDictionary<string, ItemListFieldInfo>? GetListFields(string itemType);

    /// <summary>The list with this id; null when there is none.</summary>
    ItemListSnapshot? ReadList(int listId);

    /// <summary>
    /// The entry map's rows for these entries of an item type, by item id: the entries this engine
    /// created on this host for a document entry. Entries without a row are absent.
    /// </summary>
    IReadOnlyDictionary<string, ItemListEntryMapping> GetEntryMappings(string itemType, IReadOnlyCollection<string> itemIds);
}

/// <summary>The item lists of one document's item, ready for the document.</summary>
public sealed record ItemListExtraction(
    Dictionary<string, SerializedItemList> Lists,
    Dictionary<string, SerializedItemListOptions>? Options);

/// <summary>
/// Content ownership R7: moves the item lists of a paragraph's or page's item out of its
/// <c>fields</c> (where the value is a host list id) into the document's <c>itemLists</c>, as
/// ordered entries with their own fields and, recursively, their own lists.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every inline item list field of the item type is named in <c>itemLists</c>, with an
/// empty list when the item has none: the document owns the field either way, and an empty
/// list is how a Replace empties it on the host.</item>
/// <item>A field the predicate excludes (<c>excludeFields</c>, <c>excludeFieldsByItemType</c>)
/// is left out, as it is left out of <c>fields</c>; the same exclusions apply to the entries'
/// item type.</item>
/// <item>A list of pages or paragraphs (item source other than inline) is not carried: its
/// entries are other documents' items. The field stays in <c>fields</c> as before and one line
/// says so.</item>
/// <item>An entry's system fields (<c>Id</c>, <c>Sort</c>, <c>ItemInstanceType</c>) are not
/// fields: the id is the entry's <c>id</c> and the order is the sequence.</item>
/// <item>An entry this engine created on this host for a document entry (a row in the entry map
/// for the same owner and field) is written under the document's id it was created for, not
/// under this host's number: that id is the one every host agrees on, so the document matches
/// the entry wherever it is delivered, the host it came from included. When that id is also
/// the raw id of another entry of the same list, the entry keeps its host id.</item>
/// <item>A list id that does not exist, a list of another item type than the field declares, a
/// relation whose item is gone and a list two owners share are each a <c>WARNING</c> (strict
/// mode escalates it), never a silent drop.</item>
/// </list>
/// </remarks>
public sealed class ItemListExtractor
{
    /// <summary>Fields of a list entry's item that are not content.</summary>
    public static readonly IReadOnlySet<string> EntrySystemFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Id", "Sort", "ItemInstanceType" };

    private readonly IItemListSource _source;
    private readonly Action<string>? _log;
    private readonly Dictionary<int, string> _listOwners = new();
    private readonly HashSet<string> _memberItemTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notedUncarried = new(StringComparer.OrdinalIgnoreCase);

    public ItemListExtractor(IItemListSource source, Action<string>? log = null)
    {
        _source = source;
        _log = log;
    }

    /// <summary>The item types of every entry extracted so far (the item-type XML a target needs).</summary>
    public IReadOnlySet<string> MemberItemTypes => _memberItemTypes;

    /// <summary>
    /// Extracts the item lists of one item. <paramref name="fields"/> is the item's extracted
    /// field map; the list fields carried in the result are removed from it. Null when the item
    /// type has no inline item list field (or its metadata cannot be read): the document is then
    /// written exactly as before.
    /// </summary>
    /// <param name="itemType">The owner item's type.</param>
    /// <param name="fields">The owner item's extracted fields; carried list fields are removed.</param>
    /// <param name="excludedFields">The owner's effective field exclusions.</param>
    /// <param name="ownerLabel">How log lines name the owner.</param>
    /// <param name="excludedFieldsFor">The effective field exclusions of an entry item type.</param>
    /// <param name="ownerKey">
    /// The owner's cross-host key (<see cref="ItemListIdentity.ParagraphOwner"/>,
    /// <see cref="ItemListIdentity.PageOwner"/>); with it, entries this engine created on this host
    /// are written under the document id they were created for.
    /// </param>
    public ItemListExtraction? Extract(
        string? itemType,
        Dictionary<string, object> fields,
        IReadOnlySet<string>? excludedFields,
        string ownerLabel,
        Func<string, IReadOnlySet<string>?>? excludedFieldsFor = null,
        string? ownerKey = null)
    {
        if (string.IsNullOrEmpty(itemType))
            return null;
        var extraction = ExtractLists(itemType,
            key => fields.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value,
            excludedFields, ownerLabel, excludedFieldsFor ?? (_ => null), depth: 1, ownerKey);
        if (extraction is null)
            return null;
        foreach (var field in extraction.Lists.Keys)
            RemoveIgnoreCase(fields, field);
        return extraction;
    }

    /// <summary>
    /// For an item whose lists this version does not carry (a grid row's, an area's, a page
    /// property item's): says once per item type and field that the list keeps travelling as a
    /// raw list id.
    /// </summary>
    public void NoteUncarried(string? itemType, IEnumerable<KeyValuePair<string, object>> fields, string ownerLabel)
    {
        if (string.IsNullOrEmpty(itemType))
            return;
        var listFields = _source.GetListFields(itemType);
        if (listFields is null || listFields.Count == 0)
            return;
        foreach (var kv in fields)
        {
            if (!listFields.ContainsKey(kv.Key) || ToListId(kv.Value) <= 0)
                continue;
            if (_notedUncarried.Add($"{itemType}|{kv.Key}"))
                _log?.Invoke($"Item list not carried: field '{kv.Key}' of item type '{itemType}' ({ownerLabel}) is an item list, " +
                             "but item lists travel inside paragraph and page documents only. The field keeps its raw list id.");
        }
    }

    private ItemListExtraction? ExtractLists(
        string itemType,
        Func<string, object?> valueOf,
        IReadOnlySet<string>? excludedFields,
        string ownerLabel,
        Func<string, IReadOnlySet<string>?> excludedFieldsFor,
        int depth,
        string? ownerKey)
    {
        var listFields = _source.GetListFields(itemType);
        if (listFields is null || listFields.Count == 0)
            return null;

        var lists = new Dictionary<string, SerializedItemList>(StringComparer.Ordinal);
        Dictionary<string, SerializedItemListOptions>? options = null;

        foreach (var (field, info) in listFields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (excludedFields?.Contains(field) == true)
                continue;

            if (!info.IsInline)
            {
                if (_notedUncarried.Add($"{itemType}|{field}"))
                    _log?.Invoke($"Item list not carried: field '{field}' of item type '{itemType}' lists {info.ItemSource} items, " +
                                 "not entries of its own. The field keeps its raw list id.");
                continue;
            }

            var listId = ToListId(valueOf(field));
            var entries = new List<SerializedItemListEntry>();
            if (listId > 0)
            {
                var snapshot = _source.ReadList(listId);
                if (snapshot is null)
                {
                    _log?.Invoke($"WARNING: {ownerLabel}: field '{field}' points at item list {listId}, which does not exist. " +
                                 "The list is written empty.");
                }
                else if (!string.Equals(snapshot.ItemType, info.MemberItemType, StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke($"WARNING: {ownerLabel}: field '{field}' points at item list {listId} of item type '{snapshot.ItemType}', " +
                                 $"but the field declares '{info.MemberItemType}'. The list is written empty.");
                }
                else
                {
                    // The same document can be mapped more than once in a run (a page that is a
                    // structural stub for one predicate and content for another): only another
                    // owner makes the list shared.
                    var thisOwner = $"{ownerLabel} (field '{field}')";
                    if (!_listOwners.TryGetValue(listId, out var otherOwner))
                        _listOwners[listId] = thisOwner;
                    else if (!string.Equals(otherOwner, thisOwner, StringComparison.Ordinal))
                        _log?.Invoke($"WARNING: {ownerLabel}: field '{field}' points at item list {listId}, which {otherOwner} also uses. " +
                                     "A list has one owner; each document carries its own copy of the entries.");

                    foreach (var danglingId in snapshot.DanglingIds)
                        _log?.Invoke($"WARNING: {ownerLabel}: item list '{field}' (list {listId}) relates to item '{danglingId}' of type " +
                                     $"'{snapshot.ItemType}', which does not exist. The entry is not written.");

                    _memberItemTypes.Add(info.MemberItemType);
                    var documentIds = DocumentIds(info.MemberItemType, snapshot.Members, ownerKey, field, ownerLabel);
                    foreach (var member in snapshot.Members)
                    {
                        var documentId = documentIds[member.Id];
                        entries.Add(ExtractEntry(info.MemberItemType, member, documentId, excludedFieldsFor,
                            $"{ownerLabel}, item list '{field}' entry '{documentId}'", depth,
                            ownerKey is null ? null : ItemListIdentity.EntryOwner(ownerKey, field, documentId)));
                    }

                    if (snapshot.ChangedOnLanguage)
                        (options ??= new Dictionary<string, SerializedItemListOptions>(StringComparer.Ordinal))[field] =
                            new SerializedItemListOptions { ChangedOnLanguage = true };
                }
            }

            lists[field] = new SerializedItemList(entries);
        }

        return lists.Count == 0 ? null : new ItemListExtraction(lists, options);
    }

    /// <summary>
    /// The id each member is written under: the document id the entry map recorded for it when
    /// this engine created it for the same owner and field, else its id on this host.
    /// </summary>
    private Dictionary<string, string> DocumentIds(
        string memberItemType,
        IReadOnlyList<ItemListMember> members,
        string? ownerKey,
        string field,
        string ownerLabel)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
            ids[member.Id] = member.Id;
        if (ownerKey is null || members.Count == 0)
            return ids;

        var mappings = _source.GetEntryMappings(memberItemType, members.Select(m => m.Id).ToList());
        if (mappings.Count == 0)
            return ids;

        var proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            if (mappings.TryGetValue(member.Id, out var row)
                && string.Equals(row.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.Field, field, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(row.SourceId))
                proposed[member.Id] = row.SourceId;
        }

        // A document id must name one entry of the list: a source id that another member holds
        // as its raw id, or that two members were created for, is not used.
        var taken = members.Where(m => !proposed.ContainsKey(m.Id)).Select(m => m.Id).ToList();
        foreach (var (memberId, sourceId) in proposed)
        {
            var clashes = taken.Any(t => ItemListIdentity.SameId(t, sourceId))
                          || proposed.Count(p => ItemListIdentity.SameId(p.Value, sourceId)) > 1;
            if (clashes)
            {
                _log?.Invoke($"{ownerLabel}: item list '{field}' entry '{memberId}' was created for the document entry " +
                             $"'{sourceId}', but another entry of the list also answers to that id; it is written under '{memberId}'.");
                continue;
            }
            ids[memberId] = sourceId;
        }
        return ids;
    }

    private SerializedItemListEntry ExtractEntry(
        string memberItemType,
        ItemListMember member,
        string documentId,
        Func<string, IReadOnlySet<string>?> excludedFieldsFor,
        string entryLabel,
        int depth,
        string? entryOwnerKey)
    {
        var memberExclusions = excludedFieldsFor(memberItemType);
        ItemListExtraction? nested = null;
        if (depth < ItemListWriter.MaxDepth)
        {
            nested = ExtractLists(memberItemType,
                key => member.Fields.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value,
                memberExclusions, entryLabel, excludedFieldsFor, depth + 1, entryOwnerKey);
        }
        else if (_source.GetListFields(memberItemType) is { Count: > 0 })
        {
            _log?.Invoke($"WARNING: {entryLabel}: item lists are nested more than {ItemListWriter.MaxDepth} levels deep; " +
                         "the lists below this entry are not written.");
        }

        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in member.Fields)
        {
            if (value is null || EntrySystemFields.Contains(name))
                continue;
            if (memberExclusions?.Contains(name) == true)
                continue;
            if (nested is not null && nested.Lists.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            fields[name] = value;
        }

        return new SerializedItemListEntry
        {
            Id = documentId,
            Fields = fields,
            ItemLists = nested?.Lists
        };
    }

    private static int ToListId(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => l is > 0 and <= int.MaxValue ? (int)l : 0,
        _ => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
    };

    private static void RemoveIgnoreCase(Dictionary<string, object> fields, string field)
    {
        foreach (var key in fields.Keys.Where(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)).ToList())
            fields.Remove(key);
    }
}
