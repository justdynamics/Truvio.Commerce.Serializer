using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Configuration;

public class ContentPredicate
{
    private readonly ProviderPredicateDefinition _definition;

    public ContentPredicate(ProviderPredicateDefinition definition)
    {
        _definition = definition;
    }

    /// <summary>
    /// Returns true if the given content path and areaId should be included according to this predicate.
    /// Exclude rules override include rules. Path-only: identity selectors (R3) cannot be decided
    /// from a path and are not applied, see <see cref="ContentPageContext.ForPath"/>.
    /// </summary>
    public bool ShouldInclude(string contentPath, int areaId) =>
        Evaluate(ContentPageContext.ForPath(contentPath), areaId) == ContentMatch.Included;

    /// <summary>True when the page is selected by this predicate (path, identity, excludes).</summary>
    public bool ShouldInclude(ContentPageContext page, int areaId) =>
        Evaluate(page, areaId) == ContentMatch.Included;

    /// <summary>
    /// Content ownership R3: matches one page. Area and path first; then excludes (path or GUID,
    /// self or ancestor), which always win; then, when the predicate has identity selectors and the
    /// context carries an identity, the page must match one of them: its GUID is listed (or, with
    /// <c>includeDescendants</c>, an ancestor's is), its navigation tag is listed, or its item type
    /// is listed.
    /// </summary>
    public ContentMatch Evaluate(ContentPageContext page, int areaId)
    {
        if (areaId != _definition.AreaId)
            return ContentMatch.NotSelected;

        if (!IsUnderPath(page.Path, _definition.Path))
            return ContentMatch.NotSelected;

        foreach (var exclude in _definition.Excludes)
        {
            if (IsUnderPath(page.Path, exclude))
                return ContentMatch.Excluded;
        }

        if (page.HasIdentity && _definition.ExcludePageUniqueIds.Count > 0
            && (_definition.ExcludePageUniqueIds.Contains(page.PageUniqueId)
                || page.AncestorUniqueIds.Any(_definition.ExcludePageUniqueIds.Contains)))
            return ContentMatch.Excluded;

        if (!_definition.HasIdentitySelectors || !page.HasIdentity)
            return ContentMatch.Included;

        return MatchesIdentity(_definition, page) ? ContentMatch.Included : ContentMatch.NotSelected;
    }

    /// <summary>R3 identity selection: GUID (with optional descendants), navigation tag or item type, by OR.</summary>
    internal static bool MatchesIdentity(ProviderPredicateDefinition definition, ContentPageContext page)
    {
        if (definition.PageUniqueIds.Count > 0)
        {
            if (definition.PageUniqueIds.Contains(page.PageUniqueId))
                return true;
            if (definition.IncludeDescendants && page.AncestorUniqueIds.Any(definition.PageUniqueIds.Contains))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(page.NavigationTag)
            && definition.NavigationTags.Any(t => string.Equals(t?.Trim(), page.NavigationTag.Trim(), StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(page.ItemType)
            && definition.ItemTypes.Any(t => string.Equals(t?.Trim(), page.ItemType.Trim(), StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if candidatePath equals basePath or starts with basePath followed by a "/" (path boundary check).
    /// Comparison is case-insensitive (OrdinalIgnoreCase).
    /// Internal so <see cref="ContentCoverageEvaluator"/> shares the exact same path semantics.
    /// </summary>
    internal static bool IsUnderPath(string candidatePath, string basePath)
    {
        if (string.Equals(candidatePath, basePath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Root path "/" includes everything
        if (basePath == "/")
            return candidatePath.StartsWith("/", StringComparison.OrdinalIgnoreCase);

        return candidatePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Aggregates multiple ContentPredicate instances and evaluates them with OR logic:
/// a path is included if ANY predicate includes it.
/// </summary>
public class ContentPredicateSet
{
    private readonly List<ContentPredicate> _predicates;

    public ContentPredicateSet(SerializerConfiguration configuration)
    {
        _predicates = configuration.Predicates
            .Select(p => new ContentPredicate(p))
            .ToList();
    }

    /// <summary>
    /// Returns true if any predicate in the set includes the given content path and areaId.
    /// </summary>
    public bool ShouldInclude(string contentPath, int areaId)
    {
        return _predicates.Any(p => p.ShouldInclude(contentPath, areaId));
    }

    /// <summary>True if any predicate in the set selects the page (R3: path, identity, excludes).</summary>
    public bool ShouldInclude(ContentPageContext page, int areaId)
    {
        return _predicates.Any(p => p.ShouldInclude(page, areaId));
    }
}
