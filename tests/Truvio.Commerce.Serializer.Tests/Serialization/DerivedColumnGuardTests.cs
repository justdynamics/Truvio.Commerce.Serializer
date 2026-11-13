using System.Reflection;
using Dynamicweb.Content;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// The header, menu text and sort a delivery writes are what the row holds after the run.
/// Dynamicweb's own save re-derives them (the paragraph header and page menu text from the item
/// title, as the item is at the time of the save; a sort of 0 to the page's highest + 1). The
/// fake host below does exactly that on every save; the guard under test is the real one, and the
/// lifecycle's values come from the real Merge fill helpers. Found on the CABP clean room: after
/// two deliveries the dry run still showed 79 headers, 26 menu texts and 2 sorts.
/// </summary>
public class DerivedColumnGuardTests
{
    // -------------------------------------------------------------------------
    // A host whose save behaves as Dynamicweb 10.29.8's does
    // -------------------------------------------------------------------------

    private sealed class FakeHost : IDerivedColumnStore
    {
        public readonly Dictionary<int, ParagraphColumns> Paragraphs = new();
        public readonly Dictionary<int, PageColumns> Pages = new();
        /// <summary>The item title of each paragraph's or page's item, as the item is now.</summary>
        public readonly Dictionary<int, string> ItemTitles = new();
        public int Writes;

        /// <summary>ParagraphService.SaveParagraph: HandleItemStructure, then ParagraphRepository.</summary>
        public void PlatformSaveParagraph(int id, ParagraphColumns onTheObject)
        {
            var header = onTheObject.Header;
            var sort = onTheObject.Sort;
            if (ItemTitles.TryGetValue(id, out var title) && !string.IsNullOrEmpty(title))
                header = title;
            if (sort == 0)
                sort = Paragraphs.Where(p => p.Key != id).Select(p => p.Value.Sort).DefaultIfEmpty(0).Max() + 1;
            Paragraphs[id] = new ParagraphColumns(header, sort);
        }

        /// <summary>PageService.SavePage on an existing page: HandleItemStructure, then PageRepository.UpdatePage.</summary>
        public void PlatformSavePage(int id, Page page)
        {
            if (ItemTitles.TryGetValue(id, out var title) && !string.IsNullOrEmpty(title))
                page.MenuText = title;
            Pages[id] = new PageColumns(page.MenuText ?? string.Empty, page.Sort);
        }

        public ParagraphColumns? ReadParagraph(int paragraphId) =>
            Paragraphs.TryGetValue(paragraphId, out var row) ? row : null;

        public void WriteParagraph(int paragraphId, string? header, int? sort)
        {
            Writes++;
            var row = Paragraphs[paragraphId];
            Paragraphs[paragraphId] = new ParagraphColumns(header ?? row.Header, sort ?? row.Sort);
        }

        public PageColumns? ReadPage(int pageId) =>
            Pages.TryGetValue(pageId, out var row) ? row : null;

        public void WritePage(int pageId, string? menuText, int? sort)
        {
            Writes++;
            var row = Pages[pageId];
            Pages[pageId] = new PageColumns(menuText ?? row.MenuText, sort ?? row.Sort);
        }
    }

    private const int ParagraphId = 87;
    private const int PageId = 195;

    /// <summary>
    /// The guard's own save sequence (<see cref="DerivedColumnGuard.SaveParagraph"/>), with the
    /// fake platform in place of Services: note what the writer put on the object, save, settle.
    /// </summary>
    private static void GuardedSave(DerivedColumnGuard guard, FakeHost host, ParagraphColumns onTheObject)
    {
        host.PlatformSaveParagraph(ParagraphId, onTheObject);
        guard.SettleParagraph(ParagraphId, onTheObject);
    }

    private static void GuardedSave(DerivedColumnGuard guard, FakeHost host, Page page)
    {
        var intended = PageColumns.Of(page);
        host.PlatformSavePage(PageId, page);
        guard.SettlePage(PageId, intended);
    }

