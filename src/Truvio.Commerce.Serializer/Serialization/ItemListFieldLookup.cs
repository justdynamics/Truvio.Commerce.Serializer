using System.Collections.Concurrent;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R7: answers "which fields of this item type are item lists?" from
/// Dynamicweb's item metadata. Thin platform half of <see cref="ItemListFieldClassifier"/>;
/// mirrors <see cref="ReferenceFieldLookup"/>, including its "cache positives only" rule: replace
/// mode delivers the item-type XML in the same run that writes the items, so a type whose
/// metadata is not readable yet is probed again instead of pinned for the process.
/// </summary>
internal static class ItemListFieldLookup
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ItemListFieldInfo>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The item list fields of <paramref name="itemType"/> by system name (case-insensitive).
    /// Null when the type's metadata cannot be read; an empty map means "read, and none".
    /// </summary>
    public static IReadOnlyDictionary<string, ItemListFieldInfo>? For(string? itemType, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(itemType))
            return null;

        if (_cache.TryGetValue(itemType, out var cached))
            return cached;

        try
        {
            var typeMetadata = Dynamicweb.Content.Items.ItemManager.Metadata.GetItemType(itemType);
            if (typeMetadata == null)
                return null;

            var listFields = new Dictionary<string, ItemListFieldInfo>(StringComparer.OrdinalIgnoreCase);
            // GetItemFields includes inherited fields (matches ReferenceFieldLookup).
            foreach (var field in Dynamicweb.Content.Items.ItemManager.Metadata.GetItemFields(typeMetadata))
            {
                if (string.IsNullOrWhiteSpace(field.SystemName))
                    continue;

                var info = ItemListFieldClassifier.FromEditorConfiguration(field.Editor?.TypeName, field.EditorConfiguration);
                if (info is not null)
                    listFields[field.SystemName] = info;
            }

            _cache[itemType] = listFields;
            return listFields;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  Could not read field metadata for item type '{itemType}' ({ex.Message}); " +
                        "its item list fields are not known.");
            return null;
        }
    }

    /// <summary>Test seam: drops cached resolutions so a test can re-probe metadata.</summary>
    internal static void ClearCache() => _cache.Clear();
}
