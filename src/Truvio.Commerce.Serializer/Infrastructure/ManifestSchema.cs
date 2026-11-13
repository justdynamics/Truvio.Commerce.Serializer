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
    /// Content ownership R7: the engine floor of a mode folder whose documents carry
    /// <c>itemLists</c>. An engine through 1.0.9-beta ignores the key and nulls the list field
    /// under Replace, so it must not deserialize such a folder.
    /// <para>This is the version the item-list shape ships in. If the release that carries it is
    /// named differently, change this constant before the release (a unit test pins it above
    /// 1.0.9-beta and, once the package version moves, at or below it).</para>
    /// </summary>
    public const string ItemListsMinEngineVersion = "1.1.0-beta";

    /// <summary>
    /// The highest floor this engine meets. It is a code fact, not the package version: a
    /// development build still numbered 1.0.9-beta reads the item-list shape.
    /// </summary>
    public const string EngineFloorMet = ItemListsMinEngineVersion;

    /// <summary>
    /// Refuses a manifest whose <see cref="Manifest.MinEngineVersion"/> is above what this engine
    /// meets, naming both versions. A manifest without a floor, or with one this engine meets,
    /// passes. An unreadable floor is refused too: it cannot be shown to be met.
    /// </summary>
    public static void CheckEngineFloor(Manifest manifest, string path)
    {
        if (string.IsNullOrWhiteSpace(manifest.MinEngineVersion))
            return;
        if (!EngineVersion.TryParse(manifest.MinEngineVersion, out var floor))
            throw new InvalidOperationException(
                $"Manifest '{path}' declares minEngineVersion '{manifest.MinEngineVersion}', which is not a version. Re-serialize the folder.");
        if (EngineVersion.Compare(floor, EngineVersion.Parse(EngineFloorMet)) > 0)
            throw new InvalidOperationException(
                $"Manifest '{path}' needs Truvio.Commerce.Serializer {manifest.MinEngineVersion} or newer to deserialize correctly; " +
                $"this engine meets {EngineFloorMet}. Upgrade the engine on this host before delivering the folder.");
    }

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
