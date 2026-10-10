using Truvio.Commerce.Serializer.Models;

namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>Coverage state of a single content node.</summary>
public enum ContentCoverage
{
    /// <summary>Not covered by any predicate, and nothing below it is.</summary>
    None,
    /// <summary>
    /// Either the node is covered but part of its subtree is carved out by an exclude,
    /// or the node itself is not covered while a predicate targets a subtree below it.
    /// </summary>
    Partial,
    /// <summary>The node and (as far as path algebra can tell) its entire subtree are covered.</summary>
    Full
}

/// <summary>Coverage verdict plus a human-readable explanation for the tree tooltip.</summary>
public sealed record ContentCoverageResult(ContentCoverage Coverage, string Explanation)
{
    public static readonly ContentCoverageResult None = new(ContentCoverage.None, "");
}

/// <summary>
/// Pure path-algebra evaluation of how one mode's Content predicates cover a content node:
/// fully managed, partially managed (excluded descendants, or managed subtrees below an
/// unmanaged page), or not managed. The explanation names the predicate and the exclude /
/// subtree paths responsible, so a developer can see WHY a page is managed or not straight
/// from the tree tooltip.
/// </summary>
public class ContentCoverageEvaluator
{
    private readonly List<ProviderPredicateDefinition> _predicates;
    private readonly string _modeWord;
    private readonly string _modeWordCapitalized;

    /// <param name="contentPredicates">
    /// Content predicates of ONE mode (already expanded for language layers when applicable).
    /// </param>
    /// <param name="modeWord">Word used in explanations: "replace" (default) or "merge".</param>
    public ContentCoverageEvaluator(IEnumerable<ProviderPredicateDefinition> contentPredicates, string modeWord = "replace")
    {
        _predicates = contentPredicates.ToList();
        _modeWord = modeWord;
        _modeWordCapitalized = string.IsNullOrEmpty(modeWord)
            ? modeWord
            : char.ToUpperInvariant(modeWord[0]) + modeWord.Substring(1);
    }

    /// <summary>Path-only evaluation; identity selectors (R3) are not applied, see <see cref="ContentPageContext.ForPath"/>.</summary>
    public ContentCoverageResult Evaluate(string contentPath, int areaId) =>
        Evaluate(ContentPageContext.ForPath(contentPath), areaId);

    /// <summary>
    /// Content ownership R3: evaluates one page with its full context, so identity predicates
    /// (pageUniqueIds, navigationTags, itemTypes, excludePageUniqueIds) decide coverage with the
    /// same matcher serialize uses.
    /// </summary>
    public ContentCoverageResult Evaluate(ContentPageContext page, int areaId)
    {
        var contentPath = page.Path;
        var areaPredicates = _predicates.Where(p => p.AreaId == areaId).ToList();
        if (areaPredicates.Count == 0)
            return ContentCoverageResult.None;

        var includedBy = areaPredicates.Where(p => new ContentPredicate(p).ShouldInclude(page, areaId)).ToList();

        if (includedBy.Count > 0)
        {
            // Excludes that carve descendants out of this node's subtree — unless another
            // predicate covers that excluded path again.
            var carvedOut = includedBy
                .SelectMany(p => p.Excludes)
                .Where(e => IsStrictlyUnder(e, contentPath))
                .Where(e => !areaPredicates.Any(q => Includes(q, e)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var names = string.Join("', '", includedBy.Select(p => p.Name));
            if (carvedOut.Count == 0)
                return new ContentCoverageResult(ContentCoverage.Full,
                    $"{_modeWordCapitalized}-managed by '{names}'");

            return new ContentCoverageResult(ContentCoverage.Partial,
                $"Partially {_modeWord}-managed by '{names}' — excluded below this page: {string.Join(", ", carvedOut)}");
        }

        // Node itself is not covered. Is a predicate targeting a subtree below it?
        var managedBelow = areaPredicates
            .Where(p => IsStrictlyUnder(p.Path, contentPath))
            .Select(p => $"{p.Path} ('{p.Name}')")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (managedBelow.Count > 0)
            return new ContentCoverageResult(ContentCoverage.Partial,
                $"Not managed itself — contains {_modeWord}-managed subtree(s): {string.Join(", ", managedBelow)}");

        return ContentCoverageResult.None;
    }

    /// <summary>
    /// Names of the predicates that include THIS node itself — page-level inclusion,
    /// independent of subtree carve-outs. Empty when the node's own content is not managed.
    /// Used by the edit-screen mode alerts, where "a subtree below is managed"
    /// (the second flavour of <see cref="ContentCoverage.Partial"/>) must NOT warn.
    /// </summary>
    public IReadOnlyList<string> GetManagingPredicateNames(string contentPath, int areaId)
        => GetManagingPredicateNames(ContentPageContext.ForPath(contentPath), areaId);

    /// <summary>R3: <see cref="GetManagingPredicateNames(string, int)"/> with the page's full context.</summary>
    public IReadOnlyList<string> GetManagingPredicateNames(ContentPageContext page, int areaId)
        => GetManagingPredicates(page, areaId).Select(p => p.Name).ToList();

    /// <summary>The predicates that include THIS page itself (R3 matcher), for lifecycle-aware cues.</summary>
    public IReadOnlyList<ProviderPredicateDefinition> GetManagingPredicates(ContentPageContext page, int areaId)
        => _predicates
            .Where(p => p.AreaId == areaId && new ContentPredicate(p).ShouldInclude(page, areaId))
            .ToList();

    /// <summary>Single-predicate inclusion: under the predicate path and not under any of its excludes.</summary>
    private static bool Includes(ProviderPredicateDefinition predicate, string contentPath)
    {
        if (!ContentPredicate.IsUnderPath(contentPath, predicate.Path))
            return false;
        return !predicate.Excludes.Any(e => ContentPredicate.IsUnderPath(contentPath, e));
    }

    /// <summary>candidate lies under basePath but is not basePath itself.</summary>
    private static bool IsStrictlyUnder(string candidate, string basePath)
    {
        if (string.Equals(candidate, basePath, StringComparison.OrdinalIgnoreCase))
            return false;
        return ContentPredicate.IsUnderPath(candidate, basePath);
    }
}
