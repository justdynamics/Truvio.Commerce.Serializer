using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Foundry #1315: the serializer emitted only paragraphs that matched a grid row, so every
/// paragraph placed directly on a page (<c>ParagraphGridRowId = 0</c>) was dropped silently —
/// stock Swift 2 service pages shipped empty and the header search type-ahead was dead.
///
/// <para>A page with one gridless paragraph must serialize and deserialize to exactly one
/// paragraph with GridRowId 0. The DW-coupled halves (reading <c>GridRowId == 0</c> off the
/// live tree, writing it back through <c>Services.Paragraphs</c>) need a host, so they are
/// asserted at source level here, the same way the other ContentDeserializer suites do; the
/// document round-trip in between is exercised for real.</para>
/// </summary>
public class PageLevelParagraphTests : IDisposable
{
    private readonly FileSystemStore _store = new();
    private readonly string _tempRoot;

    public PageLevelParagraphTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SerializerTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static readonly Guid ParagraphGuid = Guid.NewGuid();

    private static SerializedArea AreaWithOneGridlessParagraph()
    {
        var page = ContentTreeBuilder.BuildSinglePage("Product and content search results") with
        {
            Paragraphs = new List<SerializedParagraph>
            {
                new()
                {
                    ParagraphUniqueId = ParagraphGuid,
                    SourceParagraphId = 1117,
                    SortOrder = 0,
                    Container = "dwcontent",
                    ModuleSystemName = "eCom_ProductCatalog",
                    Header = "Product search dropdown",
                    Fields = new Dictionary<string, object> { ["PageSize"] = "5" }
                }
            }
        };

        return new SerializedArea
        {
            AreaId = Guid.NewGuid(),
            Name = "Swift 2",
            SortOrder = 1,
            Pages = new List<SerializedPage> { page }
        };
    }

    // -----------------------------------------------------------------------
    // Document round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public void WriteThenRead_KeepsExactlyOnePageLevelParagraph()
    {
        _store.WriteTree(AreaWithOneGridlessParagraph(), _tempRoot);

        var readBack = _store.ReadTree(_tempRoot, "Swift 2");

        var page = Assert.Single(readBack.Pages);
        var paragraph = Assert.Single(page.Paragraphs);
        Assert.Equal(ParagraphGuid, paragraph.ParagraphUniqueId);
        Assert.Equal("dwcontent", paragraph.Container);
        Assert.Equal("eCom_ProductCatalog", paragraph.ModuleSystemName);
        Assert.Equal("5", Assert.Contains("PageSize", paragraph.Fields));
        // It is NOT smuggled into a grid row: a synthetic row would be created on the target.
        Assert.Empty(page.GridRows);
    }

    [Fact]
    public void WriteTree_PutsThePageLevelParagraphBesidePageYml_NotInAGridRowFolder()
    {
        _store.WriteTree(AreaWithOneGridlessParagraph(), _tempRoot);

        var pageDir = Path.Combine(_tempRoot, "Swift 2", "Product and content search results");
        Assert.True(File.Exists(Path.Combine(pageDir, "page.yml")));
        var paragraphFile = Assert.Single(Directory.GetFiles(pageDir, "paragraph-*.yml"));
        Assert.Equal("paragraph-p0.yml", Path.GetFileName(paragraphFile));
        Assert.Empty(Directory.GetDirectories(pageDir));
    }

    [Fact]
    public void WriteTree_DropsAStalePageLevelParagraphFromAPreviousRun()
    {
        _store.WriteTree(AreaWithOneGridlessParagraph(), _tempRoot);

        var areaWithout = AreaWithOneGridlessParagraph();
        areaWithout = areaWithout with
        {
            Pages = new List<SerializedPage>
            {
                areaWithout.Pages[0] with { Paragraphs = new List<SerializedParagraph>() }
            }
        };
        _store.WriteTree(areaWithout, _tempRoot);

        var page = Assert.Single(_store.ReadTree(_tempRoot, "Swift 2").Pages);
        Assert.Empty(page.Paragraphs);
    }

    [Fact]
    public void WriteTree_DedupesPageLevelParagraphsThatShareASortOrder()
    {
        // DW's Sort defaults to 0, so several gridless paragraphs on one page routinely collide.
        var area = AreaWithOneGridlessParagraph();
        var first = area.Pages[0].Paragraphs[0];
        var area2 = area with
        {
            Pages = new List<SerializedPage>
            {
                area.Pages[0] with
                {
                    Paragraphs = new List<SerializedParagraph>
                    {
                        first,
                        first with { ParagraphUniqueId = Guid.NewGuid(), SourceParagraphId = 8949 }
                    }
                }
            }
        };

        _store.WriteTree(area2, _tempRoot);

        var page = Assert.Single(_store.ReadTree(_tempRoot, "Swift 2").Pages);
        Assert.Equal(2, page.Paragraphs.Count);
    }

    // -----------------------------------------------------------------------
    // The walkers must see them
    // -----------------------------------------------------------------------

    [Fact]
    public void ParagraphIdCollector_VisitsPageLevelParagraphs()
    {
        var visited = new List<Guid>();
        ParagraphIdCollector.Visit(AreaWithOneGridlessParagraph().Pages, p => visited.Add(p.ParagraphUniqueId));

        Assert.Equal(new[] { ParagraphGuid }, visited);
    }

    [Fact]
    public void BuildSourceToTargetParagraphMap_IncludesPageLevelParagraphs()
    {
        var map = Truvio.Commerce.Serializer.Serialization.InternalLinkResolver
            .BuildSourceToTargetParagraphMap(
                AreaWithOneGridlessParagraph().Pages,
                new Dictionary<Guid, int> { { ParagraphGuid, 22410 } });

        Assert.Equal(22410, Assert.Contains(1117, map));
    }

    [Fact]
    public void DocumentHeader_StampsPageLevelParagraphs()
    {
        var stamped = DocumentHeader.Stamp(AreaWithOneGridlessParagraph(), SerializerMode.Replace);

        var paragraph = Assert.Single(stamped.Pages[0].Paragraphs);
        Assert.NotNull(paragraph.Ownership);
    }

    // -----------------------------------------------------------------------
    // Engine halves that need a DW host — asserted at source level
    // -----------------------------------------------------------------------

    private static string ReadSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Truvio.Commerce.Serializer", relativePath));
    }

    [Fact]
    public void ContentSerializer_SelectsTheGridlessParagraphs_AndLogsTheCount()
    {
        var source = ReadSource(Path.Combine("Serialization", "ContentSerializer.cs"));

        Assert.Contains("p.GridRowId == 0", source);
        Assert.Contains("Page-level paragraphs (GridRowId 0)", source);
        Assert.Contains("Paragraphs = serializedPageParagraphs", source);
    }

    [Fact]
    public void ContentDeserializer_WritesThemBackWithGridRowZero_AndLogsTheCount()
    {
        var source = ReadSource(Path.Combine("Serialization", "ContentDeserializer.cs"));

        Assert.Contains("DeserializePageLevelParagraphs", source);
        Assert.Contains("gridRowId: 0", source);
        Assert.Contains("Page-level paragraphs (GridRowId 0) for page", source);
    }

    [Fact]
    public void ContentMapper_CarriesTheParagraphContainer()
    {
        Assert.Contains("Container = paragraph.Container",
            ReadSource(Path.Combine("Serialization", "ContentMapper.cs")));
    }
}
