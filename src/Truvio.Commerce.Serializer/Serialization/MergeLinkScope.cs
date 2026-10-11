namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Content ownership R2: the link scope of documents a Merge run filled instead of wrote.
/// </summary>
/// <remarks>
/// The link pass rewrites source page and paragraph ids into target ids. A document the run
/// created or overwrote holds source ids in every field, so every field is resolved. A document
/// Merge filled holds two kinds of value: the fields the run filled (source ids, from YAML) and
/// the fields it kept (the host's values, already target ids after the first delivery). Reading
/// a kept value as a source id maps it again: a NavigationRoot of ID=52 (the target of source 50)
/// becomes the target of source 52, and every later Merge moves it once more. So for a merged
/// item only the filled fields are resolved, and the page shortcut, the page's ecom product page
/// and a paragraph's module settings are resolved only when the run filled them.
/// A document with no record here was written in full and is resolved as before.
/// </remarks>
internal sealed class MergeLinkScope
{
    private readonly Dictionary<string, HashSet<string>> _itemFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _keptShortcuts = new();
    private readonly HashSet<int> _keptProductPages = new();
    private readonly HashSet<int> _keptModuleSettings = new();

    private static string ItemKey(string itemType, string itemId) => $"{itemType}|{itemId}";

    /// <summary>
    /// Records a Merge fill of an existing item: <paramref name="filledFields"/> are the fields the
    /// run wrote (may be empty: nothing filled, nothing to resolve). Repeated calls add up.
    /// </summary>
    public void RecordItemFill(string itemType, string itemId, IEnumerable<string> filledFields)
    {
        var key = ItemKey(itemType, itemId);
        if (!_itemFields.TryGetValue(key, out var set))
            _itemFields[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(filledFields);
    }

    /// <summary>
    /// The fields of an item the link pass may resolve: <c>null</c> when the run wrote the item in
    /// full (resolve everything), otherwise the fields a Merge filled.
    /// </summary>
    public IReadOnlySet<string>? ItemFieldScope(string? itemType, string? itemId)
    {
        if (string.IsNullOrEmpty(itemType) || string.IsNullOrEmpty(itemId))
            return null;
        return _itemFields.TryGetValue(ItemKey(itemType, itemId), out var set) ? set : null;
    }

    /// <summary>The fields to hand to the resolver: all of them, or only those in <paramref name="scope"/>.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> Restrict(
        IEnumerable<KeyValuePair<string, object?>> fields, IReadOnlySet<string>? scope)
        => scope is null ? fields : fields.Where(kvp => scope.Contains(kvp.Key));

    /// <summary>A Merge kept the page's existing shortcut.</summary>
    public void KeepShortcut(int pageId) => _keptShortcuts.Add(pageId);

    /// <summary>A Merge kept the page's existing ecom navigation product page.</summary>
    public void KeepProductPage(int pageId) => _keptProductPages.Add(pageId);

    /// <summary>
    /// A Merge kept the paragraph's existing module settings, or filled elements into them; the
    /// existing elements already hold target ids, so the settings are not re-resolved.
    /// </summary>
    public void KeepModuleSettings(int paragraphId) => _keptModuleSettings.Add(paragraphId);

    public bool ResolveShortcut(int pageId) => !_keptShortcuts.Contains(pageId);
    public bool ResolveProductPage(int pageId) => !_keptProductPages.Contains(pageId);
    public bool ResolveModuleSettings(int paragraphId) => !_keptModuleSettings.Contains(paragraphId);
}
