using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: decides at serialize whether item lists travel inside their documents,
/// and finds entries that would travel twice.
/// </summary>
/// <remarks>
/// Through 1.0.9-beta an item list travelled as SqlTable rows: an <c>ItemList</c> predicate, an
/// <c>ItemListRelation</c> predicate and one per entry table (<c>ItemType_&lt;type&gt;</c>).
/// Those rows carry the source's list ids, so a configuration that still ships them cannot also
/// carry the lists inside the documents: the two deliveries would write the same list twice
/// under different ids. The boundary is therefore the configuration: while a SqlTable predicate
/// on <c>ItemList</c> or <c>ItemListRelation</c> is present, the serializer writes documents
/// exactly as 1.0.9-beta did; once both are removed, lists move into the documents.
/// </remarks>
public static class ItemListTravelPolicy
{
    public const string ItemListTable = "ItemList";
    public const string ItemListRelationTable = "ItemListRelation";
    public const string ItemTypeTablePrefix = "ItemType_";

    /// <summary>
    /// The SqlTable predicates that still ship item lists as rows (<c>ItemList</c> or
    /// <c>ItemListRelation</c>), in any mode.
    /// </summary>
    public static IReadOnlyList<ProviderPredicateDefinition> ListRowPredicates(
        IEnumerable<ProviderPredicateDefinition> predicates) =>
        SqlTablePredicates(predicates)
            .Where(p => IsTable(p.Table, ItemListTable) || IsTable(p.Table, ItemListRelationTable))
            .ToList();

    /// <summary>True when lists travel inside their documents: no predicate ships them as rows.</summary>
    public static bool ListsTravelInDocuments(IEnumerable<ProviderPredicateDefinition> predicates) =>
        ListRowPredicates(predicates).Count == 0;

    /// <summary>
    /// The SqlTable predicates on the table of an item type whose entries the documents now
    /// carry: those entries would be delivered twice, once inside their document and once as
    /// rows under the source's ids.
    /// </summary>
    public static IReadOnlyList<ProviderPredicateDefinition> EntryRowPredicates(
        IEnumerable<ProviderPredicateDefinition> predicates,
        IEnumerable<string> memberItemTypes)
    {
        var tables = new HashSet<string>(
            memberItemTypes.Select(type => ItemTypeTablePrefix + type), StringComparer.OrdinalIgnoreCase);
        return SqlTablePredicates(predicates)
            .Where(p => !string.IsNullOrWhiteSpace(p.Table) && tables.Contains(Unbracket(p.Table!)))
            .ToList();
    }

    private static IEnumerable<ProviderPredicateDefinition> SqlTablePredicates(
        IEnumerable<ProviderPredicateDefinition> predicates) =>
        predicates.Where(p => string.Equals(p.ProviderType, "SqlTable", StringComparison.OrdinalIgnoreCase));

    private static bool IsTable(string? table, string name) =>
        !string.IsNullOrWhiteSpace(table) && string.Equals(Unbracket(table), name, StringComparison.OrdinalIgnoreCase);

    private static string Unbracket(string table)
    {
        var name = table.Trim();
        var lastDot = name.LastIndexOf("].[", StringComparison.Ordinal);
        if (lastDot >= 0) name = name[(lastDot + 2)..];
        else if (name.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        return name.Trim('[', ']');
    }
}
