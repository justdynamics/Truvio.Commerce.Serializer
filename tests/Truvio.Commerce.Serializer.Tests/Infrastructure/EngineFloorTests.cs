using System.Reflection;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Serialization;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Content ownership R7, owner decision 4 of 2026-11-13: a mode folder whose documents carry
/// <c>itemLists</c> declares the oldest engine that reads it (<c>minEngineVersion</c> in the
/// mode manifest). An engine through 1.0.9-beta refuses the key (its manifest read is strict);
/// this engine refuses a floor above what it meets; a delivery tool compares the floor with the
/// host's installed engine before it deserializes.
/// </summary>
public class EngineFloorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ManifestWriter _writer = new();

    public EngineFloorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EngineFloor_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ContentEntry Entry() => new()
    {
        EntryId = "content/area-3",
        AreaId = 3,
        AreaName = "Cameron Ashley",
        Path = "/",
        PageId = 0,
        Files = new[] { "_content/Cameron Ashley/area.yml" }
    };

    private string ManifestPath => Path.Combine(_tempDir, "replace-manifest.json");

    // -- the marker -------------------------------------------------------------

    [Fact]
    public void WithoutAFloor_TheManifestIsByteCompatible_NoKeyIsWritten()
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() });

        Assert.DoesNotContain("minEngineVersion", File.ReadAllText(ManifestPath));
        Assert.Null(_writer.Read(_tempDir, "replace")!.MinEngineVersion);
    }

    [Fact]
    public void WithAFloor_TheManifestDeclaresIt_AndThisEngineReadsIt()
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() }, minEngineVersion: ManifestSchema.ItemListsMinEngineVersion);

        Assert.Contains($"\"minEngineVersion\": \"{ManifestSchema.ItemListsMinEngineVersion}\"", File.ReadAllText(ManifestPath));
        Assert.Equal(ManifestSchema.ItemListsMinEngineVersion, _writer.Read(_tempDir, "replace")!.MinEngineVersion);
    }

    [Fact]
    public void TheFloorIsNotCarriedIntoTheNextSerialize()
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() }, minEngineVersion: ManifestSchema.ItemListsMinEngineVersion);

        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() });

        Assert.Null(_writer.Read(_tempDir, "replace")!.MinEngineVersion);
    }

    [Fact]
    public void AFloorAboveThisEngine_IsRefusedAtRead_NamingBothVersions()
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() }, minEngineVersion: "9.0.0");

        var ex = Assert.Throws<InvalidOperationException>(() => _writer.Read(_tempDir, "replace"));

        Assert.Contains("9.0.0 or newer", ex.Message);
        Assert.Contains(ManifestSchema.EngineFloorMet, ex.Message);
    }

    [Fact]
    public void AnUnreadableFloor_IsRefused()
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() }, minEngineVersion: "soon");

        Assert.Throws<InvalidOperationException>(() => _writer.Read(_tempDir, "replace"));
    }

    // -- the floor value ----------------------------------------------------------

    [Fact]
    public void ItemListFloor_IsAboveTheLastEngineThatMisreadsItemLists()
    {
        Assert.True(EngineVersion.Compare(EngineVersion.Parse(ManifestSchema.ItemListsMinEngineVersion), EngineVersion.Parse("1.0.9-beta")) > 0);
    }

    [Fact]
    public void ItemListFloor_IsAtOrBelowThePackageVersion_OnceThePackageVersionMoves()
    {
        // Until the release bumps the package version (still 1.0.9-beta on this branch), a dev
        // build meets the floor by code. Once it moves, the floor must not name a later release.
        var informational = typeof(ManifestSchema).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var package = EngineVersion.Parse(informational);
        if (EngineVersion.Compare(package, EngineVersion.Parse("1.0.9-beta")) <= 0)
            return;
        Assert.True(EngineVersion.Compare(EngineVersion.Parse(ManifestSchema.ItemListsMinEngineVersion), package) <= 0,
            $"ItemListsMinEngineVersion {ManifestSchema.ItemListsMinEngineVersion} is above the package version {informational}.");
    }

    [Theory]
    [InlineData("1.0.9-beta", "1.1.0-beta", -1)]
    [InlineData("1.1.0-beta", "1.1.0", -1)]
    [InlineData("1.1.0", "1.1.0-beta", 1)]
    [InlineData("1.0.10-beta", "1.0.9-beta", 1)]
    [InlineData("1.1.0-beta.2", "1.1.0-beta.10", -1)]
    [InlineData("1.1.0-beta+abc123", "1.1.0-beta", 0)]
    [InlineData("1.1", "1.1.0", 0)]
    [InlineData("1.1.0-alpha", "1.1.0-beta", -1)]
    public void EngineVersion_OrdersAsSemVer(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(EngineVersion.Compare(EngineVersion.Parse(a), EngineVersion.Parse(b))));
    }

    [Fact]
    public void EngineVersion_Max_TakesTheHigherFloor()
    {
        Assert.Null(EngineVersion.Max(null, null));
        Assert.Equal("1.1.0-beta", EngineVersion.Max(null, "1.1.0-beta"));
        Assert.Equal("1.2.0", EngineVersion.Max("1.2.0", "1.1.0-beta"));
    }

    // -- when serialize sets it -------------------------------------------------------

    [Fact]
    public void Orchestrator_DeclaresTheFloor_OnlyWhenADocumentCarriesItemLists()
    {
        Assert.Null(SerializerOrchestrator.EngineFloorFor(new[] { new SerializeResult(), new SerializeResult { DocumentsWithItemLists = 0 } }));
        Assert.Equal(ManifestSchema.ItemListsMinEngineVersion,
            SerializerOrchestrator.EngineFloorFor(new[] { new SerializeResult(), new SerializeResult { DocumentsWithItemLists = 2 } }));
    }

    [Fact]
    public void DocumentsWithItemLists_CountsPagesAndParagraphs_ChildrenAndGridParagraphsIncluded()
    {
        var lists = new Dictionary<string, SerializedItemList> { ["Slides"] = new(Array.Empty<SerializedItemListEntry>()) };
        var gridParagraph = new SerializedParagraph { SortOrder = 1, ParagraphUniqueId = Guid.NewGuid(), ItemLists = lists };
        var pageParagraph = new SerializedParagraph { SortOrder = 1, ParagraphUniqueId = Guid.NewGuid(), ItemLists = lists };
        var plain = new SerializedParagraph { SortOrder = 1, ParagraphUniqueId = Guid.NewGuid() };
        var child = ContentTreeBuilder.BuildSinglePage("Child") with { ItemLists = lists };
        var page = ContentTreeBuilder.BuildSinglePage("Home") with
        {
            Paragraphs = new List<SerializedParagraph> { pageParagraph, plain },
            GridRows = new List<SerializedGridRow>
            {
                new() { Id = Guid.NewGuid(), SortOrder = 1, Columns = new List<SerializedGridColumn> { new() { Id = 1, Paragraphs = new List<SerializedParagraph> { gridParagraph } } } }
            },
            Children = new List<SerializedPage> { child }
        };

        Assert.Equal(3, ContentSerializer.CountDocumentsWithItemLists(new[] { page }));
        Assert.Equal(0, ContentSerializer.CountDocumentsWithItemLists(new[] { ContentTreeBuilder.BuildSinglePage("Plain") }));
    }
}
