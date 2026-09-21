using Truvio.Commerce.Serializer.Providers;

namespace Truvio.Commerce.Serializer.Reporting;

/// <summary>
/// Phase 43 / REPORT-02: immutable counts component of <see cref="EntryOutcome"/>. Matches
/// the per-table counts in <see cref="ProviderDeserializeResult"/> exactly so
/// <see cref="From(ProviderDeserializeResult)"/> is a straight projection.
/// </summary>
/// <remarks>
/// <c>Deleted</c> is optional so every existing four-argument construction keeps compiling
/// and every existing JSON name keeps its meaning; it is non-zero only for the opted-in
/// <c>replaceStrategy: truncate</c> path.
/// </remarks>
public sealed record ProviderCounts(int Created, int Updated, int Skipped, int Failed, int Deleted = 0)
{
    /// <summary>Zero-counts singleton for skipped/failed-without-dispatch outcomes.</summary>
    public static ProviderCounts Zero { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Project a per-table provider result into the entry-outcome counts shape.</summary>
    public static ProviderCounts From(ProviderDeserializeResult r) =>
        new(r.Created, r.Updated, r.Skipped, r.Failed, r.Deleted);
}
