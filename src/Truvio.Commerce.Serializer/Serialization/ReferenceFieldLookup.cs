using System.Collections.Concurrent;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Engine issue #15: answers "which fields of this item type are declared as page / paragraph
/// / item references?" so <see cref="ContentDeserializer"/> can restrict the resolver's
/// raw-numeric short-circuit to exactly those fields.
///
/// <para>Thin DW-metadata half of <see cref="ReferenceFieldClassifier"/>; mirrors
/// <see cref="ButtonDataFieldLookup"/>, including its "cache positives only" rule — replace
/// mode deploys the item-type XML in the same run that writes the items, so a type whose
/// metadata is not readable yet must be re-probed rather than pinned for the process.</para>
/// </summary>
internal static class ReferenceFieldLookup
{
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// System names of the reference-typed fields on <paramref name="itemType"/>.
    /// Returns <c>null</c> when the type's metadata cannot be read — "unknown", which callers
    /// treat as the pre-#15 permissive behaviour rather than silently disabling remapping for
    /// a type whose XML lands later in the same run. An empty set means "read, and none".
    /// </summary>
    public static IReadOnlySet<string>? For(string? itemType, Action<string>? log = null)
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

            var referenceFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // GetItemFields includes inherited fields (matches ButtonDataFieldLookup).
            foreach (var field in Dynamicweb.Content.Items.ItemManager.Metadata.GetItemFields(typeMetadata))
            {
                if (!string.IsNullOrWhiteSpace(field.SystemName) &&
                    ReferenceFieldClassifier.IsReferenceEditor(field.Editor?.TypeName))
                {
                    referenceFields.Add(field.SystemName);
                }
            }

            _cache[itemType] = referenceFields;
            return referenceFields;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  Could not read field metadata for item type '{itemType}' " +
                        $"({ex.Message}) — raw-numeric link remapping stays permissive for it.");
            return null;
        }
    }

    /// <summary>Test seam: drops cached resolutions so a test can re-probe metadata.</summary>
    internal static void ClearCache() => _cache.Clear();
}
