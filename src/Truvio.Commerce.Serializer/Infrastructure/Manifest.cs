using System.Text.Json;
using System.Text.Json.Serialization;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// v0.6.0 manifest envelope. Written atomically by <see cref="ManifestWriter"/> at the end
/// of every serialize run; read by the deserialize path in Phase 43. Schema version 2;
/// older manifests fail the JsonDocument precheck before typed deserialize. Per
/// <c>feedback_no_backcompat.md</c> — re-run serialize, no migration code.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Manifest
{
    /// <summary>Schema version — must equal <see cref="ManifestSchema.CurrentVersion"/> on read.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>"replace" or "merge" — matches the on-disk subfolder name.</summary>
    public required string Mode { get; init; }

    /// <summary>UTC timestamp when serialize completed.</summary>
    public required DateTime WrittenAtUtc { get; init; }

    /// <summary>
    /// Atomic-write completion sentinel. Always written as <c>true</c> by <see cref="ManifestWriter"/>;
    /// reading a manifest where this is missing or false (which can only happen on a torn write)
    /// MUST fail at read time. Phase 42-01 defends pitfall #2 (torn manifest from crashed serialize).
    /// </summary>
    public required bool Complete { get; init; }

    /// <summary>
    /// Top-level by-ItemType field exclusions baked into the manifest at serialize time so the
    /// deserialize path does not need to consult <c>Serializer.config.json</c> to read them
    /// (per MANIFEST-05 / SUMMARY.md settled question 1). Empty dict when none configured.
    /// </summary>
    public required IReadOnlyDictionary<string, List<string>> ExcludeFieldsByItemType { get; init; }

    /// <summary>Top-level by-ItemType XML-element exclusions, same rationale as <see cref="ExcludeFieldsByItemType"/>.</summary>
    public required IReadOnlyDictionary<string, List<string>> ExcludeXmlElementsByType { get; init; }

    /// <summary>Polymorphic entries — discriminated by <c>providerType</c> ("Content" / "SqlTable").</summary>
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }

    /// <summary>
    /// Content ownership R15: the data class of what this mode folder ships (for example
    /// <c>content</c>, <c>master</c>, <c>reference</c>, <c>transactional</c>). Repository metadata:
    /// carried through serialize, never read by deserialize. Omitted from the file when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataClass { get; init; }

    /// <summary>
    /// Content ownership R15: the system of record for what this folder ships (for example
    /// <c>dynamicweb</c>, <c>erp</c>, <c>cms-editors</c>). Carried through serialize, never read
    /// by deserialize. Omitted from the file when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemOfRecord { get; init; }

    /// <summary>
    /// Content ownership R15: free repository metadata (any JSON values under any keys, for
    /// example an <c>ownership</c> note). Carried through serialize, never read by deserialize.
    /// Every other unknown top-level key still fails the strict read. Omitted when unset.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}
