using Dynamicweb.Content;
using Dynamicweb.Core;
using Dynamicweb.Data;

namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>The paragraph columns Dynamicweb re-derives when a paragraph is saved.</summary>
internal readonly record struct ParagraphColumns(string Header, int Sort)
{
    public static ParagraphColumns Of(Paragraph paragraph) => new(paragraph.Header ?? string.Empty, paragraph.Sort);
}

/// <summary>The page columns Dynamicweb re-derives when a page is saved.</summary>
internal readonly record struct PageColumns(string MenuText, int Sort)
{
    public static PageColumns Of(Page page) => new(page.MenuText ?? string.Empty, page.Sort);
}

/// <summary>Reads and writes the re-derived columns of one row, bypassing the platform's save.</summary>
internal interface IDerivedColumnStore
{
    ParagraphColumns? ReadParagraph(int paragraphId);

    /// <summary>Writes the given columns (null = leave the column alone).</summary>
    void WriteParagraph(int paragraphId, string? header, int? sort);

    PageColumns? ReadPage(int pageId);

    /// <summary>Writes the given columns (null = leave the column alone).</summary>
    void WritePage(int pageId, string? menuText, int? sort);
}

/// <summary>
/// Keeps the header, menu text and sort the writer set on a paragraph or page, when the platform's
/// own save replaces them.
/// <para>
/// Dynamicweb 10 re-derives three columns on every save, from state the writer does not control
/// (read from the 10.29.8 package, and seen on the CABP clean room):
/// <list type="bullet">
/// <item><c>ParagraphService.SaveParagraph</c> → <c>HandleItemStructure</c> sets
/// <c>Paragraph.Header</c> to the item's title (<c>ItemService.GetTitle</c>) whenever that title is
/// not empty. The item is read as it is at the time of the save, so on an existing paragraph the
/// header becomes the title of the item <i>before</i> the run writes its fields, and any header
/// that is not the item title never sticks.</item>
/// <item><c>PageService.SavePage</c> → <c>HandleItemStructure</c> does the same for
/// <c>Page.MenuText</c> (not for template pages).</item>
/// <item><c>ParagraphRepository</c> replaces a <c>Sort</c> of 0 with the page's highest sort + 1
/// on insert and on update; <c>PageRepository</c> does so on insert.</item>
/// </list>
/// The item save the writer does afterwards (<c>new ItemContext()</c>, so
/// <c>SynchronizePages = false</c>) does not touch the paragraph or page row.
/// </para>
/// <para>
/// Every paragraph and page save of the content writer goes through this guard: it notes the
/// three values the writer put on the object before the save, lets the platform save, and writes
/// back by plain SQL whichever of them the save changed. What the writer put on the object is
/// already the lifecycle's answer: the document's value under Replace (overwrite), the host's
/// value or the document's value into an empty column under Merge (fill), and nothing at all under
/// Keep (a kept document is never saved). Saves that only rewrite a link keep what the row held.
/// </para>
/// </summary>
internal sealed class DerivedColumnGuard
{
    private readonly IDerivedColumnStore _store;
    private readonly Action<string>? _log;

    public DerivedColumnGuard(IDerivedColumnStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log;
    }

    /// <summary>A guard over the host's own database.</summary>
    public static DerivedColumnGuard ForHost(Action<string>? log = null) => new(new DwDerivedColumnStore(), log);

    /// <summary>Rows whose columns the guard wrote back in this run.</summary>
    public int Settled { get; private set; }

    /// <summary>
    /// Writes back the columns of paragraph <paramref name="paragraphId"/> that differ from
    /// <paramref name="intended"/>. Returns the names of the columns written (empty: the row
    /// already held them).
    /// </summary>
    public IReadOnlyList<string> SettleParagraph(int paragraphId, ParagraphColumns intended)
    {
        if (paragraphId <= 0) return Array.Empty<string>();
        var row = _store.ReadParagraph(paragraphId);
        if (row is null) return Array.Empty<string>();

        var header = DryRunDiff.SameText(row.Value.Header, intended.Header) ? null : intended.Header;
        int? sort = row.Value.Sort == intended.Sort ? null : intended.Sort;
        if (header is null && sort is null) return Array.Empty<string>();

        _store.WriteParagraph(paragraphId, header, sort);
        Settled++;
        var written = new List<string>(2);
        if (header is not null)
        {
            written.Add("Header");
            _log?.Invoke($"  Kept Header '{intended.Header}' on paragraph ID={paragraphId} (the platform's save had set '{row.Value.Header}' from the item title)");
        }
        if (sort is not null)
        {
            written.Add("Sort");
            _log?.Invoke($"  Kept Sort {intended.Sort} on paragraph ID={paragraphId} (the platform's save had renumbered it to {row.Value.Sort})");
        }
        return written;
    }

