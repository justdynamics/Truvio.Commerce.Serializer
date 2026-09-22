using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Providers;

namespace Truvio.Commerce.Serializer.Reporting;

/// <summary>
/// Phase 43 / REPORT-02: per-entry outcome record. Replaces aggregate
/// <see cref="OrchestratorResult.DeserializeResults"/> as the canonical reporting surface.
/// One <see cref="EntryOutcome"/> per dispatched manifest entry; <c>Skipped</c> outcomes
/// surface entries the orchestrator never dispatched (today's silent-skip class).
/// </summary>
/// <remarks>
/// Per CONTEXT D-02:
/// <list type="bullet">
/// <item>Files-don't-exist-on-disk ⇒ <see cref="EntryStatus.Failed"/> via <see cref="Failed"/>.</item>
/// <item>Dry-run ⇒ <see cref="EntryStatus.Succeeded"/> with <see cref="Counts"/> populated.</item>
/// <item>Merge-fill no-op ⇒ <see cref="EntryStatus.Succeeded"/> with <see cref="ProviderCounts.Skipped"/> non-zero.</item>
/// <item>providerFilter exclusion ⇒ <see cref="EntryStatus.Skipped"/> via <see cref="Skipped"/>.</item>
/// </list>
/// </remarks>
public sealed record EntryOutcome
{
    /// <summary>
    /// Phase 44 / IN-03 (D-09): reserved EntryId for synthetic run-level outcomes (e.g.,
    /// <see cref="RunLevelError"/> from strict-mode escalation). Grep-friendly literal —
    /// downstream consumers filter via <c>o.EntryId == EntryOutcome.RunLevelEntryId</c> per IN-06.
    /// </summary>
    public const string RunLevelEntryId = "<run-level>";

    /// <summary>
    /// Phase 44 / IN-03: reserved ProviderType for run-level synthetic outcomes — matches
    /// <see cref="RunLevelEntryId"/> so the pair is grep-equivalent.
    /// </summary>
    public const string RunLevelProviderType = "<run-level>";

    public required string EntryId { get; init; }
    public required string ProviderType { get; init; }
    public required EntryStatus Status { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public ProviderCounts Counts { get; init; } = ProviderCounts.Zero;
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Build an outcome from a dispatched <see cref="ProviderDeserializeResult"/>. Status
    /// is <see cref="EntryStatus.Failed"/> when <see cref="ProviderDeserializeResult.HasErrors"/>
    /// is true (covers both <c>Failed &gt; 0</c> and non-empty <c>Errors</c> per D-02);
    /// otherwise <see cref="EntryStatus.Succeeded"/>.
    /// </summary>
    /// <remarks>
    /// Phase 44 / WR-02: the <c>warnings</c> parameter + <c>Warned</c> EntryStatus value
    /// were dropped — no production caller passed warnings and the dead branch invited
    /// drift. If a future warning emitter wants the surface back, re-introduce explicitly
    /// alongside the call site that produces warnings.
    /// </remarks>
    public static EntryOutcome From(
        ManifestEntry entry,
        ProviderDeserializeResult r,
        TimeSpan duration)
    {
        var status = r.HasErrors ? EntryStatus.Failed : EntryStatus.Succeeded;

        return new EntryOutcome
        {
            EntryId = entry.EntryId,
            ProviderType = entry.ProviderType,
            Status = status,
            Message = r.Summary,
            Errors = r.Errors.ToList(),
            Warnings = r.Warnings.ToList(),
            Counts = ProviderCounts.From(r),
            Duration = duration
        };
    }

    /// <summary>
    /// Build a Skipped outcome — the orchestrator filtered the entry out (providerFilter
    /// exclusion). Reserved per CONTEXT D-02 — do not use for per-row skip counts inside
    /// a successful dispatch (those go in <see cref="ProviderCounts.Skipped"/>).
    /// </summary>
    public static EntryOutcome Skipped(ManifestEntry entry, string reason) =>
        new()
        {
            EntryId = entry.EntryId,
            ProviderType = entry.ProviderType,
            Status = EntryStatus.Skipped,
            Message = reason,
            Errors = Array.Empty<string>(),
            Warnings = Array.Empty<string>(),
            Counts = ProviderCounts.Zero,
            Duration = TimeSpan.Zero
        };

    /// <summary>
    /// Build a Failed outcome for entries that never reached a provider (no provider registered)
    /// or that threw before producing a result (files-don't-exist, exception during dispatch).
    /// </summary>
    public static EntryOutcome Failed(ManifestEntry entry, string error, TimeSpan duration = default) =>
        new()
        {
            EntryId = entry.EntryId,
            ProviderType = entry.ProviderType,
            Status = EntryStatus.Failed,
            Message = error,
            Errors = new[] { error },
            Warnings = Array.Empty<string>(),
            Counts = ProviderCounts.Zero,
            Duration = duration
        };

    /// <summary>
    /// Synthesise a Failed outcome that does not correspond to any single entry — used to
    /// surface run-level errors (e.g. <c>CumulativeStrictModeException</c> from the
    /// strict-mode escalator) into the entry-outcomes list per CONTEXT line 99-100, so
    /// <see cref="OrchestratorResult.HasErrors"/> aggregates them via the same path as
    /// per-entry failures.
    /// </summary>
    /// <remarks>
    /// Phase 44 / IN-03 + IN-06: the <c>"&lt;run-level&gt;"</c> literals are now sourced from
    /// <see cref="RunLevelEntryId"/> / <see cref="RunLevelProviderType"/> public const strings
    /// so downstream filter expressions (<see cref="OrchestratorResult.Summary"/>,
    /// command summary builders) can identify run-level synthesis via grep-friendly constants
    /// rather than open-coded literals.
    /// </remarks>
    public static EntryOutcome RunLevelError(string error) =>
        new()
        {
            EntryId = RunLevelEntryId,
            ProviderType = RunLevelProviderType,
            Status = EntryStatus.Failed,
            Message = error,
            Errors = new[] { error },
            Warnings = Array.Empty<string>(),
            Counts = ProviderCounts.Zero,
            Duration = TimeSpan.Zero
        };
}
