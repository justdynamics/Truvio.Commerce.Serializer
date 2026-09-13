using System.Text.Json;

namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// Predicate-shaped scope passed inline on the <c>Serialize</c> / <c>Deserialize</c> API commands,
/// so a caller drives one subtree or table per call instead of the whole configuration. Field
/// names match the config predicate keys. The configured predicates stay the saved defaults and
/// the safety fence: <see cref="InlineScopeResolver"/> accepts a scope only when it falls inside a
/// configured predicate of the same mode, and fills every omitted field from that predicate.
/// </summary>
public sealed class InlineScope
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Optional. When it names a configured predicate, that predicate is the fence.</summary>
    public string? Name { get; set; }

    /// <summary>"Content" or "SqlTable". Omitted: SqlTable when <see cref="Table"/> is set, else Content.</summary>
    public string? ProviderType { get; set; }

    // Content
    public int AreaId { get; set; }
    public string? Path { get; set; }
    /// <summary>Alternative to <see cref="Path"/>: the scope root page. Its content path is resolved on the host.</summary>
    public int PageId { get; set; }
    public List<string>? Excludes { get; set; }
    public bool? IncludeLanguageLayers { get; set; }
    public List<string>? ExcludeAreaColumns { get; set; }

    // SqlTable
    public string? Table { get; set; }
    public string? Where { get; set; }
    public List<string>? IncludeFields { get; set; }

    // Both providers
    public List<string>? ExcludeFields { get; set; }
    public List<string>? ExcludeXmlElements { get; set; }

    // Owned by the configured predicate: omit, or pass the configured value.
    public string? NameColumn { get; set; }
    public string? CompareColumns { get; set; }
    public List<string>? XmlColumns { get; set; }
    public List<string>? ServiceCaches { get; set; }
    public string? SchemaSync { get; set; }
    public List<string>? ResolveLinksInColumns { get; set; }
    public List<int>? AcknowledgedOrphanPageIds { get; set; }

    /// <summary>Parses the JSON form used by the <c>?scope=</c> query-string fallback. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static InlineScope? Parse(string json) => JsonSerializer.Deserialize<InlineScope>(json, _jsonOptions);

    /// <summary>One-line description for logs and API messages.</summary>
    public override string ToString() =>
        !string.IsNullOrWhiteSpace(Table)
            ? $"SqlTable {Table}{(string.IsNullOrWhiteSpace(Where) ? "" : $" where {Where}")}"
            : $"Content area {AreaId} {(string.IsNullOrWhiteSpace(Path) ? $"page {PageId}" : Path)}";
}
