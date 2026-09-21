using System.Text.Json;
using System.Text.Json.Serialization;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Canonical schema constants + JsonSerializerOptions for v0.6.0 manifest read/write.
/// Phase 42-01: hard cut from v1 (the implicit flat-files manifest). No backcompat;
/// reading a manifest with SchemaVersion != CurrentVersion fails fast at the JsonDocument
/// precheck before typed deserialize sees mismatched shapes.
/// </summary>
public static class ManifestSchema
{
    /// <summary>
    /// Manifest schema version written by this build. Version 3 adds the optional SqlTableEntry
    /// fields <c>keyColumns</c> and <c>replaceStrategy</c> (the heap key-resolution fix).
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Versions this build can read. Version 3 is version 2 plus two optional fields, so a
    /// manifest serialized before the fix still loads and still deserializes; only the
    /// version marker moved. A version outside this set is rejected at the schemaVersion gate
    /// before typed deserialize sees a mismatched shape.
    /// </summary>
    public static readonly IReadOnlySet<int> ReadableVersions = new HashSet<int> { 2, 3 };

    /// <summary>
    /// Canonical JsonSerializerOptions for every manifest read/write. Single options bag —
    /// do NOT introduce a parallel one (per STACK.md §4 reuse rule). Strict reads via
    /// UnmappedMemberHandling.Disallow + JsonDerivedType allow-list with
    /// IgnoreUnrecognizedTypeDiscriminators=false.
    /// </summary>
    public static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}
