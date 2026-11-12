using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Coverage states the tree annotations are built on: Full (sync icon), Partial
/// (sync-slash icon — excluded descendants, or managed subtree below an unmanaged page),
/// None (no icon). Explanations must name the predicates / paths responsible.
/// </summary>
public class ContentCoverageEvaluatorTests
{
    private static ProviderPredicateDefinition Predicate(
        string name = "baseline", int areaId = 3, string path = "/", params string[] excludes) => new()
    {
        Name = name,
        ProviderType = "Content",
        Mode = SerializerMode.Replace,
        AreaId = areaId,
        Path = path,
        Excludes = excludes.ToList()
    };

    // -------------------------------------------------------------------------
    // Full coverage
    // -------------------------------------------------------------------------

    [Fact]
    public void RootPredicateWithoutExcludes_CoversEverythingFully()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate() });

        var result = evaluator.Evaluate("/Shop/Products", 3);

        Assert.Equal(ContentCoverage.Full, result.Coverage);
        Assert.Contains("baseline", result.Explanation);
    }

    [Fact]
    public void SubtreePredicate_NodeInsideSubtree_IsFull()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/Shop") });

        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate("/Shop", 3).Coverage);
        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate("/Shop/Products", 3).Coverage);
    }

    [Fact]
    public void ExcludeElsewhere_DoesNotDowngradeUnrelatedSubtree()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/", excludes: "/Posts") });

        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate("/Shop", 3).Coverage);
    }

    [Fact]
    public void ExcludeReincludedByOtherPredicate_StaysFull()
    {
        var evaluator = new ContentCoverageEvaluator(new[]
        {
            Predicate("a", path: "/", excludes: "/Shop/Hidden"),
            Predicate("b", path: "/Shop/Hidden")
        });

        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate("/Shop", 3).Coverage);
    }

    // -------------------------------------------------------------------------
    // Partial — excluded descendants
    // -------------------------------------------------------------------------

    [Fact]
    public void IncludedNodeWithExcludedDescendant_IsPartial_AndNamesTheExclude()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/", excludes: "/Shop/Hidden") });

        var result = evaluator.Evaluate("/Shop", 3);

        Assert.Equal(ContentCoverage.Partial, result.Coverage);
        Assert.Contains("/Shop/Hidden", result.Explanation);
        Assert.Contains("baseline", result.Explanation);
    }

    [Fact]
    public void RootNode_WithAnyExclude_IsPartialAtRoot()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/", excludes: "/Posts") });

        var result = evaluator.Evaluate("/", 3);

        Assert.Equal(ContentCoverage.Partial, result.Coverage);
        Assert.Contains("/Posts", result.Explanation);
    }

    [Fact]
    public void ExcludedNodeItself_GetsNoIcon_ButItsParentIsPartial()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/", excludes: "/Shop/Hidden") });

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop/Hidden", 3).Coverage);
        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop/Hidden/Deeper", 3).Coverage);
        Assert.Equal(ContentCoverage.Partial, evaluator.Evaluate("/Shop", 3).Coverage);
    }

    // -------------------------------------------------------------------------
    // Partial — managed subtree below an unmanaged node
    // -------------------------------------------------------------------------

    [Fact]
    public void UnmanagedAncestorOfManagedSubtree_IsPartial_AndNamesTheSubtree()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/Shop/Products") });

        var result = evaluator.Evaluate("/Shop", 3);

        Assert.Equal(ContentCoverage.Partial, result.Coverage);
        Assert.Contains("/Shop/Products", result.Explanation);
        Assert.Contains("Not managed itself", result.Explanation);
    }

    [Fact]
    public void SiblingOfManagedSubtree_IsNone()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/Shop/Products") });

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop/Checkout", 3).Coverage);
        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/About", 3).Coverage);
    }

    // -------------------------------------------------------------------------
    // Area scoping + edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void OtherAreaPredicates_DoNotApply()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(areaId: 7) });

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop", 3).Coverage);
    }

    [Fact]
    public void NoPredicates_IsNone()
    {
        var evaluator = new ContentCoverageEvaluator(Array.Empty<ProviderPredicateDefinition>());

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop", 3).Coverage);
    }

    [Fact]
    public void PathBoundary_ExcludePrefixDoesNotMatchSiblingWithSharedPrefix()
    {
        // "/Shop" exclude must not swallow "/Shopping"
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/", excludes: "/Shop") });

        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate("/Shopping", 3).Coverage);
        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop", 3).Coverage);
    }

    [Fact]
    public void CaseInsensitive_PathsMatch()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate(path: "/shop", excludes: "/shop/hidden") });

        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate("/SHOP/Products", 3).Coverage);
        Assert.Equal(ContentCoverage.None, evaluator.Evaluate("/Shop/HIDDEN", 3).Coverage);
    }

    [Fact]
    public void MergeModeWord_AppearsInExplanations()
    {
        var evaluator = new ContentCoverageEvaluator(
            new[] { Predicate("starter-posts", path: "/Posts") }, modeWord: "merge");

        var full = evaluator.Evaluate("/Posts", 3);
        Assert.Equal(ContentCoverage.Full, full.Coverage);
        Assert.Contains("Merge-managed by 'starter-posts'", full.Explanation);

        var ancestor = evaluator.Evaluate("/", 3);
        Assert.Equal(ContentCoverage.Partial, ancestor.Coverage);
        Assert.Contains("merge-managed subtree", ancestor.Explanation);
    }

    [Fact]
    public void MultiplePredicates_ExplanationNamesAllIncluding()
    {
        var evaluator = new ContentCoverageEvaluator(new[]
        {
            Predicate("a", path: "/Shop"),
            Predicate("b", path: "/")
        });

        var result = evaluator.Evaluate("/Shop", 3);

        Assert.Equal(ContentCoverage.Full, result.Coverage);
        Assert.Contains("a", result.Explanation);
        Assert.Contains("b", result.Explanation);
    }

    // -------------------------------------------------------------------------
    // R3: an identity-selected page below an unselected node
    // -------------------------------------------------------------------------

    private static readonly Guid ShopId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProductsId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid CartId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid PostsId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");

    private static ContentPageContext Page(string path, Guid id, params Guid[] ancestors) => new()
    {
        Path = path,
        PageUniqueId = id,
        AncestorUniqueIds = ancestors
    };

    /// <summary>The tree walk's answer for a predicate that selects /Shop/Products/Cart only.</summary>
    private static IReadOnlyCollection<Guid> AncestorsOfCart(ProviderPredicateDefinition _) => new[] { ShopId, ProductsId };

    [Fact]
    public void IdentitySelectedDescendant_AncestorIsPartial_AndNamesThePredicate()
    {
        var predicate = Predicate("system") with { PageUniqueIds = new() { CartId } };
        var evaluator = new ContentCoverageEvaluator(new[] { predicate }, "replace", AncestorsOfCart);

        var shop = evaluator.Evaluate(Page("/Shop", ShopId), 3);
        var products = evaluator.Evaluate(Page("/Shop/Products", ProductsId, ShopId), 3);

        Assert.Equal(ContentCoverage.Partial, shop.Coverage);
        Assert.Contains("Not managed itself", shop.Explanation);
        Assert.Contains("selected by identity ('system')", shop.Explanation);
        Assert.Equal(ContentCoverage.Partial, products.Coverage);
        // The alert rule is unchanged: the ancestor's own content is not managed.
        Assert.Empty(evaluator.GetManagingPredicateNames(Page("/Shop", ShopId), 3));
    }

    [Fact]
    public void IdentitySelectedDescendant_SelectedPageIsFull_AndASiblingBranchIsNone()
    {
        var predicate = Predicate("system") with { PageUniqueIds = new() { CartId } };
        var evaluator = new ContentCoverageEvaluator(new[] { predicate }, "replace", AncestorsOfCart);

        Assert.Equal(ContentCoverage.Full,
            evaluator.Evaluate(Page("/Shop/Products/Cart", CartId, ProductsId, ShopId), 3).Coverage);
        Assert.Equal(ContentCoverage.None, evaluator.Evaluate(Page("/Posts", PostsId), 3).Coverage);
    }

    [Fact]
    public void IdentitySelectedDescendant_WithoutTheTreeWalk_StaysPathOnly()
    {
        // No ancestor source (path-only callers): the answer is the one from before R3.
        var predicate = Predicate("system") with { PageUniqueIds = new() { CartId } };
        var evaluator = new ContentCoverageEvaluator(new[] { predicate });

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate(Page("/Shop", ShopId), 3).Coverage);
    }

    [Fact]
    public void IdentitySelectedDescendant_ExcludedAncestor_IsNotMarked()
    {
        // An exclude takes its whole subtree out, so nothing below it is selected.
        var predicate = Predicate("system", excludes: "/Shop") with { NavigationTags = new() { "Cart" } };
        var evaluator = new ContentCoverageEvaluator(new[] { predicate }, "replace", AncestorsOfCart);

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate(Page("/Shop", ShopId), 3).Coverage);
    }

    [Fact]
    public void IdentitySelectedDescendant_AsksTheTreeOncePerPredicate_AndNotForPathPredicates()
    {
        var calls = new List<string>();
        IReadOnlyCollection<Guid> Walk(ProviderPredicateDefinition p)
        {
            calls.Add(p.Name);
            return new[] { ShopId, ProductsId };
        }
        var evaluator = new ContentCoverageEvaluator(new[]
        {
            Predicate("system") with { ItemTypes = new() { "Swift-v2_Cart" } },
            Predicate("posts", path: "/Posts")
        }, "replace", Walk);

        evaluator.Evaluate(Page("/Shop", ShopId), 3);
        evaluator.Evaluate(Page("/Shop/Products", ProductsId, ShopId), 3);
        evaluator.Evaluate(Page("/Posts", PostsId), 3);

        Assert.Equal(new[] { "system" }, calls);
    }

    [Fact]
    public void IdentitySelectedDescendant_AWalkThatThrows_ReportsNoCoverage()
    {
        var predicate = Predicate("system") with { PageUniqueIds = new() { CartId } };
        var evaluator = new ContentCoverageEvaluator(new[] { predicate }, "replace",
            _ => throw new InvalidOperationException("no DW runtime"));

        Assert.Equal(ContentCoverage.None, evaluator.Evaluate(Page("/Shop", ShopId), 3).Coverage);
    }

    // -------------------------------------------------------------------------
    // GetManagingPredicateNames — page-level inclusion for the editor mode alerts
    // -------------------------------------------------------------------------

    [Fact]
    public void GetManagingPredicateNames_IncludedNode_ReturnsPredicateNames()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate("framework", path: "/", excludes: "/Home") });

        Assert.Equal(new[] { "framework" }, evaluator.GetManagingPredicateNames("/Shop", 3));
    }

    [Fact]
    public void GetManagingPredicateNames_ExcludedNode_IsEmpty()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate("framework", path: "/", excludes: "/Home") });

        Assert.Empty(evaluator.GetManagingPredicateNames("/Home", 3));
        Assert.Empty(evaluator.GetManagingPredicateNames("/Home/Hero", 3));
    }

    [Fact]
    public void GetManagingPredicateNames_ManagedSubtreeBelowUnmanagedNode_IsEmpty()
    {
        // The tree shows Partial ("contains managed subtrees") for this shape; the editor
        // alert must NOT fire — the node's own content is not managed.
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate("posts", path: "/Posts") });

        Assert.Empty(evaluator.GetManagingPredicateNames("/", 3));
        Assert.Equal(ContentCoverage.Partial, evaluator.Evaluate("/", 3).Coverage);
    }

    [Fact]
    public void GetManagingPredicateNames_WrongArea_IsEmpty()
    {
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate("framework") });

        Assert.Empty(evaluator.GetManagingPredicateNames("/Shop", 26));
    }

    [Fact]
    public void GetManagingPredicateNames_PathWithEmbeddedSlashInMenuText_Matches()
    {
        // "Header / Footer" is a literal menu text: matching is pure string prefix with a
        // '/'-boundary, both sides built from the same menu texts — embedded slashes work.
        var evaluator = new ContentCoverageEvaluator(new[] { Predicate("chrome", path: "/Header / Footer") });

        Assert.Equal(new[] { "chrome" }, evaluator.GetManagingPredicateNames("/Header / Footer", 3));
        Assert.Equal(new[] { "chrome" }, evaluator.GetManagingPredicateNames("/Header / Footer/Desktop Header", 3));
        Assert.Empty(evaluator.GetManagingPredicateNames("/Header / Footer 2", 3));
    }
}
