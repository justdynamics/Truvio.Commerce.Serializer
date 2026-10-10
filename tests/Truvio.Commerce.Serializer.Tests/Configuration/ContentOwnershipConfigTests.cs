using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Content ownership R1, R3 and R4 configuration keys: <c>onExisting</c>, <c>bodyOnExisting</c>,
/// <c>ownershipOverrides</c>, <c>pageUniqueIds</c> / <c>includeDescendants</c> /
/// <c>navigationTags</c> / <c>itemTypes</c> / <c>excludePageUniqueIds</c>. Load, validation,
/// writer round trip, and the identity matcher shared by serialize and the admin cues.
/// </summary>
public class ContentOwnershipConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _lines = new();

    private static readonly Guid Home = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid About = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Story = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public ContentOwnershipConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigOwnership_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        ConfigLoader._testWarningSink.Value = _lines.Add;
    }

    public void Dispose()
    {
        ConfigLoader._testWarningSink.Value = null;
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string predicateJson)
    {
        var path = Path.Combine(_tempDir, "Serializer.config.json");
        File.WriteAllText(path,
            "{ \"outputDirectory\": \"" + _tempDir.Replace("\\", "\\\\") + "\", " +
            "\"predicates\": [ " + predicateJson + " ] }");
        return path;
    }

    private const string ContentBase = "\"name\": \"editorial\", \"mode\": \"Merge\", \"providerType\": \"Content\", \"areaId\": 3, \"path\": \"/\"";

    // -------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_ReadsEveryOwnershipKey_InCanonicalForm()
    {
        var config = ConfigLoader.Load(WriteConfig("{ " + ContentBase + ", " +
            "\"onExisting\": \"Keep\", \"bodyOnExisting\": \"KEEP\", " +
            $"\"pageUniqueIds\": [\"{Home}\", \"{About}\"], \"includeDescendants\": true, " +
            "\"navigationTags\": [\"Canopy\", \" \"], \"itemTypes\": [\"Swift-v2_Page\"], " +
            $"\"excludePageUniqueIds\": [\"{Story}\"], " +
            $"\"ownershipOverrides\": [ {{ \"paragraphUniqueId\": \"{Story}\", \"mode\": \"Merge\", \"onExisting\": \"keep\" }}, " +
            $"{{ \"pageUniqueId\": \"{Home}\", \"bodyOnExisting\": \"keep\" }} ] }}"), null);

        var p = Assert.Single(config.Predicates);
        Assert.Equal("keep", p.OnExisting);
        Assert.Equal("keep", p.BodyOnExisting);
        Assert.Equal(new[] { Home, About }, p.PageUniqueIds);
        Assert.True(p.IncludeDescendants);
        Assert.Equal(new[] { "Canopy" }, p.NavigationTags);
        Assert.Equal(new[] { "Swift-v2_Page" }, p.ItemTypes);
        Assert.Equal(new[] { Story }, p.ExcludePageUniqueIds);
        Assert.True(p.HasIdentitySelectors);
        Assert.Equal(2, p.OwnershipOverrides.Count);
        Assert.Equal(Story, p.OwnershipOverrides[0].ParagraphUniqueId);
        Assert.Equal("keep", p.OwnershipOverrides[0].OnExisting);
        Assert.Equal(Home, p.OwnershipOverrides[1].PageUniqueId);
        Assert.Equal("keep", p.OwnershipOverrides[1].BodyOnExisting);
    }

    [Fact]
    public void Load_WithoutTheNewKeys_LeavesThemUnset()
    {
        var p = Assert.Single(ConfigLoader.Load(WriteConfig("{ " + ContentBase + " }"), null).Predicates);

        Assert.Null(p.OnExisting);
        Assert.Null(p.BodyOnExisting);
        Assert.Empty(p.OwnershipOverrides);
        Assert.Empty(p.PageUniqueIds);
        Assert.False(p.IncludeDescendants);
        Assert.Empty(p.NavigationTags);
        Assert.Empty(p.ItemTypes);
        Assert.Empty(p.ExcludePageUniqueIds);
        Assert.False(p.HasIdentitySelectors);
    }

    [Fact]
    public void Load_SqlTablePredicate_AcceptsOnExisting()
    {
        var p = Assert.Single(ConfigLoader.Load(WriteConfig(
            "{ \"name\": \"t\", \"mode\": \"Merge\", \"providerType\": \"SqlTable\", \"table\": \"EcomCountries\", \"onExisting\": \"keep\" }"),
            null).Predicates);
        Assert.Equal("keep", p.OnExisting);
    }

    [Theory]
    [InlineData("\"onExisting\": \"seed\"", "onExisting 'seed' is not supported")]
    [InlineData("\"bodyOnExisting\": \"later\"", "bodyOnExisting 'later' is not supported")]
    [InlineData("\"pageUniqueIds\": [\"00000000-0000-0000-0000-000000000000\"]", "pageUniqueIds entry")]
    [InlineData("\"pageUniqueIds\": [\"not-a-guid\"]", "pageUniqueIds entry")]
    [InlineData("\"excludePageUniqueIds\": [\"x\"]", "excludePageUniqueIds entry")]
    [InlineData("\"includeDescendants\": true", "includeDescendants without pageUniqueIds")]
    [InlineData("\"ownershipOverrides\": [ { \"onExisting\": \"keep\" } ]", "exactly one of")]
    [InlineData("\"ownershipOverrides\": [ { \"pageUniqueId\": \"11111111-1111-1111-1111-111111111111\", \"paragraphUniqueId\": \"22222222-2222-2222-2222-222222222222\", \"onExisting\": \"keep\" } ]", "exactly one of")]
    [InlineData("\"ownershipOverrides\": [ { \"paragraphUniqueId\": \"22222222-2222-2222-2222-222222222222\", \"bodyOnExisting\": \"keep\" } ]", "applies to a pageUniqueId entry only")]
    [InlineData("\"ownershipOverrides\": [ { \"paragraphUniqueId\": \"22222222-2222-2222-2222-222222222222\", \"mode\": \"Mirror\" } ]", "mode 'Mirror' is not supported")]
    [InlineData("\"ownershipOverrides\": [ { \"paragraphUniqueId\": \"22222222-2222-2222-2222-222222222222\" } ]", "changes nothing")]
    public void Load_RejectsInvalidOwnershipKeys(string fragment, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigLoader.Load(WriteConfig("{ " + ContentBase + ", " + fragment + " }"), null));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("editorial", ex.Message);
    }

    [Theory]
    [InlineData("\"bodyOnExisting\": \"keep\"")]
    [InlineData("\"pageUniqueIds\": [\"11111111-1111-1111-1111-111111111111\"]")]
    [InlineData("\"navigationTags\": [\"Home\"]")]
    [InlineData("\"ownershipOverrides\": [ { \"pageUniqueId\": \"11111111-1111-1111-1111-111111111111\", \"onExisting\": \"keep\" } ]")]
    public void Load_RejectsContentOnlyKeysOnSqlTable(string fragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(WriteConfig(
            "{ \"name\": \"t\", \"mode\": \"Merge\", \"providerType\": \"SqlTable\", \"table\": \"EcomCountries\", " + fragment + " }"),
            null));
        Assert.Contains("Content predicates only", ex.Message);
    }

    [Fact]
    public void Load_RejectsKeepWithTruncate()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(WriteConfig(
            "{ \"name\": \"t\", \"mode\": \"Replace\", \"providerType\": \"SqlTable\", \"table\": \"EcomCountries\", " +
            "\"onExisting\": \"keep\", \"replaceStrategy\": \"truncate\" }"), null));
        Assert.Contains("cannot keep one", ex.Message);
    }

    [Fact]
    public void ConfigWriter_RoundTripsTheOwnershipKeys()
    {
        var original = ConfigLoader.Load(WriteConfig("{ " + ContentBase + ", \"onExisting\": \"keep\", \"bodyOnExisting\": \"keep\", " +
            $"\"pageUniqueIds\": [\"{Home}\"], \"includeDescendants\": true, \"navigationTags\": [\"Canopy\"], " +
            $"\"excludePageUniqueIds\": [\"{Story}\"], " +
            $"\"ownershipOverrides\": [ {{ \"gridRowUniqueId\": \"{About}\", \"onExisting\": \"overwrite\" }} ] }}"), null);

        var path = Path.Combine(_tempDir, "written.config.json");
        ConfigWriter.Save(original, path);
        var reread = Assert.Single(ConfigLoader.Load(path, null).Predicates);

        Assert.Equal("keep", reread.OnExisting);
        Assert.Equal("keep", reread.BodyOnExisting);
        Assert.Equal(new[] { Home }, reread.PageUniqueIds);
        Assert.True(reread.IncludeDescendants);
        Assert.Equal(new[] { "Canopy" }, reread.NavigationTags);
        Assert.Equal(new[] { Story }, reread.ExcludePageUniqueIds);
        var o = Assert.Single(reread.OwnershipOverrides);
        Assert.Equal(About, o.GridRowUniqueId);
        Assert.Equal("overwrite", o.OnExisting);
    }

    [Fact]
    public void ConfigWriter_PredicateWithoutOwnershipKeys_WritesNoLifecycleKeys()
    {
        var original = ConfigLoader.Load(WriteConfig("{ " + ContentBase + " }"), null);
        var path = Path.Combine(_tempDir, "written.config.json");
        ConfigWriter.Save(original, path);
        var json = File.ReadAllText(path);

        Assert.DoesNotContain("onExisting", json);
        Assert.DoesNotContain("bodyOnExisting", json);
        Assert.DoesNotContain("includeDescendants", json);
    }

    // -------------------------------------------------------------------------
    // R3 matcher
    // -------------------------------------------------------------------------

    private static ProviderPredicateDefinition Predicate() => new()
    {
        Name = "p",
        ProviderType = "Content",
        Mode = SerializerMode.Merge,
        AreaId = 3,
        Path = "/"
    };

    private static ContentPageContext Page(Guid id, string path, string? tag = null, string? itemType = null, params Guid[] ancestors) => new()
    {
        Path = path,
        PageUniqueId = id,
        NavigationTag = tag,
        ItemType = itemType,
        AncestorUniqueIds = ancestors
    };

    [Fact]
    public void Identity_ByGuid_SelectsThePageAndNotItsChildren()
    {
        var p = new ContentPredicate(Predicate() with { PageUniqueIds = new() { About } });

        Assert.Equal(ContentMatch.Included, p.Evaluate(Page(About, "/Home/About", ancestors: Home), 3));
        Assert.Equal(ContentMatch.NotSelected, p.Evaluate(Page(Story, "/Home/About/Story", ancestors: new[] { Home, About }), 3));
        Assert.Equal(ContentMatch.NotSelected, p.Evaluate(Page(Home, "/Home"), 3));
    }

    [Fact]
    public void Identity_ByGuidWithDescendants_SelectsTheSubtree()
    {
        var p = new ContentPredicate(Predicate() with { PageUniqueIds = new() { About }, IncludeDescendants = true });

        Assert.True(p.ShouldInclude(Page(About, "/Home/About", ancestors: Home), 3));
        Assert.True(p.ShouldInclude(Page(Story, "/Home/About/Story", ancestors: new[] { Home, About }), 3));
        Assert.False(p.ShouldInclude(Page(Home, "/Home"), 3));
    }

    [Fact]
    public void Identity_IsNotMovedByARename()
    {
        var p = new ContentPredicate(Predicate() with { PageUniqueIds = new() { About } });

        Assert.True(p.ShouldInclude(Page(About, "/Home/About"), 3));
        Assert.True(p.ShouldInclude(Page(About, "/Home/Who we are"), 3));
    }

    [Fact]
    public void Identity_ByNavigationTagAndItemType_CaseInsensitive_OrCombined()
    {
        var p = new ContentPredicate(Predicate() with
        {
            NavigationTags = new() { "canopy" },
            ItemTypes = new() { "Cabp_Story" }
        });

        Assert.True(p.ShouldInclude(Page(Guid.NewGuid(), "/Home/Canopy", tag: "Canopy"), 3));
        Assert.True(p.ShouldInclude(Page(Guid.NewGuid(), "/Home/X", itemType: "CABP_story"), 3));
        Assert.False(p.ShouldInclude(Page(Guid.NewGuid(), "/Home/Y", tag: "Cart", itemType: "Swift-v2_Page"), 3));
    }

    [Fact]
    public void Identity_ExcludeByGuid_WinsAndCarvesTheSubtree()
    {
        var p = new ContentPredicate(Predicate() with { ExcludePageUniqueIds = new() { About } });

        Assert.Equal(ContentMatch.Excluded, p.Evaluate(Page(About, "/Home/About", ancestors: Home), 3));
        Assert.Equal(ContentMatch.Excluded, p.Evaluate(Page(Story, "/Home/About/Story", ancestors: new[] { Home, About }), 3));
        Assert.Equal(ContentMatch.Included, p.Evaluate(Page(Home, "/Home"), 3));
    }

    [Fact]
    public void Identity_StillLimitedByPathAreaAndPathExcludes()
    {
        var p = new ContentPredicate(Predicate() with
        {
            Path = "/Home",
            Excludes = new() { "/Home/Private" },
            PageUniqueIds = new() { Home, About, Story },
            IncludeDescendants = true
        });

        Assert.Equal(ContentMatch.NotSelected, p.Evaluate(Page(About, "/Other/About"), 3));
        Assert.Equal(ContentMatch.NotSelected, p.Evaluate(Page(About, "/Home/About"), 7));
        Assert.Equal(ContentMatch.Excluded, p.Evaluate(Page(Story, "/Home/Private", ancestors: Home), 3));
    }

    [Fact]
    public void PathOnlyContext_IgnoresIdentitySelectors_AsBefore()
    {
        var p = new ContentPredicate(Predicate() with { PageUniqueIds = new() { About } });
        Assert.True(p.ShouldInclude("/Anything", 3));
    }

    [Fact]
    public void PathPredicate_WithFullContext_BehavesExactlyAsPathOnly()
    {
        var p = new ContentPredicate(Predicate() with { Path = "/Home", Excludes = new() { "/Home/About" } });

        foreach (var (path, expected) in new[] { ("/Home", true), ("/Home/About", false), ("/Home/About/Story", false), ("/Other", false) })
        {
            Assert.Equal(expected, p.ShouldInclude(path, 3));
            Assert.Equal(expected, p.ShouldInclude(Page(Guid.NewGuid(), path), 3));
        }
    }

    [Fact]
    public void IdentityPassThrough_OnlyForUnselectedPagesInsideThePath()
    {
        var predicate = Predicate() with { PageUniqueIds = new() { Story } };

        Assert.True(ContentSerializer.IsIdentityPassThrough(Page(Home, "/Home"), predicate));
        Assert.False(ContentSerializer.IsIdentityPassThrough(Page(Story, "/Home/About/Story"), predicate));

        var excluding = predicate with { ExcludePageUniqueIds = new() { Home } };
        Assert.False(ContentSerializer.IsIdentityPassThrough(Page(Home, "/Home"), excluding));

        var pathOnly = Predicate();
        Assert.False(ContentSerializer.IsIdentityPassThrough(Page(Home, "/Home"), pathOnly));
    }

    [Fact]
    public void CoverageEvaluator_UsesTheIdentityMatcher()
    {
        var evaluator = new ContentCoverageEvaluator(new[]
        {
            Predicate() with { Name = "editorial", PageUniqueIds = new() { About }, IncludeDescendants = true }
        }, "merge");

        Assert.Equal(new[] { "editorial" }, evaluator.GetManagingPredicateNames(Page(Story, "/Home/About/Story", ancestors: new[] { Home, About }), 3));
        Assert.Empty(evaluator.GetManagingPredicateNames(Page(Home, "/Home"), 3));
        Assert.Equal(ContentCoverage.Full, evaluator.Evaluate(Page(About, "/Home/About", ancestors: Home), 3).Coverage);
        Assert.Equal(ContentCoverage.None, evaluator.Evaluate(Page(Home, "/Home"), 3).Coverage);
    }
}
