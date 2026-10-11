using System.Text.RegularExpressions;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Content ownership R1 wiring in <c>ContentDeserializer</c>. The write paths need a live DW
/// runtime (the integration tests cover them on a host); these structural guards pin the
/// contract that matters: on every UPDATE path (area, page, grid row, paragraph) the keep check
/// runs before anything is written, a kept page or paragraph is recorded for the post-write
/// passes, and both post-write passes (link resolution, master links) consult that record.
/// The decision logic itself is unit-tested through <c>KeptDocuments</c>.
/// </summary>
public class ContentDeserializerKeepTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentDeserializer.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static int IndexOf(string needle, int from = 0)
    {
        var i = Source.IndexOf(needle, from, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{needle}' not found in ContentDeserializer.cs");
        return i;
    }

    [Fact]
    public void Page_KeepCheck_PrecedesTheMergeAndReplaceWrites()
    {
        var keep = IndexOf("ShouldKeep(dto.Ownership, $\"page");
        var load = IndexOf("var existingPage = Services.Pages.GetPage(existingId);");
        var merge = IndexOf("existingPage.ParentPageId = ctx.ParentPageId;");
        Assert.True(keep < load && load < merge);
        Assert.True(IndexOf("_kept.PageIds.Add(existingId);", keep) < load);
    }

    [Fact]
    public void GridRow_KeepCheck_PrecedesTheUpdate()
    {
        var keep = IndexOf("ShouldKeep(dto.Ownership, $\"grid row");
        var update = IndexOf("existingRow2.Sort = dto.SortOrder;");
        Assert.True(keep < update);
    }

    [Fact]
    public void Paragraph_KeepCheck_PrecedesTheUpdate_AndRecordsTheParagraph()
    {
        var keep = IndexOf("ShouldKeep(dto.Ownership, $\"paragraph");
        var update = IndexOf("existingForUpdate.GridRowId = gridRowId;");
        Assert.True(keep < update);
        Assert.True(IndexOf("_kept.ParagraphIds.Add(existingParagraphId);", keep) < update);
    }

    [Fact]
    public void Area_KeepCheck_TurnsOffAreaStateWrites()
    {
        var keep = IndexOf("ShouldKeep(area.Ownership, $\"area");
        var props = IndexOf("WriteAreaProperties(entry.AreaId, area.Properties");
        Assert.True(keep < props);
        Assert.Contains("ownsAreaState = false;", Source.Substring(keep, 400));
    }

    [Fact]
    public void WrittenParagraphs_AreRecorded_OnCreateUpdateAndMergeFill()
    {
        // Create, the Replace UPDATE path and the R2 Merge fill (a filled field can carry a source
        // id, so a merged paragraph on a kept page is still link-resolved).
        Assert.Equal(3, Regex.Matches(Source, @"_kept\.WrittenParagraphIds\.Add\(").Count);
    }

    [Fact]
    public void PostWritePasses_ConsultTheKeptRecord()
    {
        Assert.Contains("ResolveLinksInArea(_entry.AreaId, resolver, entryTargetPageIds, entryOwnsAreaState, _kept);", Source);
        Assert.Contains("RestoreMasterLinks(allAreaPages, allGuidCache, paragraphCache, _kept);", Source);
        Assert.Contains("kept.ResolvePageFields(page.ID)", Source);
        Assert.Contains("kept.ResolveParagraph(page.ID, para.ID)", Source);
        Assert.Contains("kept.PageIds.Contains(update.TargetPageId)", Source);
        Assert.Contains("kept.ParagraphIds.Contains(update.TargetParagraphId)", Source);
    }

    [Fact]
    public void KeptAreaItemFields_AreNotLinkResolved()
    {
        Assert.Contains("&& !_kept.AreaKept;", Source);
    }
}
