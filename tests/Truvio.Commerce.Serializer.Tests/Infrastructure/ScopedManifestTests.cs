using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

public class ScopedManifestTests
{
    private static ContentEntry Content(string id, string path, params string[] files) => new()
    {
        EntryId = id, AreaId = 1, AreaName = "Site", Path = path, PageId = 0, Files = files
    };

    private static SqlTableEntry Sql(string table, params string[] files) => new()
    {
        EntryId = $"sql/{table}", Table = table, Files = files
    };

    [Fact]
    public void Merge_CoveringContentEntry_GainsScopedFiles()
    {
        var existing = new List<ManifestEntry> { Content("content/area-1", "/", "_content/Site/A/page.yml"), Sql("EcomCountries", "_sql/EcomCountries/DK.yml") };
        var scoped = new[] { Content("content/area-1/A/B", "/A/B", "_content/Site/A/B/page.yml") };

        var merged = ScopedManifest.Merge(existing, scoped);

        Assert.Equal(2, merged.Count);
        var content = Assert.IsType<ContentEntry>(merged[0]);
        Assert.Equal("/", content.Path);
        Assert.Equal(new[] { "_content/Site/A/B/page.yml", "_content/Site/A/page.yml" }, content.Files);
    }

    [Fact]
    public void Merge_SameTable_UnionsFiles_UnrelatedEntryIsAppended()
    {
        var existing = new List<ManifestEntry> { Sql("EcomCountries", "_sql/EcomCountries/DK.yml") };
        var merged = ScopedManifest.Merge(existing, new ManifestEntry[]
        {
            Sql("EcomCountries", "_sql/EcomCountries/SE.yml", "_sql/EcomCountries/DK.yml"),
            Sql("EcomCurrencies", "_sql/EcomCurrencies/EUR.yml")
        });

        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged[0].Files.Count);
        Assert.Equal("sql/EcomCurrencies", merged[1].EntryId);
    }

    [Fact]
    public void Merge_NoExistingManifest_ReturnsScopedEntries()
    {
        var merged = ScopedManifest.Merge(null, new[] { Sql("EcomCountries", "x.yml") });
        Assert.Single(merged);
    }

    private static readonly (string, string)[] AreaPages =
    {
        ("_content/Site/A/page.yml", "/A"),
        ("_content/Site/A/B/page.yml", "/A/B"),
        ("_content/Site/A/B/C/page.yml", "/A/B/C"),
        ("_content/Site/D/page.yml", "/D")
    };

    private static ProviderPredicateDefinition ContentScope(string path, params string[] excludes) => new()
    {
        Name = "scope", ProviderType = "Content", AreaId = 1, Path = path, Excludes = excludes.ToList()
    };

    [Fact]
    public void Select_SqlScope_PicksTableEntries()
    {
        var entries = new List<ManifestEntry> { Sql("EcomCountries"), Sql("EcomCurrencies"), Content("content/area-1", "/") };
        var selected = ScopedManifest.SelectForScope(entries,
            new ProviderPredicateDefinition { Name = "s", ProviderType = "SqlTable", Table = "ecomcountries" },
            _ => AreaPages);

        Assert.Equal("sql/EcomCountries", Assert.Single(selected).EntryId);
    }

    [Fact]
    public void Select_ScopeBelowEntry_NarrowsToScopeFilesWithStubAncestors()
    {
        var entry = Content("content/area-1", "/", AreaPages.Select(p => p.Item1).ToArray());
        var selected = ScopedManifest.SelectForScope(new List<ManifestEntry> { entry }, ContentScope("/A/B"), _ => AreaPages);

        var narrowed = Assert.IsType<ContentEntry>(Assert.Single(selected));
        Assert.Equal("/A/B", narrowed.Path);
        Assert.True(narrowed.StubUnlistedAncestors);
        Assert.Equal(new[] { "_content/Site/A/B/C/page.yml", "_content/Site/A/B/page.yml" }, narrowed.Files);
    }

    [Fact]
    public void Select_EntryInsideScope_PassesUnchanged_ExcludeCutsIn_Narrows()
    {
        var entry = Content("content/area-1/A", "/A", "_content/Site/A/page.yml", "_content/Site/A/B/page.yml", "_content/Site/A/B/C/page.yml");

        var whole = ScopedManifest.SelectForScope(new List<ManifestEntry> { entry }, ContentScope("/"), _ => AreaPages);
        Assert.Same(entry.Files, Assert.IsType<ContentEntry>(Assert.Single(whole)).Files);
        Assert.False(((ContentEntry)whole[0]).StubUnlistedAncestors);

        var cut = ScopedManifest.SelectForScope(new List<ManifestEntry> { entry }, ContentScope("/", "/A/B/C"), _ => AreaPages);
        var narrowed = Assert.IsType<ContentEntry>(Assert.Single(cut));
        Assert.Equal(new[] { "_content/Site/A/B/page.yml", "_content/Site/A/page.yml" }, narrowed.Files);
    }

    [Fact]
    public void Select_DisjointOrOtherArea_IsDropped()
    {
        var entries = new List<ManifestEntry>
        {
            Content("content/area-1/D", "/D", "_content/Site/D/page.yml"),
            Content("content/area-1", "/", "_content/Site/A/page.yml") with { AreaId = 2 }
        };
        Assert.Empty(ScopedManifest.SelectForScope(entries, ContentScope("/A"), _ => AreaPages));
    }

    [Fact]
    public void PagePaths_BuildsMenuTextChain()
    {
        var child = Page("B", "_content/Site/A/B/page.yml");
        var root = Page("A", "_content/Site/A/page.yml") with { Children = new() { child } };

        var paths = ScopedManifest.PagePaths(new[] { root }).ToList();

        Assert.Equal(("_content/Site/A/B/page.yml", "/A/B"), paths[1]);
    }

    internal static SerializedPage Page(string menuText, string? sourceFile) => new()
    {
        PageUniqueId = Guid.NewGuid(), Name = menuText, MenuText = menuText, UrlName = menuText, SortOrder = 1, SourceFile = sourceFile
    };
}
