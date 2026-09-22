using System.Text.Json.Serialization;
using Truvio.Commerce.Serializer.Configuration;

namespace Truvio.Commerce.Serializer.Models;

/// <summary>
/// Extended predicate definition for provider-based routing.
/// Includes fields for all provider types (Content, SqlTable, etc.).
/// </summary>
public record ProviderPredicateDefinition
{
    /// <summary>Human-readable predicate name.</summary>
    public required string Name { get; init; }

    /// <summary>Provider type to route to (e.g., "Content", "SqlTable").</summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// Which SerializerMode this predicate runs under. Each predicate declares its own mode and
    /// the orchestrator filters `config.Predicates` by `p.Mode == SerializerMode.Replace`
    /// (or Merge) when iterating. JSON key is "mode" (camelCase convention); on-disk values are
    /// "Replace" / "Merge" (read case-insensitively).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SerializerMode Mode { get; init; } = SerializerMode.Replace;

    /// <summary>SQL table name for SqlTable predicates (e.g., "EcomOrderFlow").</summary>
    public string? Table { get; init; }

    /// <summary>Column used as natural key for row identity (e.g., "OrderFlowName"). Empty = use composite PK.</summary>
    public string? NameColumn { get; init; }

    /// <summary>Comma-separated columns used for change detection. Empty = use all non-identity columns.</summary>
    public string? CompareColumns { get; init; }

    /// <summary>Area ID for Content predicates.</summary>
    public int AreaId { get; init; } = 0;

    /// <summary>
    /// Content predicates only: when true and <see cref="AreaId"/> is a master area, the
    /// serialize run expands this predicate into one synthetic predicate per language-layer
    /// area (Area.MasterAreaId == AreaId) so language versions of the selected subtree are
    /// serialized alongside the master. Language pages are matched against this predicate's
    /// <see cref="Path"/> via their master-page chain (language MenuTexts are translated, so
    /// the master's path space is the stable predicate coordinate system).
    /// </summary>
    public bool IncludeLanguageLayers { get; init; } = false;

    /// <summary>Root path for Content predicates.</summary>
    public string Path { get; init; } = "";

    /// <summary>Page ID for Content predicates.</summary>
    public int PageId { get; init; } = 0;

    /// <summary>Paths or patterns to exclude.</summary>
    public List<string> Excludes { get; init; } = new();

    /// <summary>
    /// Fully-qualified DW service cache type names to clear after deserialization.
    /// Sourced from DataGroup XML ServiceCaches sections.
    /// </summary>
    public List<string> ServiceCaches { get; init; } = new();

    /// <summary>
    /// Optional schema sync configuration. When set, the orchestrator runs
    /// post-deserialize schema sync to ensure custom columns exist on the target table.
    /// Format: "EcomGroupFields" (only supported value currently).
    /// </summary>
    public string? SchemaSync { get; init; }

    /// <summary>Column names containing embedded XML content for SqlTable predicates.</summary>
    public List<string> XmlColumns { get; init; } = new();

    /// <summary>
    /// Optional explicit match key for a SqlTable predicate whose table has no PRIMARY KEY
    /// (a heap). Second step of <see cref="Providers.SqlTable.KeyResolution"/>: it beats
    /// unique-index inference and the all-columns fallback, and it is ignored on a table that
    /// declares a PRIMARY KEY. Example: <c>DynamicStructures</c> with
    /// <c>KeyColumns = ["DynamicStructureUniqueId"]</c>.
    /// </summary>
    public List<string> KeyColumns { get; init; } = new();

    /// <summary>
    /// Optional whole-table replacement opt-in for a SqlTable predicate: <c>"truncate"</c>
    /// deletes every target row before the payload is written, under Replace only. Absent
    /// (the default) means Replace upserts on the resolved key and leaves target rows absent
    /// from the payload alone. Under Merge the field is ignored with a WARNING.
    /// </summary>
    public string? ReplaceStrategy { get; init; }

    /// <summary>Field names to exclude from serialization output.</summary>
    public List<string> ExcludeFields { get; init; } = new();

    /// <summary>XML element names to exclude from embedded XML content during serialization.</summary>
    public List<string> ExcludeXmlElements { get; init; } = new();

    /// <summary>Area SQL table column names to exclude from serialization. Content predicates only.</summary>
    public List<string> ExcludeAreaColumns { get; init; } = new();

    /// <summary>
    /// Optional WHERE clause for SqlTable predicates, applied at serialize time to filter rows
    /// (FILTER-01 / Phase 37-03). Validated at config-load via SqlWhereClauseValidator — every
    /// identifier must match INFORMATION_SCHEMA.COLUMNS of <see cref="Table"/>; banned tokens
    /// (<c>;</c>, <c>--</c>, <c>/*</c>, <c>EXEC</c>, <c>xp_</c>, etc.) are rejected.
    /// Example: <c>AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')</c>.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Optional per-predicate column opt-in (Phase 37-03, RUNTIME-COLS-01): columns listed here
    /// are KEPT in serialization output even if they would otherwise be auto-excluded by
    /// <see cref="Configuration.RuntimeExcludes"/>. Case-insensitive.
    /// </summary>
    public List<string> IncludeFields { get; init; } = new();

    /// <summary>
    /// Optional per-SqlTable-predicate (Phase 37-05 / LINK-02 pass 2, D-22): column names whose
    /// string values get <see cref="Serialization.InternalLinkResolver.ResolveInStringColumn"/>
    /// applied at deserialize. Default.aspx?ID=N references in these columns are rewritten
    /// source→target page ID using the cross-environment map built from Content-provider runs.
    /// Example: <c>UrlPath</c> predicate with <c>ResolveLinksInColumns = ["UrlPathRedirect"]</c>
    /// lets the <c>UrlPathRedirect</c> field survive cross-environment syncs. Empty = no link
    /// resolution for this table's columns.
    /// </summary>
    public List<string> ResolveLinksInColumns { get; init; } = new();

    /// <summary>
    /// Per-predicate Baseline link-sweep bypass (2026-04-20 follow-up to Phase 37-05 LINK-02).
    /// Page IDs whose unresolvable references should be logged as warnings rather than raised
    /// as fatal errors by the serialize-time <see cref="Infrastructure.BaselineLinkSweeper"/>.
    /// Content predicates only. Use for known-broken source data that cannot be cleaned upstream
    /// in time; any unresolvable NOT in this list still fails serialize.
    /// </summary>
    public List<int> AcknowledgedOrphanPageIds { get; init; } = new();

    /// <summary>
    /// Set on the effective predicate of an inline API scope (see
    /// <see cref="Configuration.InlineScopeResolver"/>); never read from or written to the config.
    /// A scoped serialize writes into a mode directory that already holds the fence predicate's
    /// tree, so it reuses existing page folders, never overwrites a full page with an ancestor
    /// stub, and merges the template manifest instead of replacing it.
    /// </summary>
    [JsonIgnore]
    public bool IsInlineScope { get; init; }
}