    /// <summary>The Replace update path: the document's scalars on the object, save, then the item's fields.</summary>
    private static void DeliverReplace(DerivedColumnGuard guard, FakeHost host, SerializedParagraph dto, string itemTitleInDocument)
    {
        // existingForUpdate.Sort = dto.SortOrder; existingForUpdate.Header = dto.Header; save.
        GuardedSave(guard, host, new ParagraphColumns(dto.Header ?? string.Empty, dto.SortOrder));
        host.ItemTitles[ParagraphId] = itemTitleInDocument;   // SaveItemFields: no paragraph save (SynchronizePages = false)
    }

    private static void DeliverReplace(DerivedColumnGuard guard, FakeHost host, SerializedPage dto, string itemTitleInDocument)
    {
        var row = host.Pages[PageId];
        var page = new Page { MenuText = row.MenuText, Sort = row.Sort };
        page.MenuText = dto.MenuText;
        page.Sort = dto.SortOrder;
        GuardedSave(guard, host, page);
        host.ItemTitles[PageId] = itemTitleInDocument;
    }

    private static SerializedParagraph Para(string? header, int sort = 3, string? template = null) => new()
    {
        ParagraphUniqueId = Guid.Parse("db34714a-16ff-4dc7-9db8-23fa3ed41522"),
        SortOrder = sort,
        Header = header,
        Template = template,
        ItemType = "Swift-v2_Text"
    };

    private static SerializedPage PageDoc(string menuText, int sort = 4) => new()
    {
        PageUniqueId = Guid.Parse("6c5dd7bf-6788-4107-ab78-1b623de21de8"),
        Name = menuText,
        MenuText = menuText,
        UrlName = "carrito",
        SortOrder = sort
    };

    // -------------------------------------------------------------------------
    // Replace (overwrite)
    // -------------------------------------------------------------------------

    [Fact]
    public void ReplaceUpdate_HeaderAndSort_AreTheDocuments_AndASecondDryRunReportsNothing()
    {
        // CABP: the paragraph's header is an editor's label, not its item title, and its sort is 0.
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns("Old title", 85);
        host.Paragraphs[1] = new ParagraphColumns("Other", 84);
        host.ItemTitles[ParagraphId] = "Old title";
        var guard = new DerivedColumnGuard(host);
        var dto = Para("How CABP works", sort: 0);

        DeliverReplace(guard, host, dto, itemTitleInDocument: "From order to oncoming truck.");

        Assert.Equal(new ParagraphColumns("How CABP works", 0), host.Paragraphs[ParagraphId]);
        var row = host.Paragraphs[ParagraphId];
        Assert.Empty(DryRunDiff.ParagraphColumnDiffs(dto.Header, dto.SortOrder, row.Header, row.Sort));

        // Delivery 2 (the item now has the document's title): the row holds the same, and the guard
        // only writes back what the platform's save re-derived again.
        DeliverReplace(guard, host, dto, itemTitleInDocument: "From order to oncoming truck.");
        row = host.Paragraphs[ParagraphId];
        Assert.Equal(new ParagraphColumns("How CABP works", 0), row);
        Assert.Empty(DryRunDiff.ParagraphColumnDiffs(dto.Header, dto.SortOrder, row.Header, row.Sort));
    }

    [Fact]
    public void ReplaceUpdate_WithoutTheGuard_DoesNotConverge()
    {
        // The defect as it was: the save re-derives the header from the item title, so the dry run
        // reports the header (and the renumbered sort) after every delivery.
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns("Old title", 85);
        host.ItemTitles[ParagraphId] = "From order to oncoming truck.";
        var dto = Para("How CABP works", sort: 0);

        host.PlatformSaveParagraph(ParagraphId, new ParagraphColumns(dto.Header!, dto.SortOrder));

        var row = host.Paragraphs[ParagraphId];
        Assert.Equal(
            new[] { "Sort: 1 -> 0", "Header: 'From order to oncoming truck.' -> 'How CABP works'" },
            DryRunDiff.ParagraphColumnDiffs(dto.Header, dto.SortOrder, row.Header, row.Sort));
    }

