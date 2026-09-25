namespace Truvio.Commerce.Serializer.Providers;

/// <summary>
/// Result of a provider deserialization operation (disk to DB).
/// Separate from Serialization.DeserializeResult which is content-specific.
/// </summary>
public record ProviderDeserializeResult
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }

    /// <summary>
    /// Rows the run deleted from the target. Non-zero only for the opted-in
    /// <c>replaceStrategy: truncate</c> path — every other path upserts, and Merge never
    /// deletes. A delivery gate asserts <c>deleted == 0</c> on a Merge run.
    /// </summary>
    public int Deleted { get; init; }

    public string TableName { get; init; } = "";
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Non-fatal diagnostics from the run, surfaced as
    /// <see cref="Reporting.EntryOutcome.Warnings"/>. Carries the key resolution for a table
    /// with no primary key, and the replaceStrategy decisions.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Phase 37-05 / LINK-02 pass 2: populated by ContentProvider after a successful
    /// content deserialize. Maps source-environment page IDs to the newly-assigned
    /// target-environment page IDs. The orchestrator aggregates maps across all
    /// Content predicates, then threads the combined map into SqlTable predicates
    /// that opt in via <see cref="Models.ProviderPredicateDefinition.ResolveLinksInColumns"/>.
    /// Null for non-Content providers or dry-run / failed runs.
    /// </summary>
    public IReadOnlyDictionary<int, int>? SourceToTargetPageMap { get; init; }

    /// <summary>
    /// Engine issue #35: populated by ContentProvider with the areas whose ecom language was not
    /// on target when the area was written. The orchestrator re-checks them after every entry
    /// has run, so a language delivered by a later SqlTable entry in the same run does not warn.
    /// </summary>
    public IReadOnlyList<Serialization.PendingEcomLanguageCheck> PendingEcomLanguageChecks { get; init; }
        = Array.Empty<Serialization.PendingEcomLanguageCheck>();

    public bool HasErrors => Failed > 0 || Errors.Count > 0;

    public string Summary =>
        $"{TableName}: {Created} created, {Updated} updated, " +
        $"{Skipped} skipped, {Failed} failed, {Deleted} deleted.";
}
