using Truvio.Commerce.Serializer.Infrastructure;

namespace Truvio.Commerce.Serializer.Providers;

/// <summary>
/// Result of a provider serialization operation (DB to disk).
/// </summary>
public record SerializeResult
{
    public int RowsSerialized { get; init; }
    public string TableName { get; init; } = "";
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Absolute paths of every file this predicate emitted during the run.
    /// Aggregated by <see cref="SerializerOrchestrator.SerializeAll(System.Collections.Generic.List{Models.ProviderPredicateDefinition}, string, Configuration.SerializerMode, Configuration.ConflictStrategy, System.Action{string}?, string?)"/>
    /// and handed to <see cref="Truvio.Commerce.Serializer.Infrastructure.ManifestWriter"/> + the
    /// <see cref="Truvio.Commerce.Serializer.Infrastructure.ManifestCleaner"/> post-run (Phase 37-01 Task 2).
    /// </summary>
    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Phase 42-03 / PROVIDER-04: the manifest entry produced by this serialize call. Null on
    /// validation failure / exception (the per-provider Serialize body returns early before
    /// BuildManifestEntry runs in those cases). The orchestrator collects non-null entries
    /// across all providers and hands them to <see cref="Infrastructure.ManifestWriter"/>.
    /// </summary>
    public ManifestEntry? Entry { get; init; }

    /// <summary>
    /// Content ownership R7: documents this predicate wrote with <c>itemLists</c>. Above 0 in any
    /// result of a mode, the mode manifest declares its engine floor
    /// (<see cref="Infrastructure.Manifest.MinEngineVersion"/>).
    /// </summary>
    public int DocumentsWithItemLists { get; init; }

    public bool HasErrors => Errors.Count > 0;

    public string Summary =>
        $"{TableName}: {RowsSerialized} rows serialized.";
}
