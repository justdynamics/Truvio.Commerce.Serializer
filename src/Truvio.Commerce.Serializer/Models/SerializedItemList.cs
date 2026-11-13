using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Truvio.Commerce.Serializer.Models;

/// <summary>
/// Content ownership R7: one item list, carried inside the document of the paragraph or page
/// whose item field points at it. In YAML it is a plain sequence of entries, in list order:
/// <code>
/// itemLists:
///   Tiers:
///   - id: "en-tier-1"
///     fields:
///       Title: "Bronze"
/// </code>
/// <para>The list is written and read by hand (<see cref="IYamlConvertible"/>) for two reasons.
/// An empty list has to survive the page writer, which omits empty collections: an empty
/// sequence under the field name is how a Replace empties the list on the host. And an entry is
/// read strictly: a member other than <c>id</c>, <c>fields</c> and <c>itemLists</c>, an entry
/// that is not a mapping, or an entry without an <c>id</c> fails the read instead of being
/// dropped, where every other document ignores members it does not know.</para>
/// </summary>
public sealed class SerializedItemList : IYamlConvertible
{
    public const string IdKey = "id";
    public const string FieldsKey = "fields";
    public const string ItemListsKey = "itemLists";

    /// <summary>The entries, in list order.</summary>
    public List<SerializedItemListEntry> Entries { get; private set; } = new();

    public SerializedItemList()
    {
    }

    public SerializedItemList(IEnumerable<SerializedItemListEntry> entries)
    {
        Entries = entries.ToList();
    }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        if (!parser.TryConsume<SequenceStart>(out _))
        {
            var found = parser.Current;
            throw new YamlException(found?.Start ?? Mark.Empty, found?.End ?? Mark.Empty,
                "An item list must be a sequence of entries (write [] for an empty list).");
        }

        var entries = new List<SerializedItemListEntry>();
        while (!parser.TryConsume<SequenceEnd>(out _))
            entries.Add(ReadEntry(parser, nestedObjectDeserializer));
        Entries = entries;
    }

    private static SerializedItemListEntry ReadEntry(IParser parser, ObjectDeserializer nestedObjectDeserializer)
    {
        if (!parser.TryConsume<MappingStart>(out var start))
        {
            var found = parser.Current;
            throw new YamlException(found?.Start ?? Mark.Empty, found?.End ?? Mark.Empty,
                "An item list entry must be a mapping with an 'id'.");
        }

        string? id = null;
        Dictionary<string, object>? fields = null;
        Dictionary<string, SerializedItemList>? itemLists = null;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            switch (key.Value)
            {
                case IdKey:
                    id = parser.Consume<Scalar>().Value;
                    break;
                case FieldsKey:
                    fields = nestedObjectDeserializer(typeof(Dictionary<string, object>)) as Dictionary<string, object>;
                    break;
                case ItemListsKey:
                    itemLists = nestedObjectDeserializer(typeof(Dictionary<string, SerializedItemList>))
                        as Dictionary<string, SerializedItemList>;
                    break;
                default:
                    throw new YamlException(key.Start, key.End,
                        $"Unknown member '{key.Value}' in an item list entry; an entry takes " +
                        $"'{IdKey}', '{FieldsKey}' and '{ItemListsKey}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(id))
            throw new YamlException(start.Start, start.End,
                $"An item list entry has no '{IdKey}'; every entry needs one so it can be matched on the host.");

        return new SerializedItemListEntry
        {
            Id = id,
            Fields = fields ?? new Dictionary<string, object>(),
            ItemLists = itemLists
        };
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true,
            Entries.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));

        foreach (var entry in Entries)
        {
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));

            // Keys and the id go through the document's own serializer, so they are written in
            // the same style as every other string of the document: the id is always quoted
            // text ("12" stays text).
            nestedObjectSerializer(IdKey, typeof(string));
            nestedObjectSerializer(entry.Id, typeof(string));

            if (entry.Fields.Count > 0)
            {
                nestedObjectSerializer(FieldsKey, typeof(string));
                nestedObjectSerializer(
                    new Dictionary<string, object>(entry.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal)),
                    typeof(Dictionary<string, object>));
            }

            if (entry.ItemLists is { Count: > 0 })
            {
                nestedObjectSerializer(ItemListsKey, typeof(string));
                nestedObjectSerializer(
                    new Dictionary<string, SerializedItemList>(entry.ItemLists.OrderBy(kv => kv.Key, StringComparer.Ordinal)),
                    typeof(Dictionary<string, SerializedItemList>));
            }

            emitter.Emit(new MappingEnd());
        }

        emitter.Emit(new SequenceEnd());
    }

}

/// <summary>
/// Content ownership R7: one entry of an item list, which is one item of the list's item type.
/// </summary>
public sealed record SerializedItemListEntry
{
    /// <summary>
    /// The member item's id on the source. It is the entry's identity: a delivery matches it
    /// against the members of the same list on the host (see <c>ItemListIdentity</c>).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The member item's own fields, without its system fields and its item lists.</summary>
    public Dictionary<string, object> Fields { get; init; } = new();

    /// <summary>Item lists the member item itself carries (a list inside a list entry), by field.</summary>
    public Dictionary<string, SerializedItemList>? ItemLists { get; init; }
}

/// <summary>
/// Content ownership R7: what an item list carries besides its entries. Written only when a
/// value differs from the default, under <c>itemListOptions</c> beside <c>itemLists</c>.
/// </summary>
public sealed record SerializedItemListOptions
{
    /// <summary>
    /// <c>ItemList.IsChangedOnLanguage</c>: on a language layer, the list differs from the
    /// master's, so Dynamicweb does not replace it when the master's list changes.
    /// </summary>
    public bool ChangedOnLanguage { get; init; }
}