    /// <summary>
    /// Writes back the columns of page <paramref name="pageId"/> that differ from
    /// <paramref name="intended"/>. Returns the names of the columns written.
    /// </summary>
    public IReadOnlyList<string> SettlePage(int pageId, PageColumns intended)
    {
        if (pageId <= 0) return Array.Empty<string>();
        var row = _store.ReadPage(pageId);
        if (row is null) return Array.Empty<string>();

        var menuText = DryRunDiff.SameText(row.Value.MenuText, intended.MenuText) ? null : intended.MenuText;
        int? sort = row.Value.Sort == intended.Sort ? null : intended.Sort;
        if (menuText is null && sort is null) return Array.Empty<string>();

        _store.WritePage(pageId, menuText, sort);
        Settled++;
        var written = new List<string>(2);
        if (menuText is not null)
        {
            written.Add("MenuText");
            _log?.Invoke($"  Kept MenuText '{intended.MenuText}' on page ID={pageId} (the platform's save had set '{row.Value.MenuText}' from the item title)");
        }
        if (sort is not null)
        {
            written.Add("Sort");
            _log?.Invoke($"  Kept Sort {intended.Sort} on page ID={pageId} (the platform's save had renumbered it to {row.Value.Sort})");
        }
        return written;
    }

    /// <summary>
    /// Saves <paramref name="paragraph"/> through the platform and keeps the header and sort it
    /// carried. The object (and the one the platform returns) holds them again afterwards, so a
    /// later save of the same object keeps them too.
    /// </summary>
    public Paragraph SaveParagraph(Paragraph paragraph)
    {
        var intended = ParagraphColumns.Of(paragraph);
        var saved = Services.Paragraphs.SaveParagraph(paragraph) ?? paragraph;
        SettleParagraph(saved.ID > 0 ? saved.ID : paragraph.ID, intended);
        Restore(paragraph, intended);
        if (!ReferenceEquals(saved, paragraph)) Restore(saved, intended);
        return saved;
    }

    /// <summary>
    /// Saves <paramref name="page"/> through the platform (languages skipped, as every content
    /// write does) and keeps the menu text and sort it carried.
    /// </summary>
    public Page SavePage(Page page)
    {
        var intended = PageColumns.Of(page);
        var saved = Services.Pages.SavePage(page, skipLanguages: true) ?? page;
        SettlePage(saved.ID > 0 ? saved.ID : page.ID, intended);
        Restore(page, intended);
        if (!ReferenceEquals(saved, page)) Restore(saved, intended);
        return saved;
    }

    private static void Restore(Paragraph paragraph, ParagraphColumns intended)
    {
        if (!DryRunDiff.SameText(paragraph.Header, intended.Header)) paragraph.Header = intended.Header;
        if (paragraph.Sort != intended.Sort) paragraph.Sort = intended.Sort;
    }

    private static void Restore(Page page, PageColumns intended)
    {
        if (!DryRunDiff.SameText(page.MenuText, intended.MenuText)) page.MenuText = intended.MenuText;
        if (page.Sort != intended.Sort) page.Sort = intended.Sort;
    }
}

/// <summary>
/// The Dynamicweb half of <see cref="DerivedColumnGuard"/>: plain SQL on <c>Paragraph</c> and
/// <c>Page</c>, then the row's service cache entry is cleared (the cache refreshes it from the
/// database), so later reads in the same run see what the row holds.
/// </summary>
internal sealed class DwDerivedColumnStore : IDerivedColumnStore
{
    public ParagraphColumns? ReadParagraph(int paragraphId)
    {
        using var reader = Database.CreateDataReader(CommandBuilder.Create(
            "SELECT [ParagraphHeader], [ParagraphSort] FROM [Paragraph] WHERE [ParagraphID] = {0}", paragraphId));
        if (!reader.Read()) return null;
        return new ParagraphColumns(
            reader.IsDBNull(0) ? string.Empty : Converter.ToString(reader.GetValue(0)),
            reader.IsDBNull(1) ? 0 : Converter.ToInt32(reader.GetValue(1)));
    }

    public void WriteParagraph(int paragraphId, string? header, int? sort)
    {
        if (header is not null)
            Database.ExecuteNonQuery(CommandBuilder.Create(
                "UPDATE [Paragraph] SET [ParagraphHeader] = {0} WHERE [ParagraphID] = {1}", header, paragraphId));
        if (sort is not null)
            Database.ExecuteNonQuery(CommandBuilder.Create(
                "UPDATE [Paragraph] SET [ParagraphSort] = {0} WHERE [ParagraphID] = {1}", sort.Value, paragraphId));
        Services.Paragraphs.ClearCache(paragraphId);
    }

    public PageColumns? ReadPage(int pageId)
    {
        using var reader = Database.CreateDataReader(CommandBuilder.Create(
            "SELECT [PageMenuText], [PageSort] FROM [Page] WHERE [PageID] = {0}", pageId));
        if (!reader.Read()) return null;
        return new PageColumns(
            reader.IsDBNull(0) ? string.Empty : Converter.ToString(reader.GetValue(0)),
            reader.IsDBNull(1) ? 0 : Converter.ToInt32(reader.GetValue(1)));
    }

    public void WritePage(int pageId, string? menuText, int? sort)
    {
        if (menuText is not null)
            Database.ExecuteNonQuery(CommandBuilder.Create(
                "UPDATE [Page] SET [PageMenuText] = {0} WHERE [PageID] = {1}", menuText, pageId));
        if (sort is not null)
            Database.ExecuteNonQuery(CommandBuilder.Create(
                "UPDATE [Page] SET [PageSort] = {0} WHERE [PageID] = {1}", sort.Value, pageId));
        Services.Pages.ClearCache(pageId);
    }
}