    [Fact]
    public void ReplaceUpdate_MenuText_IsTheDocuments_AndASecondDryRunReportsNothing()
    {
        // CABP: Spanish pages carry Spanish menu texts over items whose title is still English.
        var host = new FakeHost();
        host.Pages[PageId] = new PageColumns("Cart", 4);
        host.ItemTitles[PageId] = "Cart";
        var guard = new DerivedColumnGuard(host);
        var dto = PageDoc("Carrito");

        DeliverReplace(guard, host, dto, itemTitleInDocument: "Cart");

        var row = host.Pages[PageId];
        Assert.Equal(new PageColumns("Carrito", 4), row);
        Assert.Empty(DryRunDiff.PageColumnDiffs(dto.MenuText, dto.SortOrder, row.MenuText, row.Sort));

        DeliverReplace(guard, host, dto, itemTitleInDocument: "Cart");
        row = host.Pages[PageId];
        Assert.Equal(new PageColumns("Carrito", 4), row);
        Assert.Empty(DryRunDiff.PageColumnDiffs(dto.MenuText, dto.SortOrder, row.MenuText, row.Sort));
    }

    [Fact]
    public void Settle_WritesNothing_WhenTheRowAlreadyHoldsTheValues()
    {
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns("How CABP works", 3);
        host.Pages[PageId] = new PageColumns("Carrito", 4);
        var guard = new DerivedColumnGuard(host);

        Assert.Empty(guard.SettleParagraph(ParagraphId, new ParagraphColumns("How CABP works", 3)));
        Assert.Empty(guard.SettlePage(PageId, new PageColumns("Carrito", 4)));
        Assert.Equal(0, host.Writes);
        Assert.Equal(0, guard.Settled);
    }

    [Fact]
    public void Settle_WritesOnlyTheColumnThatDiffers()
    {
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns("Item title", 3);
        var written = new List<string>();
        var guard = new DerivedColumnGuard(host, written.Add);

        Assert.Equal(new[] { "Header" }, guard.SettleParagraph(ParagraphId, new ParagraphColumns("Label", 3)));
        Assert.Equal(new ParagraphColumns("Label", 3), host.Paragraphs[ParagraphId]);
        Assert.Contains(written, l => l.Contains("Kept Header 'Label' on paragraph ID=87", StringComparison.Ordinal));
        Assert.Equal(1, guard.Settled);
    }

