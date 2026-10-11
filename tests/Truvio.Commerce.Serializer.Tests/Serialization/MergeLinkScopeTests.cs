using System.Text.RegularExpressions;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R2: a Merge that keeps an existing value must not let the link pass read
/// that value (already a target id) as a source id. Measured on a scratch host: the Swift
/// header's MenuRelatedContent NavigationRoot shipped as ID=50 (source Shop), landed as ID=52,
/// then moved to ID=54 and ID=56 on the next two Merge runs, and the megamenu lost its dropdowns.
/// </summary>
public class MergeLinkScopeTests
{
    // Source page ids collide with target ids the way they did on the host: source 50 (Shop) is
    // target 52, source 52 (Product List) is target 54, source 54 is target 56.
    private static readonly Dictionary<int, int> Map = new() { { 50, 52 }, { 52, 54 }, { 54, 56 } };

    private const string ItemType = "Swift-v2_MenuRelatedContent";
    private const string ItemId = "1";

    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentDeserializer.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>
    /// One link pass over an item, the way ResolveLinksInItemFields runs it: skip a fully kept
    /// item, resolve only the scoped fields, write back the changed ones.
    /// </summary>
    private static void LinkPass(Dictionary<string, object?> item, MergeLinkScope scope)
    {
        var fieldScope = scope.ItemFieldScope(ItemType, ItemId);
        if (fieldScope is { Count: 0 })
            return;
        var changed = ContentDeserializer.ResolveLinkFields(
            MergeLinkScope.Restrict(item, fieldScope), new InternalLinkResolver(Map), k => $"test|{k}");
        foreach (var kvp in changed)
            item[kvp.Key] = kvp.Value;
    }

    [Fact]
    public void MergeTwice_KeepsNavigationRootStable()
    {
        // First delivery creates the paragraph: every field comes from YAML and is resolved.
        var item = new Dictionary<string, object?> { ["NavigationRoot"] = "Default.aspx?ID=50" };
        LinkPass(item, new MergeLinkScope());
        Assert.Equal("Default.aspx?ID=52", item["NavigationRoot"]);

        // Two Merge runs: the field is set on the host, so the fill keeps it.
        for (var run = 0; run < 2; run++)
        {
            var scope = new MergeLinkScope();
            scope.RecordItemFill(ItemType, ItemId, Array.Empty<string>());
            LinkPass(item, scope);
            Assert.Equal("Default.aspx?ID=52", item["NavigationRoot"]);
        }
    }

    [Fact]
    public void WithoutTheScope_AMergeRunDrifts()
    {
        // The defect: the kept value re-read as a source id, once per Merge run.
        var item = new Dictionary<string, object?> { ["NavigationRoot"] = "Default.aspx?ID=52" };
        LinkPass(item, new MergeLinkScope());
        Assert.Equal("Default.aspx?ID=54", item["NavigationRoot"]);
        LinkPass(item, new MergeLinkScope());
        Assert.Equal("Default.aspx?ID=56", item["NavigationRoot"]);
    }

    [Fact]
    public void FilledField_IsResolved_KeptSibling_IsNot()
    {
        var item = new Dictionary<string, object?>
        {
            ["NavigationRoot"] = "Default.aspx?ID=52", // kept: already the target of source 50
            ["Link"] = "Default.aspx?ID=50",           // filled this run from YAML: a source id
        };
        var scope = new MergeLinkScope();
        scope.RecordItemFill(ItemType, ItemId, new[] { "link" });
        LinkPass(item, scope);
        Assert.Equal("Default.aspx?ID=52", item["NavigationRoot"]);
        Assert.Equal("Default.aspx?ID=52", item["Link"]);
    }

    [Fact]
    public void ItemWrittenInFull_HasNoScope()
    {
        var scope = new MergeLinkScope();
        scope.RecordItemFill("Other", "7", new[] { "Title" });
        Assert.Null(scope.ItemFieldScope(ItemType, ItemId));
        Assert.Null(scope.ItemFieldScope(null, ItemId));
    }

