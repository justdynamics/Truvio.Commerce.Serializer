using System.Collections.Concurrent;
using Dynamicweb.Content.Items.Metadata;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Engine issue #15: answers "which fields of this item type are declared as page / paragraph
/// / item references?" so <see cref="ContentDeserializer"/> can restrict the resolver's
/// raw-numeric short-circuit to exactly those fields. Engine issue #32 adds the option-list
/// fields whose source yields a page or paragraph id, whatever their editor.
///
/// <para>Thin DW-metadata half of <see cref="ReferenceFieldClassifier"/>; mirrors
/// <see cref="ButtonDataFieldLookup"/>, including its "cache positives only" rule — replace
/// mode deploys the item-type XML in the same run that writes the items, so a type whose
/// metadata is not readable yet must be re-probed rather than pinned for the process.</para>
/// </summary>
internal static class ReferenceFieldLookup
{
    private static readonly ConcurrentDictionary<string, ItemTypeReferenceFields> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The reference-typed fields on <paramref name="itemType"/>.
    /// Returns <c>null</c> when the type's metadata cannot be read — "unknown", which callers
    /// treat as the pre-#15 permissive behaviour rather than silently disabling remapping for
    /// a type whose XML lands later in the same run. Empty sets mean "read, and none".
    /// </summary>
    public static ItemTypeReferenceFields? For(string? itemType, Action<string>? log = null)
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

            var editorFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var optionFields = new Dictionary<string, ReferenceKind>(StringComparer.OrdinalIgnoreCase);
            // GetItemFields includes inherited fields (matches ButtonDataFieldLookup).
            foreach (var field in Dynamicweb.Content.Items.ItemManager.Metadata.GetItemFields(typeMetadata))
            {
                if (string.IsNullOrWhiteSpace(field.SystemName))
                    continue;

                // Engine issue #32: same rule as ReferenceFieldClassifier.FromItemTypeXml —
                // an option source yielding a page/paragraph id wins over the editor.
                var source = field.Options?.Source;
                var valueField = source switch
                {
                    FieldOptionMetadataItemSource itemSource => itemSource.ValueField,
                    FieldOptionMetadataSqlSource sqlSource => sqlSource.ValueField,
                    _ => null
                };
                var kind = ReferenceFieldClassifier.ClassifyOptionSource(source?.SourceType.ToString(), valueField);

                if (kind != ReferenceKind.None)
                    optionFields[field.SystemName] = kind;
                else if (ReferenceFieldClassifier.IsReferenceEditor(field.Editor?.TypeName))
                    editorFields.Add(field.SystemName);
            }

            var result = new ItemTypeReferenceFields(editorFields, optionFields);
            _cache[itemType] = result;
            return result;
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
