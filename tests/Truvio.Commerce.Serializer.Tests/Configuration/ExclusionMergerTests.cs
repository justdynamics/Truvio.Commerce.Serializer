using Truvio.Commerce.Serializer.Configuration;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

public class ExclusionMergerTests
{
    // -------------------------------------------------------------------------
    // MergeFieldExclusions
    // -------------------------------------------------------------------------

    [Fact]
    public void MergeFieldExclusions_EmptyFlatAndEmptyDict_ReturnsNull()
    {
        var flat = new List<string>();
        var dict = new Dictionary<string, List<string>>();

        var result = ExclusionMerger.MergeFieldExclusions(flat, dict, "Swift_PageItemType");

        Assert.Null(result);
    }

    [Fact]
    public void MergeFieldExclusions_FlatListOnly_ReturnsHashSetWithFlatItems()
    {
        var flat = new List<string> { "NavigationTag", "AreaDomain" };
        var dict = new Dictionary<string, List<string>>();

        var result = ExclusionMerger.MergeFieldExclusions(flat, dict, "Swift_PageItemType");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains("NavigationTag", result);
        Assert.Contains("AreaDomain", result);
    }

    [Fact]
    public void MergeFieldExclusions_DictEntryOnly_ReturnsHashSetWithTypedItems()
    {
        var flat = new List<string>();
        var dict = new Dictionary<string, List<string>>
        {
            ["Swift_PageItemType"] = new List<string> { "Hidden", "TreeSection" }
        };

        var result = ExclusionMerger.MergeFieldExclusions(flat, dict, "Swift_PageItemType");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains("Hidden", result);
        Assert.Contains("TreeSection", result);
    }

