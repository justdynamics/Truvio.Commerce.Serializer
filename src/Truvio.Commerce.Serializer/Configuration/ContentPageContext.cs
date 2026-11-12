namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// What a Content predicate is matched against for one page (content ownership R3): its path in
/// predicate space plus the identity a predicate can select by. For a language-layer page every
/// value is its MASTER page's (path, GUID, navigation tag, item type, ancestor GUIDs), so a layer
/// always follows its master and an editor's rename or translation never moves it.
/// <para>
/// A context built from a path alone (<see cref="ForPath"/>) has no identity. Identity selectors
/// cannot be decided on it, so it is matched on path and path excludes only: the answer is the
/// superset the predicate could select. Path-only callers (inline API scopes, scoped manifests)
/// keep working; every caller that has the page builds the full context.
/// </para>
/// </summary>
public sealed record ContentPageContext
{
    public required string Path { get; init; }
    public Guid PageUniqueId { get; init; }
    public string? NavigationTag { get; init; }
    public string? ItemType { get; init; }

    /// <summary>GUIDs of the page's ancestors, nearest first or last (order is not used).</summary>
    public IReadOnlyList<Guid> AncestorUniqueIds { get; init; } = Array.Empty<Guid>();

    /// <summary>True when the context carries a page GUID, so identity selectors can be decided.</summary>
    public bool HasIdentity => PageUniqueId != Guid.Empty;

    public static ContentPageContext ForPath(string path) => new() { Path = path };
}

/// <summary>Outcome of matching one page against one Content predicate.</summary>
public enum ContentMatch
{
    /// <summary>Outside the predicate's area or path, or not selected by its identity selectors.</summary>
    NotSelected,
    /// <summary>Carved out by an exclude (path or GUID). The page's subtree is out too.</summary>
    Excluded,
    /// <summary>Selected.</summary>
    Included
}