    [Fact]
    public void RecordItemFill_AddsUp_CaseInsensitive()
    {
        var scope = new MergeLinkScope();
        scope.RecordItemFill(ItemType, ItemId, new[] { "Link" });
        scope.RecordItemFill(ItemType.ToUpperInvariant(), ItemId, new[] { "Title" });
        var fields = scope.ItemFieldScope(ItemType, ItemId);
        Assert.NotNull(fields);
        Assert.True(fields!.Contains("LINK"));
        Assert.True(fields.Contains("title"));
    }

    [Fact]
    public void KeptPageAndParagraphValues_AreNotResolved()
    {
        var scope = new MergeLinkScope();
        Assert.True(scope.ResolveShortcut(10));
        Assert.True(scope.ResolveProductPage(10));
        Assert.True(scope.ResolveModuleSettings(20));
        scope.KeepShortcut(10);
        scope.KeepProductPage(10);
        scope.KeepModuleSettings(20);
        Assert.False(scope.ResolveShortcut(10));
        Assert.False(scope.ResolveProductPage(10));
        Assert.False(scope.ResolveModuleSettings(20));
        Assert.True(scope.ResolveShortcut(11));
    }

    // -------------------------------------------------------------------------
    // Wiring (needs a live runtime, so pinned against the source)
    // -------------------------------------------------------------------------

    private static int IndexOf(string needle, int from = 0)
    {
        var i = Source.IndexOf(needle, from, StringComparison.Ordinal);
        Assert.True(i >= 0, $"not found: {needle}");
        return i;
    }

    [Fact]
    public void MergeFills_RecordTheirScope_EvenWhenNothingIsFilled()
    {
        // Both item merges record before their "nothing filled" early return.
        foreach (var name in new[] { "private int MergeItemFields(", "private int MergePropertyItemFields(" })
        {
            var start = IndexOf(name);
            var record = IndexOf("_mergeLinkScope.RecordItemFill(", start);
            var early = IndexOf("if (filled == 0) return 0;", start);
            Assert.True(record < early, name);
        }
    }

    [Fact]
    public void PageMerge_RecordsKeptShortcutAndProductPage_BeforeTheFill()
    {
        var keepShortcut = IndexOf("_mergeLinkScope.KeepShortcut(existingId);");
        var keepProduct = IndexOf("_mergeLinkScope.KeepProductPage(existingId);");
        var fill = IndexOf("filled += ApplyPagePropertiesWithMerge(existingPage, dto, ref left);");
        Assert.True(keepShortcut < fill && keepProduct < fill);
    }

    [Fact]
    public void ParagraphMerge_ReadsModuleSettings_BeforeTheFill()
    {
        var start = IndexOf("private void MergeParagraph(");
        var had = IndexOf("var hadModuleSettings = !string.IsNullOrWhiteSpace(existing.ModuleSettings);", start);
        var fill = IndexOf("MergeParagraphScalars(existing, dto, ref left);", start);
        Assert.True(had < fill);
        IndexOf("_mergeLinkScope.KeepModuleSettings(existingParagraphId);", start);
    }

    [Fact]
    public void LinkPass_ConsultsTheScope()
    {
        Assert.Contains("_mergeLinkScope.ResolveShortcut(page.ID)", Source);
        Assert.Contains("_mergeLinkScope.ResolveProductPage(page.ID)", Source);
        Assert.Contains("_mergeLinkScope.ResolveModuleSettings(para.ID)", Source);
        Assert.Equal(2, Regex.Matches(Source, @"ResolveLinkFields\(MergeLinkScope\.Restrict\(fields, scope\)").Count);
        Assert.Equal(2, Regex.Matches(Source, @"if \(scope is \{ Count: 0 \}\)").Count);
    }
}
