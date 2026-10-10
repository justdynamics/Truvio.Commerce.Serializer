using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Content ownership R1 (create-only lifecycle) and R4 (body and single-document stamping):
/// the <c>ownership.onExisting</c> header key, its parsing, the stamp policy's precedence, the YAML
/// round trip, and that a configuration without the new keys writes exactly the headers of before.
/// </summary>
public class ContentOwnershipLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerializerLifecycle_" + Guid.NewGuid().ToString("N")[..8]);

    public ContentOwnershipLifecycleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Header parsing (R1)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("keep", ExistingDocumentLifecycle.Keep)]
    [InlineData("KEEP", ExistingDocumentLifecycle.Keep)]
    [InlineData(" fill ", ExistingDocumentLifecycle.Fill)]
    [InlineData("Overwrite", ExistingDocumentLifecycle.Overwrite)]
    public void ParseLifecycle_KnownValues(string raw, ExistingDocumentLifecycle expected)
    {
        Assert.Equal(expected, DocumentHeader.ParseLifecycle(raw));
        Assert.True(DocumentHeader.IsValidLifecycle(raw));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("1")]
    [InlineData("create-only")]
    public void ParseLifecycle_UnknownValues_AreNullAndInvalid(string raw)
    {
        Assert.Null(DocumentHeader.ParseLifecycle(raw));
        Assert.False(DocumentHeader.IsValidLifecycle(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseLifecycle_Blank_IsUnsetAndValid(string? raw)
    {
        Assert.Null(DocumentHeader.ParseLifecycle(raw));
        Assert.True(DocumentHeader.IsValidLifecycle(raw));
    }

    [Theory]
    // No onExisting: the mode decides, then the pass, exactly as before R1.
    [InlineData("replace", null, ConflictStrategy.DestinationWins, ExistingDocumentLifecycle.Overwrite, ConflictStrategy.SourceWins)]
    [InlineData("merge", null, ConflictStrategy.SourceWins, ExistingDocumentLifecycle.Fill, ConflictStrategy.DestinationWins)]
    [InlineData(null, null, ConflictStrategy.DestinationWins, ExistingDocumentLifecycle.Fill, ConflictStrategy.DestinationWins)]
    // onExisting wins over the mode.
    [InlineData("merge", "overwrite", ConflictStrategy.DestinationWins, ExistingDocumentLifecycle.Overwrite, ConflictStrategy.SourceWins)]
    [InlineData("replace", "fill", ConflictStrategy.SourceWins, ExistingDocumentLifecycle.Fill, ConflictStrategy.DestinationWins)]
    [InlineData("replace", "keep", ConflictStrategy.SourceWins, ExistingDocumentLifecycle.Keep, ConflictStrategy.DestinationWins)]
    // An unknown lifecycle is ignored: the mode decides.
    [InlineData("replace", "seed", ConflictStrategy.DestinationWins, ExistingDocumentLifecycle.Overwrite, ConflictStrategy.SourceWins)]
    public void LifecycleFor_AndStrategyFor_OnExistingWinsOverMode(
        string? mode, string? onExisting, ConflictStrategy pass,
        ExistingDocumentLifecycle expectedLifecycle, ConflictStrategy expectedStrategy)
    {
        var header = mode is null && onExisting is null ? null : new DocumentHeader { Mode = mode, OnExisting = onExisting };
        Assert.Equal(expectedLifecycle, DocumentHeader.LifecycleFor(header, pass));
        Assert.Equal(expectedStrategy, DocumentHeader.StrategyFor(header, pass));
        Assert.Equal(expectedLifecycle == ExistingDocumentLifecycle.Keep, DocumentHeader.IsKeep(header));
    }

    [Fact]
    public void TakeHeaderFromRow_ReadsModeAndOnExisting_AndRemovesTheKey()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ownership"] = new Dictionary<object, object> { ["mode"] = "merge", ["onExisting"] = "keep" },
            ["Id"] = "1"
        };

        var header = DocumentHeader.TakeHeaderFromRow(row);

        Assert.Equal("merge", header?.Mode);
        Assert.Equal("keep", header?.OnExisting);
        Assert.False(row.ContainsKey("ownership"));
    }

    [Fact]
    public void TakeFromRow_StillReturnsTheMode()
    {
        var row = new Dictionary<string, object?>
        {
            ["ownership"] = new Dictionary<object, object> { ["mode"] = "replace", ["onExisting"] = "keep" }
        };
        Assert.Equal(SerializerMode.Replace, DocumentHeader.TakeFromRow(row));
    }

    // -------------------------------------------------------------------------
    // Backward compatibility: no new keys -> the headers of before, byte for byte
    // -------------------------------------------------------------------------

    [Fact]
    public void PredicateWithoutLifecycleKeys_StampsModeOnlyHeaders_LikeTheOldStamp()
    {
        var tree = ContentTreeBuilder.BuildSampleTree();
        var predicate = ContentPredicate("p", SerializerMode.Merge);

        var viaPolicy = DocumentHeader.Stamp(tree, OwnershipStampPolicy.FromPredicate(predicate));
        var viaMode = DocumentHeader.Stamp(tree, SerializerMode.Merge);

        Assert.All(AllHeaders(viaPolicy), h => Assert.Equal(new DocumentHeader { Mode = "merge" }, h));
        Assert.Equal(AllHeaders(viaMode), AllHeaders(viaPolicy));
    }

    [Fact]
    public void ModeOnlyHeader_YamlHasNoOnExistingKey()
    {
        var store = new FileSystemStore();
        store.WriteTree(DocumentHeader.Stamp(ContentTreeBuilder.BuildSampleTree(), SerializerMode.Replace), _root);

        foreach (var file in Directory.GetFiles(_root, "*.yml", SearchOption.AllDirectories))
            Assert.DoesNotContain("onExisting", File.ReadAllText(file));
    }

    // -------------------------------------------------------------------------
    // R1: predicate onExisting is stamped on every document and round-trips
    // -------------------------------------------------------------------------

    [Fact]
    public void PredicateOnExistingKeep_StampsEveryDocument_AndRoundTrips()
    {
        var predicate = ContentPredicate("editorial", SerializerMode.Merge) with { OnExisting = "keep" };
        var stamped = DocumentHeader.Stamp(ContentTreeBuilder.BuildSampleTree(), OwnershipStampPolicy.FromPredicate(predicate));

        var store = new FileSystemStore();
        store.WriteTree(stamped, _root);
        var read = store.ReadTree(_root);

        Assert.All(AllHeaders(read), h =>
        {
            Assert.Equal("merge", h?.Mode);
            Assert.Equal("keep", h?.OnExisting);
        });
        var pageYaml = File.ReadAllText(Path.Combine(_root, "Main Website", "Customer Center", "page.yml"));
        Assert.StartsWith("\"ownership\":", pageYaml);
        Assert.Contains("\"onExisting\": \"keep\"", pageYaml);
    }

    // -------------------------------------------------------------------------
    // R4: body and single-document stamping, most specific rule wins
    // -------------------------------------------------------------------------

    [Fact]
    public void BodyOnExisting_StampsRowsAndParagraphs_NotThePage()
    {
        var predicate = ContentPredicate("shells", SerializerMode.Replace) with
        {
            OnExisting = "overwrite",
            BodyOnExisting = "keep"
        };
        var stamped = DocumentHeader.Stamp(ShellTree(out _, out _, out _), OwnershipStampPolicy.FromPredicate(predicate));
        var page = stamped.Pages[0];

        Assert.Equal(H("replace", "overwrite"), page.Ownership);
        Assert.All(page.GridRows, r => Assert.Equal(H("replace", "keep"), r.Ownership));
        Assert.All(page.GridRows.SelectMany(r => r.Columns).SelectMany(c => c.Paragraphs),
            p => Assert.Equal(H("replace", "keep"), p.Ownership));
        Assert.All(page.Paragraphs, p => Assert.Equal(H("replace", "keep"), p.Ownership));
    }

    [Fact]
    public void ParagraphOverride_IsASlot_InsideAConvergingBody()
    {
        var tree = ShellTree(out var rowId, out var slotId, out var otherId);
        var predicate = ContentPredicate("system", SerializerMode.Replace) with
        {
            OwnershipOverrides = new()
            {
                new OwnershipOverride { ParagraphUniqueId = slotId, Mode = "Merge", OnExisting = "keep" }
            }
        };

        var page = DocumentHeader.Stamp(tree, OwnershipStampPolicy.FromPredicate(predicate)).Pages[0];
        var paragraphs = page.GridRows.Single(r => r.Id == rowId).Columns.SelectMany(c => c.Paragraphs).ToList();

        Assert.Equal(H("merge", "keep"), paragraphs.Single(p => p.ParagraphUniqueId == slotId).Ownership);
        Assert.Equal(H("replace", null), paragraphs.Single(p => p.ParagraphUniqueId == otherId).Ownership);
        Assert.Equal(H("replace", null), page.Ownership);
    }

    [Fact]
    public void RowOverride_CascadesToItsParagraphs_ParagraphOverrideStillWins()
    {
        var tree = ShellTree(out var rowId, out var slotId, out var otherId);
        var predicate = ContentPredicate("system", SerializerMode.Replace) with
        {
            OwnershipOverrides = new()
            {
                new OwnershipOverride { GridRowUniqueId = rowId, OnExisting = "keep" },
                new OwnershipOverride { ParagraphUniqueId = otherId, OnExisting = "overwrite" }
            }
        };

        var row = DocumentHeader.Stamp(tree, OwnershipStampPolicy.FromPredicate(predicate)).Pages[0].GridRows.Single(r => r.Id == rowId);
        var paragraphs = row.Columns.SelectMany(c => c.Paragraphs).ToList();

        Assert.Equal(H("replace", "keep"), row.Ownership);
        Assert.Equal(H("replace", "keep"), paragraphs.Single(p => p.ParagraphUniqueId == slotId).Ownership);
        Assert.Equal(H("replace", "overwrite"), paragraphs.Single(p => p.ParagraphUniqueId == otherId).Ownership);
    }

    [Fact]
    public void PageOverride_WithBodyOnExisting_MakesOnePageAShell_OthersUnchanged()
    {
        var shell = ContentTreeBuilder.BuildSinglePage("Home") with
        {
            GridRows = new() { Row(Guid.NewGuid(), Para(Guid.NewGuid())) }
        };
        var system = ContentTreeBuilder.BuildSinglePage("Cart") with
        {
            GridRows = new() { Row(Guid.NewGuid(), Para(Guid.NewGuid())) }
        };
        var area = Area(shell, system);
        var predicate = ContentPredicate("system", SerializerMode.Replace) with
        {
            OwnershipOverrides = new()
            {
                new OwnershipOverride { PageUniqueId = shell.PageUniqueId, BodyOnExisting = "keep" }
            }
        };

        var stamped = DocumentHeader.Stamp(area, OwnershipStampPolicy.FromPredicate(predicate));
        var home = stamped.Pages.Single(p => p.PageUniqueId == shell.PageUniqueId);
        var cart = stamped.Pages.Single(p => p.PageUniqueId == system.PageUniqueId);

        Assert.Equal(H("replace", null), home.Ownership);
        Assert.Equal(H("replace", "keep"), home.GridRows[0].Ownership);
        Assert.Equal(H("replace", "keep"), home.GridRows[0].Columns[0].Paragraphs[0].Ownership);
        Assert.Equal(H("replace", null), cart.GridRows[0].Ownership);
        Assert.Equal(H("replace", null), cart.GridRows[0].Columns[0].Paragraphs[0].Ownership);
    }

    [Fact]
    public void Overrides_KeyedByMasterGuid_CoverLanguageLayerDocuments()
    {
        var masterPage = Guid.NewGuid();
        var masterParagraph = Guid.NewGuid();
        var layerParagraph = Para(Guid.NewGuid()) with
        {
            Fields = new() { ["MasterParagraphGuid"] = masterParagraph.ToString() }
        };
        var layerPage = ContentTreeBuilder.BuildSinglePage("Inicio") with
        {
            MasterPageGuid = masterPage,
            GridRows = new() { Row(Guid.NewGuid(), layerParagraph) }
        };
        var predicate = ContentPredicate("layer", SerializerMode.Replace) with
        {
            OwnershipOverrides = new()
            {
                new OwnershipOverride { PageUniqueId = masterPage, OnExisting = "keep" },
                new OwnershipOverride { ParagraphUniqueId = masterParagraph, Mode = "Merge", OnExisting = "keep" }
            }
        };

        var page = DocumentHeader.Stamp(Area(layerPage), OwnershipStampPolicy.FromPredicate(predicate)).Pages[0];

        Assert.Equal(H("replace", "keep"), page.Ownership);
        Assert.Equal(H("merge", "keep"), page.GridRows[0].Columns[0].Paragraphs[0].Ownership);
    }

    [Fact]
    public void PageOverride_WithoutBodyKey_BodyInheritsThePageHeader()
    {
        var page = ContentTreeBuilder.BuildSinglePage("Story") with
        {
            GridRows = new() { Row(Guid.NewGuid(), Para(Guid.NewGuid())) }
        };
        var predicate = ContentPredicate("system", SerializerMode.Replace) with
        {
            OwnershipOverrides = new() { new OwnershipOverride { PageUniqueId = page.PageUniqueId, Mode = "Merge", OnExisting = "keep" } }
        };

        var stamped = DocumentHeader.Stamp(Area(page), OwnershipStampPolicy.FromPredicate(predicate)).Pages[0];

        Assert.Equal(H("merge", "keep"), stamped.Ownership);
        Assert.Equal(H("merge", "keep"), stamped.GridRows[0].Ownership);
        Assert.Equal(H("merge", "keep"), stamped.GridRows[0].Columns[0].Paragraphs[0].Ownership);
    }

    // -------------------------------------------------------------------------
    // R1: SqlTable row documents
    // -------------------------------------------------------------------------

    [Fact]
    public void SqlRow_OnExisting_IsWrittenInTheHeader_AndReadBack()
    {
        var store = new FlatFileStore();
        store.WriteRow(_root, "T", "R", new Dictionary<string, object?> { ["Id"] = "R" },
            mode: SerializerMode.Merge, onExisting: ExistingDocumentLifecycle.Keep);

        var text = File.ReadAllText(Path.Combine(_root, "_sql", "T", "R.yml"));
        Assert.Contains("onExisting", text);

        var (row, header) = Assert.Single(store.ReadAllDocumentsWithHeaders(_root, "T", null, null));
        Assert.Equal("merge", header?.Mode);
        Assert.Equal("keep", header?.OnExisting);
        Assert.False(row.ContainsKey("ownership"));

        var (_, mode) = Assert.Single(store.ReadAllDocuments(_root, "T"));
        Assert.Equal(SerializerMode.Merge, mode);
    }

    [Fact]
    public void SqlRow_WithoutOnExisting_HeaderHasModeOnly()
    {
        var store = new FlatFileStore();
        store.WriteRow(_root, "T", "R", new Dictionary<string, object?> { ["Id"] = "R" }, mode: SerializerMode.Replace);

        Assert.DoesNotContain("onExisting", File.ReadAllText(Path.Combine(_root, "_sql", "T", "R.yml")));
    }

    // -------------------------------------------------------------------------
    // R1: what the link pass may touch after a run that kept documents
    // -------------------------------------------------------------------------

    [Fact]
    public void KeptDocuments_KeptPage_OnlyParagraphsTheRunWroteAreResolved()
    {
        var kept = new KeptDocuments();
        kept.PageIds.Add(10);
        kept.WrittenParagraphIds.Add(101);   // an anchor this run converged

        Assert.False(kept.ResolvePageFields(10));
        Assert.True(kept.ResolveParagraph(10, 101));
        Assert.False(kept.ResolveParagraph(10, 102));   // an editor's paragraph
    }

    [Fact]
    public void KeptDocuments_ConvergedPage_EveryParagraphButTheKeptOnesIsResolved()
    {
        var kept = new KeptDocuments();
        kept.ParagraphIds.Add(201);   // a slot

        Assert.True(kept.ResolvePageFields(20));
        Assert.False(kept.ResolveParagraph(20, 201));
        Assert.True(kept.ResolveParagraph(20, 202));   // written or editor-added, as before R1
    }

    [Fact]
    public void KeptDocuments_Empty_ResolvesEverything_AsBefore()
    {
        var kept = new KeptDocuments();
        Assert.True(kept.ResolvePageFields(1));
        Assert.True(kept.ResolveParagraph(1, 2));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DocumentHeader H(string mode, string? onExisting) => new() { Mode = mode, OnExisting = onExisting };

    private static ProviderPredicateDefinition ContentPredicate(string name, SerializerMode mode) => new()
    {
        Name = name,
        ProviderType = "Content",
        Mode = mode,
        AreaId = 1,
        Path = "/"
    };

    private static SerializedArea Area(params SerializedPage[] pages) => new()
    {
        AreaId = Guid.NewGuid(),
        Name = "Main Website",
        SortOrder = 1,
        Pages = pages.ToList()
    };

    private static SerializedGridRow Row(Guid id, params SerializedParagraph[] paragraphs) => new()
    {
        Id = id,
        SortOrder = 1,
        Columns = new() { new SerializedGridColumn { Id = 1, Width = 12, Paragraphs = paragraphs.ToList() } }
    };

    private static SerializedParagraph Para(Guid id) => new() { ParagraphUniqueId = id, SortOrder = 1 };

    private static SerializedArea ShellTree(out Guid rowId, out Guid slotId, out Guid otherId)
    {
        rowId = Guid.NewGuid();
        slotId = Guid.NewGuid();
        otherId = Guid.NewGuid();
        var page = ContentTreeBuilder.BuildSinglePage("Home") with
        {
            GridRows = new() { Row(rowId, Para(slotId), Para(otherId)) },
            Paragraphs = new() { Para(Guid.NewGuid()) }
        };
        return Area(page);
    }

    private static List<DocumentHeader?> AllHeaders(SerializedArea area)
    {
        var headers = new List<DocumentHeader?> { area.Ownership };
        void Walk(SerializedPage page)
        {
            headers.Add(page.Ownership);
            foreach (var row in page.GridRows)
            {
                headers.Add(row.Ownership);
                headers.AddRange(row.Columns.SelectMany(c => c.Paragraphs).Select(p => p.Ownership));
            }
            headers.AddRange(page.Paragraphs.Select(p => p.Ownership));
            foreach (var child in page.Children) Walk(child);
        }
        foreach (var page in area.Pages) Walk(page);
        return headers;
    }
}
