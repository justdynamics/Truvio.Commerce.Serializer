namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// Merges per-predicate flat exclusion lists with type-specific dictionary entries.
/// Used at each entity (page/paragraph/area) during serialize/deserialize to build
/// the effective exclusion set for that entity's specific item type or XML type.
/// </summary>
public static class ExclusionMerger
{
    /// <summary>
    /// Merges per-predicate flat field exclusions with type-specific dictionary entry.
    /// Returns null if no exclusions apply (preserves existing null-means-no-filtering optimization).
    /// </summary>
    public static HashSet<string>? MergeFieldExclusions(
        IReadOnlyList<string> predicateExclusions,
        IReadOnlyDictionary<string, List<string>> typedExclusions,
        string? itemTypeName)
    {
        var hasFlat = predicateExclusions.Count > 0;

        List<string>? typeList = null;
        var hasTyped = !string.IsNullOrEmpty(itemTypeName)
            && TryGetValueIgnoreCase(typedExclusions, itemTypeName!, out typeList)
            && typeList!.Count > 0;

        if (!hasFlat && !hasTyped)
            return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasFlat)
            foreach (var f in predicateExclusions) result.Add(f);
        if (hasTyped)
            foreach (var f in typeList!) result.Add(f);
        return result;
    }

    /// <summary>
    /// The effective field exclusions of one item: the predicate's flat set plus the config-level
    /// entry of the item's type. Without a config-level dictionary the predicate's set is the
    /// answer as it is (null stays null: no filtering). This is the expression every item write
    /// in the content deserializer needs; callers pass the write context's two values.
    /// </summary>
    public static IReadOnlySet<string>? ForItemType(
        IReadOnlySet<string>? predicateExclusions,
        IReadOnlyDictionary<string, List<string>>? typedExclusions,
        string? itemTypeName) =>
        typedExclusions != null
            ? MergeFieldExclusions(predicateExclusions?.ToList() ?? new List<string>(), typedExclusions, itemTypeName)
            : predicateExclusions;

    /// <summary>
    /// Merges per-predicate flat XML element exclusions with type-specific dictionary entry.
    /// Returns null if no exclusions apply.
    /// </summary>
    public static IReadOnlyList<string>? MergeXmlExclusions(
        IReadOnlyList<string> predicateExclusions,
        IReadOnlyDictionary<string, List<string>> typedExclusions,
        string? xmlTypeName)
    {
        var hasFlat = predicateExclusions.Count > 0;

        List<string>? typeList = null;
        var hasTyped = !string.IsNullOrEmpty(xmlTypeName)
            && TryGetValueIgnoreCase(typedExclusions, xmlTypeName!, out typeList)
            && typeList!.Count > 0;

        if (!hasFlat && !hasTyped)
            return null;

        // Use HashSet to deduplicate, then return as list
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasFlat)
            foreach (var e in predicateExclusions) set.Add(e);
        if (hasTyped)
            foreach (var e in typeList!) set.Add(e);
        return set.ToList();
    }

    /// <summary>
    /// Case-insensitive dictionary lookup. DW item type names may differ in casing
    /// between config and runtime (per research Pitfall 4).
    /// </summary>
    private static bool TryGetValueIgnoreCase(
        IReadOnlyDictionary<string, List<string>> dict,
        string key,
        out List<string>? value)
    {
        // Fast path: exact match
        if (dict.TryGetValue(key, out var exact))
        {
            value = exact;
            return true;
        }

        // Slow path: case-insensitive scan
        foreach (var kvp in dict)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