    [Fact]
    public void MergeFieldExclusions_BothFlatAndTyped_ReturnsUnion()
    {
        var flat = new List<string> { "NavigationTag", "AreaDomain" };
        var dict = new Dictionary<string, List<string>>
        {
            ["Swift_PageItemType"] = new List<string> { "Hidden", "TreeSection" }
        };

        var result = ExclusionMerger.MergeFieldExclusions(flat, dict, "Swift_PageItemType");

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count);
        Assert.Contains("NavigationTag", result);
        Assert.Contains("AreaDomain", result);
        Assert.Contains("Hidden", result);
        Assert.Contains("TreeSection", result);
    }

    [Fact]
    public void MergeFieldExclusions_DifferentItemType_ReturnsFlatOnly()
    {
        var flat = new List<string> { "NavigationTag", "AreaDomain" };
        var dict = new Dictionary<string, List<string>>
        {
            ["Swift_PageItemType"] = new List<string> { "Hidden", "TreeSection" }
        };

        var result = ExclusionMerger.MergeFieldExclusions(flat, dict, "Other_ItemType");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains("NavigationTag", result);
        Assert.Contains("AreaDomain", result);
    }

    [Fact]
    public void MergeFieldExclusions_CaseInsensitiveDictKeyLookup()
    {
        var flat = new List<string>();
        var dict = new Dictionary<string, List<string>>
        {
            ["Swift_PageItemType"] = new List<string> { "Hidden", "TreeSection" }
        };

        // Lookup with lowercase key
        var result = ExclusionMerger.MergeFieldExclusions(flat, dict, "swift_pageitemtype");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains("Hidden", result);
        Assert.Contains("TreeSection", result);
    }

    // -------------------------------------------------------------------------
    // MergeXmlExclusions
    // -------------------------------------------------------------------------

    [Fact]
    public void MergeXmlExclusions_EmptyFlatAndEmptyDict_ReturnsNull()
    {
        var flat = new List<string>();
        var dict = new Dictionary<string, List<string>>();

        var result = ExclusionMerger.MergeXmlExclusions(flat, dict, "SomeModule");

        Assert.Null(result);
    }

    [Fact]
    public void MergeXmlExclusions_BothFlatAndTyped_ReturnsUnionList()
    {
        var flat = new List<string> { "Settings", "Cache" };
        var dict = new Dictionary<string, List<string>>
        {
            ["ProductListModule"] = new List<string> { "Sorting", "Paging" }
        };

        var result = ExclusionMerger.MergeXmlExclusions(flat, dict, "ProductListModule");

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count);
        Assert.Contains("Settings", result);
        Assert.Contains("Cache", result);
        Assert.Contains("Sorting", result);
        Assert.Contains("Paging", result);
    }

    [Fact]
    public void MergeXmlExclusions_DifferentXmlType_ReturnsFlatOnly()
    {
        var flat = new List<string> { "Settings", "Cache" };
        var dict = new Dictionary<string, List<string>>
        {
            ["ProductListModule"] = new List<string> { "Sorting", "Paging" }
        };

        var result = ExclusionMerger.MergeXmlExclusions(flat, dict, "OtherModule");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains("Settings", result);
        Assert.Contains("Cache", result);
    }

    // -------------------------------------------------------------------------
    // ForItemType: the effective exclusions of one item
    // -------------------------------------------------------------------------

    [Fact]
    public void ForItemType_NoTypedDictionary_ReturnsThePredicateSetAsItIs()
    {
        IReadOnlySet<string> flat = new HashSet<string> { "Title" };

        Assert.Same(flat, ExclusionMerger.ForItemType(flat, null, "Swift-v2_Text"));
        Assert.Null(ExclusionMerger.ForItemType(null, null, "Swift-v2_Text"));
    }

    [Fact]
    public void ForItemType_TypedDictionary_AddsTheTypesEntry_CaseInsensitive()
    {
        IReadOnlySet<string> flat = new HashSet<string> { "Title" };
        var typed = new Dictionary<string, List<string>> { ["Swift-v2_Text"] = new() { "Text" } };

        var result = ExclusionMerger.ForItemType(flat, typed, "swift-v2_text");

        Assert.NotNull(result);
        Assert.True(result!.SetEquals(new[] { "Title", "Text" }));
        Assert.True(result.Contains("text"));
    }

    [Fact]
    public void ForItemType_TypedDictionaryWithoutTheType_IsThePredicateSet_OrNullWhenEmpty()
    {
        var typed = new Dictionary<string, List<string>> { ["Other"] = new() { "Text" } };

        Assert.True(ExclusionMerger.ForItemType(new HashSet<string> { "Title" }, typed, "Swift-v2_Text")!
            .SetEquals(new[] { "Title" }));
        Assert.Null(ExclusionMerger.ForItemType(null, typed, "Swift-v2_Text"));
        Assert.Null(ExclusionMerger.ForItemType(new HashSet<string>(), typed, null));
    }

    [Fact]
    public void ForItemType_MatchesTheInlineExpressionItReplaces()
    {
        // The expression the content deserializer still spells out at its older call sites.
        static IReadOnlySet<string>? Inline(IReadOnlySet<string>? flat, IReadOnlyDictionary<string, List<string>>? typed, string? type) =>
            typed != null
                ? ExclusionMerger.MergeFieldExclusions(flat?.ToList() ?? new List<string>(), typed, type)
                : flat;

        var typedCases = new IReadOnlyDictionary<string, List<string>>?[]
        {
            null,
            new Dictionary<string, List<string>>(),
            new Dictionary<string, List<string>> { ["T"] = new() { "A", "B" } }
        };
        var flatCases = new IReadOnlySet<string>?[] { null, new HashSet<string>(), new HashSet<string> { "A", "C" } };

        foreach (var typed in typedCases)
            foreach (var flat in flatCases)
                foreach (var type in new[] { null, "", "T", "t", "U" })
                {
                    var expected = Inline(flat, typed, type);
                    var actual = ExclusionMerger.ForItemType(flat, typed, type);
                    Assert.Equal(expected is null, actual is null);
                    if (expected is not null)
                        Assert.True(actual!.SetEquals(expected));
                }
    }
}