    [Fact]
    public void Settle_AMissingRow_WritesNothing()
    {
        var host = new FakeHost();
        var guard = new DerivedColumnGuard(host);
        Assert.Empty(guard.SettleParagraph(ParagraphId, new ParagraphColumns("x", 1)));
        Assert.Empty(guard.SettlePage(PageId, new PageColumns("x", 1)));
        Assert.Empty(guard.SettleParagraph(0, new ParagraphColumns("x", 1)));
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public void Settle_NullAndEmptyHeader_AreTheSame()
    {
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns(string.Empty, 3);
        var guard = new DerivedColumnGuard(host);
        Assert.Empty(guard.SettleParagraph(ParagraphId, new ParagraphColumns(string.Empty, 3)));
        Assert.Equal(0, host.Writes);
        Assert.Empty(DryRunDiff.ParagraphColumnDiffs(null, 3, string.Empty, 3));
    }

    // -------------------------------------------------------------------------
    // Merge (fill): an editor's value is kept, an empty one is filled
    // -------------------------------------------------------------------------

    /// <summary>
    /// The Merge paragraph path (<c>MergeParagraph</c>): the fill (<see cref="MergeableParagraphFields"/>,
    /// which <c>MergeParagraphScalars</c> applies to the paragraph) over the host's columns, and a
    /// save when it filled anything. The sort is never part of the fill: the object keeps the host's.
    /// </summary>
    private static int DeliverMerge(DerivedColumnGuard guard, FakeHost host, MergeableParagraphFields existing, int existingSort, SerializedParagraph dto)
    {
        var left = 0;
        var filled = existing.Fill(dto, ref left);
        if (filled > 0)
            GuardedSave(guard, host, new ParagraphColumns(existing.Header ?? string.Empty, existingSort));
        return filled;
    }

    [Fact]
    public void MergeUpdate_EditorsHeader_IsKept_AlsoWhenAnotherFillSavesTheParagraph()
    {
        // The editor's header differs from the item title; the fill only adds the template. The
        // platform's save would put the item title in the header: that is not a fill.
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns("Editor's header", 7);
        host.ItemTitles[ParagraphId] = "Item title";
        var guard = new DerivedColumnGuard(host);
        var existing = new MergeableParagraphFields { Header = "Editor's header", ItemType = "Swift-v2_Text", Template = "" };

        var filled = DeliverMerge(guard, host, existing, 7, Para("Document header", sort: 2, template: "Text.cshtml"));

        Assert.Equal(1, filled);
        Assert.Equal(new ParagraphColumns("Editor's header", 7), host.Paragraphs[ParagraphId]);
    }

    [Fact]
    public void MergeUpdate_EditorsSortOfZero_IsKept()
    {
        // Merge never moves a paragraph; the platform's save renumbers a sort of 0.
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns("Editor's header", 0);
        host.Paragraphs[1] = new ParagraphColumns("Other", 40);
        var guard = new DerivedColumnGuard(host);
        var existing = new MergeableParagraphFields { Header = "Editor's header", ItemType = "Swift-v2_Text", Template = "" };

        DeliverMerge(guard, host, existing, 0, Para("Document header", sort: 2, template: "Text.cshtml"));

        Assert.Equal(new ParagraphColumns("Editor's header", 0), host.Paragraphs[ParagraphId]);
    }

    [Fact]
    public void MergeUpdate_EmptyHeader_IsFilledWithTheDocuments()
    {
        var host = new FakeHost();
        host.Paragraphs[ParagraphId] = new ParagraphColumns(string.Empty, 7);
        host.ItemTitles[ParagraphId] = "Item title";
        var guard = new DerivedColumnGuard(host);
        var existing = new MergeableParagraphFields { Header = string.Empty, ItemType = "Swift-v2_Text" };

        var filled = DeliverMerge(guard, host, existing, 7, Para("Document header", sort: 2));

        Assert.Equal(1, filled);
        Assert.Equal(new ParagraphColumns("Document header", 7), host.Paragraphs[ParagraphId]);

        // A second Merge finds the header set and leaves it: nothing filled, nothing saved.
        var again = new MergeableParagraphFields { Header = host.Paragraphs[ParagraphId].Header, ItemType = "Swift-v2_Text" };
        Assert.Equal(0, DeliverMerge(guard, host, again, 7, Para("Document header", sort: 2)));
        Assert.Equal(new ParagraphColumns("Document header", 7), host.Paragraphs[ParagraphId]);
    }

    private static int InvokeMergePageScalars(Page existing, SerializedPage dto, ref int left)
    {
        var m = typeof(ContentDeserializer).GetMethod("MergePageScalars", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);
        var args = new object?[] { existing, dto, left };
        var result = m!.Invoke(null, args);
        left = (int)args[2]!;
        return (int)result!;
    }

    [Fact]
    public void MergeUpdate_EditorsMenuText_IsKept()
    {
        // Merge saves an existing page every time; the platform's save would put the item title in
        // the menu text.
        var host = new FakeHost();
        host.Pages[PageId] = new PageColumns("Editor's menu text", 4);
        host.ItemTitles[PageId] = "Item title";
        var guard = new DerivedColumnGuard(host);
        var existing = new Page { MenuText = "Editor's menu text", Sort = 4 };
        var left = 0;

        InvokeMergePageScalars(existing, PageDoc("Carrito"), ref left);
        GuardedSave(guard, host, existing);

        Assert.Equal(new PageColumns("Editor's menu text", 4), host.Pages[PageId]);
    }

    [Fact]
    public void MergeUpdate_EmptyMenuText_IsFilledWithTheDocuments()
    {
        var host = new FakeHost();
        host.Pages[PageId] = new PageColumns(string.Empty, 4);
        host.ItemTitles[PageId] = "Cart";
        var guard = new DerivedColumnGuard(host);
        var existing = new Page { MenuText = string.Empty, Sort = 4 };
        var left = 0;

        InvokeMergePageScalars(existing, PageDoc("Carrito"), ref left);
        GuardedSave(guard, host, existing);

        Assert.Equal(new PageColumns("Carrito", 4), host.Pages[PageId]);
    }

    // -------------------------------------------------------------------------
    // Keep: the document is not saved, so nothing is written
    // -------------------------------------------------------------------------

    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentDeserializer.cs"));

    private static readonly string LedgerSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "DeferredLinkLedger.cs"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Keep_LeavesHeaderMenuTextAndSortUntouched()
    {
        // A kept paragraph or page returns before any save, so neither the platform nor the guard
        // writes its row (the write paths need a live runtime, so the order is pinned on the source).
        var paragraphKeep = Source.IndexOf("if (ShouldKeep(dto.Ownership, $\"paragraph {dto.ParagraphUniqueId} on page {pageId}\"))", StringComparison.Ordinal);
        var paragraphSave = Source.IndexOf("_derivedColumns.SaveParagraph(existingForUpdate);", StringComparison.Ordinal);
        var paragraphMerge = Source.IndexOf("MergeParagraph(dto, pageId, existingParagraphId, ctx);", StringComparison.Ordinal);
        Assert.True(paragraphKeep > 0 && paragraphSave > paragraphKeep && paragraphMerge > paragraphKeep,
            "The keep check must come before the paragraph's Replace and Merge writes.");
        var keptBranch = Source.Substring(paragraphKeep, paragraphMerge - paragraphKeep);
        Assert.Contains("return;", keptBranch.Substring(0, keptBranch.IndexOf("StrategyFor", StringComparison.Ordinal)));

        var pageKeep = Source.IndexOf("if (ShouldKeep(dto.Ownership, $\"page {dto.PageUniqueId} ('{dto.MenuText}')\"))", StringComparison.Ordinal);
        var pageSave = Source.IndexOf("_derivedColumns.SavePage(existingPage);", StringComparison.Ordinal);
        Assert.True(pageKeep > 0 && pageSave > pageKeep, "The keep check must come before the page's writes.");
        var keptPage = Source.Substring(pageKeep, pageSave - pageKeep);
        Assert.Contains("return existingId;", keptPage.Substring(0, keptPage.IndexOf("Services.Pages.GetPage(existingId)", StringComparison.Ordinal)));
    }

    // -------------------------------------------------------------------------
    // Wiring: no page or paragraph save bypasses the guard
    // -------------------------------------------------------------------------

    [Fact]
    public void EveryContentSave_GoesThroughTheGuard()
    {
        foreach (var source in new[] { Source, LedgerSource })
        {
            Assert.DoesNotContain("Services.Paragraphs.SaveParagraph(", source);
            Assert.DoesNotContain("Services.Pages.SavePage(", source);
        }
        Assert.Contains("_derivedColumns = DerivedColumnGuard.ForHost(log);", Source);
        Assert.Contains("_derivedColumns.SaveParagraph(existingForUpdate);", Source);
        Assert.Contains("_derivedColumns.SaveParagraph(existing);", Source);
        Assert.Contains("_derivedColumns.SaveParagraph(para);", Source);
        Assert.Contains("_derivedColumns.SavePage(existingPage);", Source);
        Assert.Contains("var saved = _derivedColumns.SavePage(page);", Source);
        Assert.Contains("guard.SaveParagraph(para);", LedgerSource);
        Assert.Contains("guard.SavePage(page);", LedgerSource);
    }

    [Fact]
    public void DryRun_ComparesHeaderMenuTextAndSort_ThroughTheSameDiff()
    {
        Assert.Contains("DryRunDiff.ParagraphColumnDiffs(dto.Header, dto.SortOrder, existing.Header, existing.Sort)", Source);
        Assert.Contains("DryRunDiff.PageColumnDiffs(dto.MenuText, dto.SortOrder, existing.MenuText, existing.Sort)", Source);
        Assert.Equal(new[] { "MenuText: 'Cart' -> 'Carrito'", "Sort: 9 -> 4" },
            DryRunDiff.PageColumnDiffs("Carrito", 4, "Cart", 9));
        Assert.Empty(DryRunDiff.PageColumnDiffs(null, 4, string.Empty, 4));
    }
}
