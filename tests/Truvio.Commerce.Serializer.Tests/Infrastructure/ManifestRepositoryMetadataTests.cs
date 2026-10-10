using System.Text.Json;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Content ownership R15: a mode manifest accepts the ruled repository keys <c>dataClass</c> and
/// <c>systemOfRecord</c> and a free <c>metadata</c> object, carries them through serialize, and
/// ignores them at deserialize. Every other unknown top-level key still fails the strict read.
/// Also R13: serialize refuses documents with an empty unique id.
/// </summary>
public class ManifestRepositoryMetadataTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ManifestWriter _writer = new();

    public ManifestRepositoryMetadataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ManifestMetadata_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>The shape the CABP repository commits: the two ruled keys after the entries.</summary>
    private void WriteRepositoryManifest(string extraKeys)
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() });
        var json = File.ReadAllText(ManifestPath).TrimEnd();
        json = json[..json.LastIndexOf('}')].TrimEnd() + ",\n  " + extraKeys + "\n}";
        File.WriteAllText(ManifestPath, json);
    }

    [Fact]
    public void Read_AcceptsDataClassAndSystemOfRecord()
    {
        WriteRepositoryManifest("\"dataClass\": \"content\",\n  \"systemOfRecord\": \"dynamicweb\"");

        var manifest = _writer.Read(_tempDir, "replace");

        Assert.NotNull(manifest);
        Assert.Equal("content", manifest!.DataClass);
        Assert.Equal("dynamicweb", manifest.SystemOfRecord);
        Assert.Single(manifest.Entries);
    }

    [Fact]
    public void Read_AcceptsAFreeMetadataObject()
    {
        WriteRepositoryManifest("\"metadata\": { \"ownership\": \"cms-editors\", \"ruling\": { \"by\": \"owner\", \"date\": \"2026-10-10\" } }");

        var manifest = _writer.Read(_tempDir, "replace");

        Assert.NotNull(manifest!.Metadata);
        Assert.Equal("cms-editors", manifest.Metadata!["ownership"].GetString());
        Assert.Equal("owner", manifest.Metadata["ruling"].GetProperty("by").GetString());
    }

    [Fact]
    public void Read_StillRejectsAnyOtherUnknownTopLevelKey()
    {
        WriteRepositoryManifest("\"dataClas\": \"content\"");

        Assert.Throws<JsonException>(() => _writer.Read(_tempDir, "replace"));
    }

    [Fact]
    public void Write_CarriesTheRepositoryKeysThroughAReserialize()
    {
        WriteRepositoryManifest("\"dataClass\": \"master\",\n  \"systemOfRecord\": \"erp\",\n  \"metadata\": { \"note\": 1 }");

        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() });

        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        Assert.Equal("master", doc.RootElement.GetProperty("dataClass").GetString());
        Assert.Equal("erp", doc.RootElement.GetProperty("systemOfRecord").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("metadata").GetProperty("note").GetInt32());
        Assert.NotNull(_writer.Read(_tempDir, "replace"));
    }

    [Fact]
    public void Write_WithoutRepositoryKeys_EmitsNoneOfThem()
    {
        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() });

        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        Assert.False(doc.RootElement.TryGetProperty("dataClass", out _));
        Assert.False(doc.RootElement.TryGetProperty("systemOfRecord", out _));
        Assert.False(doc.RootElement.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void Write_OverACorruptPreviousManifest_StillWrites()
    {
        File.WriteAllText(ManifestPath, "{ not json");

        _writer.Write(_tempDir, "replace", new ManifestEntry[] { Entry() });

        Assert.NotNull(_writer.Read(_tempDir, "replace"));
    }

    // -------------------------------------------------------------------------
    // R13: empty unique ids are refused at serialize
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyUniqueIdCheck_NamesEveryEmptyPageRowAndParagraph()
    {
        var page = new SerializedPage
        {
            PageUniqueId = Guid.NewGuid(),
            SourcePageId = 96,
            Name = "Home",
            MenuText = "Home",
            UrlName = "home",
            SortOrder = 1,
            GridRows = new()
            {
                new SerializedGridRow
                {
                    Id = Guid.Empty,
                    SortOrder = 1,
                    Columns = new()
                    {
                        new SerializedGridColumn
                        {
                            Id = 1,
                            Paragraphs = new()
                            {
                                new SerializedParagraph { ParagraphUniqueId = Guid.Empty, SourceParagraphId = 1097, SortOrder = 1 },
                                new SerializedParagraph { ParagraphUniqueId = Guid.NewGuid(), SourceParagraphId = 1098, SortOrder = 2 }
                            }
                        }
                    }
                }
            },
            Paragraphs = new() { new SerializedParagraph { ParagraphUniqueId = Guid.Empty, SourceParagraphId = 5, SortOrder = 1 } },
            Children = new()
            {
                new SerializedPage { PageUniqueId = Guid.Empty, SourcePageId = 98, Name = "About", MenuText = "About", UrlName = "about", SortOrder = 1 }
            }
        };

        var problems = EmptyUniqueIdCheck.Find(new[] { page }, "area 3 'Cameron Ashley'");

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("grid row sort 1") && p.Contains("page ID=96 '/Home'"));
        Assert.Contains(problems, p => p.Contains("paragraph ID=1097"));
        Assert.Contains(problems, p => p.Contains("paragraph ID=5"));
        Assert.Contains(problems, p => p.Contains("page ID=98 '/Home/About'") && p.Contains("PageUniqueId is empty"));
        Assert.DoesNotContain(problems, p => p.Contains("1098"));

        var ex = EmptyUniqueIdCheck.ToException("content", problems);
        Assert.Contains("refused predicate 'content'", ex.Message);
        Assert.Contains("paragraph ID=1097", ex.Message);
    }

    [Fact]
    public void EmptyUniqueIdCheck_CleanTree_HasNoProblems()
    {
        var tree = Truvio.Commerce.Serializer.Tests.Fixtures.ContentTreeBuilder.BuildSampleTree();
        Assert.Empty(EmptyUniqueIdCheck.Find(tree.Pages, "area 1"));
    }

    [Fact]
    public void ContentSerializer_RunsTheEmptyIdCheckBeforeWriting()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Truvio.Commerce.Serializer", "Serialization", "ContentSerializer.cs"));
        var check = source.IndexOf("EmptyUniqueIdCheck.Find", StringComparison.Ordinal);
        var write = source.IndexOf("_store.WriteTree(serializedArea", StringComparison.Ordinal);
        Assert.True(check > 0 && write > check, "The R13 check must run before the tree is written.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Truvio.Commerce.Serializer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
