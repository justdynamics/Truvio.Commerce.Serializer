using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// 1.0.0-beta file format: every document carries its own mode in an <c>ownership</c> header,
/// deserialize honors it with the pass mode as fallback.
/// </summary>
public class DocumentOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerializerOwnership_" + Guid.NewGuid().ToString("N")[..8]);

    public DocumentOwnershipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(SerializerMode.Merge, ConflictStrategy.SourceWins, ConflictStrategy.DestinationWins)]
    [InlineData(SerializerMode.Replace, ConflictStrategy.DestinationWins, ConflictStrategy.SourceWins)]
    [InlineData(null, ConflictStrategy.DestinationWins, ConflictStrategy.DestinationWins)]
    public void StrategyFor_DocumentModeWins_PassIsFallback(SerializerMode? mode, ConflictStrategy pass, ConflictStrategy expected)
    {
        var header = mode is null ? null : DocumentHeader.For(mode.Value);
        Assert.Equal(expected, DocumentHeader.StrategyFor(header, pass));
    }

    [Fact]
    public void SqlRow_HeaderIsWrittenFirst_AndStrippedOnRead()
    {
        var store = new FlatFileStore();
        var row = new Dictionary<string, object?> { ["CountryCode"] = "DK", ["CountryName"] = "Denmark" };

        store.WriteRow(_root, "EcomCountries", "DK", row, mode: SerializerMode.Merge);

        var text = File.ReadAllText(Path.Combine(_root, "_sql", "EcomCountries", "DK.yml"));
        Assert.StartsWith("\"ownership\":", text);

        var (read, readMode) = Assert.Single(store.ReadAllDocuments(_root, "EcomCountries"));
        Assert.Equal(SerializerMode.Merge, readMode);
        Assert.False(read.ContainsKey("ownership"));
        Assert.Equal("Denmark", read["CountryName"]);
    }

    [Fact]
    public void SqlRow_WithoutHeader_HasNoMode_AndScalarOwnershipColumnIsKept()
    {
        var store = new FlatFileStore();
        store.WriteRow(_root, "T", "R", new Dictionary<string, object?> { ["Ownership"] = "customer" });

        var (read, mode) = Assert.Single(store.ReadAllDocuments(_root, "T"));
        Assert.Null(mode);
        Assert.Equal("customer", read["Ownership"]);
    }

    [Fact]
    public void Meta_CarriesHeader()
    {
        var store = new FlatFileStore();
        store.WriteMeta(_root, "T", new TableMetadata
        {
            TableName = "T", KeyColumns = new() { "Id" }, IdentityColumns = new(), AllColumns = new() { "Id" },
            Ownership = DocumentHeader.For(SerializerMode.Replace)
        });

        Assert.Equal("replace", store.ReadMeta(_root, "T").Ownership?.Mode);
    }

    [Fact]
    public void ContentTree_StampedHeaders_RoundTripOnEveryDocument()
    {
        var store = new FileSystemStore();
        var area = DocumentHeader.Stamp(ContentTreeBuilder.BuildSampleTree(), SerializerMode.Merge);

        store.WriteTree(area, _root);
        var read = store.ReadTree(_root);

        Assert.Equal("merge", read.Ownership?.Mode);
        var page = read.Pages[0];
        Assert.Equal("merge", page.Ownership?.Mode);
        Assert.StartsWith("\"ownership\":", File.ReadAllText(Path.Combine(_root, "Main Website", "Customer Center", "page.yml")));
        var row = Assert.IsType<SerializedGridRow>(page.GridRows[0]);
        Assert.Equal("merge", row.Ownership?.Mode);
        Assert.All(row.Columns.SelectMany(c => c.Paragraphs), p => Assert.Equal("merge", p.Ownership?.Mode));
    }

    [Fact]
    public void ScopedWrite_StubAncestor_DoesNotOverwriteExistingFullPage()
    {
        var full = ContentTreeBuilder.BuildSampleTree();
        new FileSystemStore().WriteTree(full, _root);

        var stubArea = full with
        {
            Pages = new() { full.Pages[0] with { IsStructuralStub = true, GridRows = new(), Fields = new() } }
        };
        new FileSystemStore { ResolveExistingPageFolders = true }.WriteTree(stubArea, _root);

        var page = new FileSystemStore().ReadTree(_root).Pages[0];
        Assert.False(page.IsStructuralStub);
        Assert.NotEmpty(page.GridRows);
    }

    [Fact]
    public void ScopedWrite_SameNameOtherPage_GoesToSuffixedFolder()
    {
        var full = ContentTreeBuilder.BuildSampleTree();
        new FileSystemStore().WriteTree(full, _root);

        var other = full.Pages[0] with { PageUniqueId = Guid.NewGuid(), GridRows = new(), Children = new() };
        new FileSystemStore { ResolveExistingPageFolders = true }.WriteTree(full with { Pages = new() { other } }, _root);

        var suffixed = Path.Combine(_root, "Main Website", $"{other.Name} [{other.PageUniqueId.ToString("N")[..6]}]", "page.yml");
        Assert.True(File.Exists(suffixed));
        var readGuids = new FileSystemStore().ReadTree(_root).Pages.Select(p => p.PageUniqueId).ToList();
        Assert.Contains(full.Pages[0].PageUniqueId, readGuids);
        Assert.Contains(other.PageUniqueId, readGuids);
    }

    [Fact]
    public void PruneToEntryFiles_StubUnlistedAncestors_KeepsAncestorAsScalarsOnly()
    {
        var child = ScopedManifestTests.Page("B", "_content/Site/A/B/page.yml");
        var parent = ScopedManifestTests.Page("A", "_content/Site/A/page.yml") with
        {
            Children = new() { child },
            GridRows = new() { new SerializedGridRow { Id = Guid.NewGuid(), SortOrder = 1 } },
            Permissions = new() { new SerializedPermission { Owner = "Editors", OwnerType = "group", Level = "Edit", LevelValue = 4 } }
        };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Site/A/B/page.yml" };

        var stubbed = ContentDeserializer.PruneToEntryFiles(new() { parent }, files, stubUnlistedAncestors: true);
        Assert.True(stubbed[0].IsStructuralStub);
        Assert.Empty(stubbed[0].GridRows);
        Assert.Empty(stubbed[0].Permissions);
        Assert.Single(stubbed[0].Children);

        var legacy = ContentDeserializer.PruneToEntryFiles(new() { parent }, files);
        Assert.False(legacy[0].IsStructuralStub);
        Assert.Single(legacy[0].GridRows);
    }

    [Fact]
    public void MergeTemplateReferences_UnionsByKindAndPath()
    {
        var merged = ContentSerializer.MergeTemplateReferences(
            new[] { new TemplateReference { Kind = "layout", Path = "A.cshtml", ReferencedBy = new() { "p1" } } },
            new[]
            {
                new TemplateReference { Kind = "Layout", Path = "a.cshtml", ReferencedBy = new() { "p2" } },
                new TemplateReference { Kind = "itemType", Path = "Swift_Page", ReferencedBy = new() { "p2" } }
            });

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { "p1", "p2" }, merged[0].ReferencedBy);
    }
}
