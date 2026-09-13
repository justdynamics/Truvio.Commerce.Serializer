using System.IO.Compression;
using System.Text.Json;
using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.Content;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Truvio.Commerce.Serializer.Tests.Fixtures;
using Dynamicweb.CoreUI.Data;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// PackageUnzip: round trips with the zip shapes the engine writes (a PackageDownload content
/// package, a SerializeRoot mode tree), and the rejections that must leave the existing mode
/// folder untouched.
/// </summary>
public class PackageUnzipperTests : IDisposable
{
    private readonly string _work;
    private readonly string _serializerDir;
    private readonly string _replaceRoot;

    public PackageUnzipperTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "truvio-unzip-" + Guid.NewGuid().ToString("N"));
        _serializerDir = Path.Combine(_work, "Serializer");
        _replaceRoot = Path.Combine(_serializerDir, "SerializeRoot", "replace");
        Directory.CreateDirectory(_serializerDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    // -------------------------------------------------------------------------
    // Round trips
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentPackage_RoundTrip_UnzipsUnderContent_WithWholeAreaManifest()
    {
        // The layout PackageBuilder.Build zips: <area>/..., templates.manifest.yml, export.log, _assets/.
        var source = Path.Combine(_work, "package");
        new FileSystemStore().WriteTree(DocumentHeader.Stamp(ContentTreeBuilder.BuildSampleTree(), SerializerMode.Replace), source);
        File.WriteAllText(Path.Combine(source, TemplateAssetManifest.ManifestFileName), "\"references\": []\n");
        File.WriteAllText(Path.Combine(source, "export.log"), "Serializer Export\n");
        Directory.CreateDirectory(Path.Combine(source, PackageBuilder.AssetsFolderName, "Images"));
        File.WriteAllBytes(Path.Combine(source, PackageBuilder.AssetsFolderName, "Images", "logo.png"), new byte[] { 1, 2, 3 });
        var zip = Path.Combine(_work, "package.zip");
        ZipFile.CreateFromDirectory(source, zip);

        var result = PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir, SerializerMode.Replace, areaId: 7);

        var sourceFiles = RelativeFiles(source);
        Assert.Equal(PackageUnzipper.PackageShape.ContentPackage, result.Shape);
        Assert.Equal(sourceFiles.Count, result.FileCount);
        Assert.Equal(sourceFiles.Sum(f => new FileInfo(Path.Combine(source, f)).Length), result.Bytes);
        Assert.Equal(1, result.AssetCount);
        Assert.Equal(Path.GetFullPath(_replaceRoot), result.TargetPath);

        var contentDir = Path.Combine(_replaceRoot, "_content");
        foreach (var file in sourceFiles)
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, file)), File.ReadAllBytes(Path.Combine(contentDir, file)));

        // Deserialize input: the manifest entry and the tree the store reads.
        var manifest = new ManifestWriter().Read(_replaceRoot, "replace");
        Assert.NotNull(manifest);
        var entry = Assert.IsType<ContentEntry>(Assert.Single(manifest!.Entries));
        Assert.Equal(7, entry.AreaId);
        Assert.Equal("/", entry.Path);
        Assert.Equal(0, entry.PageId);
        Assert.Equal(
            ContentProvider.BuildContentEntryForArea(7, source).Files,
            entry.Files.Select(f => f["_content/".Length..]).ToList());

        var store = new FileSystemStore();
        var sourceTree = store.ReadTree(source);
        var unzippedTree = store.ReadTree(contentDir);
        Assert.Equal(JsonSerializer.Serialize(sourceTree), JsonSerializer.Serialize(unzippedTree));
        Assert.Equal("replace", unzippedTree.Pages[0].Ownership?.Mode);
    }

    [Fact]
    public void ModeTree_RoundTrip_IsByteIdentical_AndReplacesStaleFiles()
    {
        var sourceRoot = Path.Combine(_work, "source", "merge");
        var contentDir = Path.Combine(sourceRoot, "_content");
        new FileSystemStore().WriteTree(DocumentHeader.Stamp(ContentTreeBuilder.BuildSampleTree(), SerializerMode.Merge), contentDir);
        new FlatFileStore().WriteRow(sourceRoot, "EcomCountries", "DK",
            new Dictionary<string, object?> { ["CountryCode"] = "DK", ["CountryName"] = "Denmark" }, mode: SerializerMode.Merge);
        new ManifestWriter().Write(sourceRoot, "merge", new[] { ContentProvider.BuildContentEntryForArea(3, sourceRoot) });
        var zip = Path.Combine(_work, "merge.zip");
        ZipFile.CreateFromDirectory(sourceRoot, zip);

        var mergeRoot = Path.Combine(_serializerDir, "SerializeRoot", "merge");
        Directory.CreateDirectory(Path.Combine(mergeRoot, "_content", "Old Area"));
        File.WriteAllText(Path.Combine(mergeRoot, "_content", "Old Area", "area.yml"), "stale");

        var result = PackageUnzipper.Unzip(zip, mergeRoot, _serializerDir, SerializerMode.Merge);

        Assert.Equal(PackageUnzipper.PackageShape.ModeTree, result.Shape);
        Assert.Equal(0, result.AssetCount);
        var sourceFiles = RelativeFiles(sourceRoot);
        Assert.Equal(sourceFiles, RelativeFiles(mergeRoot));
        Assert.Equal(sourceFiles.Count, result.FileCount);
        foreach (var file in sourceFiles)
            Assert.Equal(File.ReadAllBytes(Path.Combine(sourceRoot, file)), File.ReadAllBytes(Path.Combine(mergeRoot, file)));

        Assert.Equal(
            JsonSerializer.Serialize(new ManifestWriter().Read(sourceRoot, "merge")!.Entries, ManifestSchema.ManifestJsonOptions),
            JsonSerializer.Serialize(new ManifestWriter().Read(mergeRoot, "merge")!.Entries, ManifestSchema.ManifestJsonOptions));
        Assert.StartsWith("\"ownership\":", File.ReadAllText(Path.Combine(mergeRoot, "_sql", "EcomCountries", "DK.yml")));
        Assert.Empty(StagingLeftovers());
    }

    [Fact]
    public void DirectoryEntries_AreNotCounted()
    {
        var zip = MakeZip(a =>
        {
            a.CreateEntry("_content/");
            AddEntry(a, "replace-manifest.json", "{}");
            AddEntry(a, "_content/page.yml", "x");
        });

        var result = PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir, SerializerMode.Replace);

        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(_replaceRoot, "_content", "page.yml")));
    }

    // -------------------------------------------------------------------------
    // Rejections: nothing written, existing mode folder kept
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("../../escape.yml")]
    [InlineData("_content/../../escape.yml")]
    [InlineData("..\\..\\escape.yml")]
    [InlineData("/escape.yml")]
    [InlineData("C:/escape.yml")]
    [InlineData("_content/page.yml:hidden")]
    public void PathTraversalEntry_IsRejected_TargetUntouched(string entryName)
    {
        SeedExistingTree();
        var zip = MakeZip(a =>
        {
            AddEntry(a, "replace-manifest.json", "{}");
            AddEntry(a, "_content/ok.yml", "fine");
            AddEntry(a, entryName, "pwned");
        });

        var ex = Assert.Throws<PackageRejectedException>(
            () => PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir, SerializerMode.Replace));

        Assert.Contains("path traversal", ex.Message);
        Assert.False(File.Exists(Path.Combine(_serializerDir, "escape.yml")));
        Assert.False(File.Exists(Path.Combine(_work, "escape.yml")));
        AssertExistingTreeKept();
    }

    [Fact]
    public void ZipOverSizeLimit_IsRejected()
    {
        SeedExistingTree();
        var zip = MakeModeTreeZip();

        var ex = Assert.Throws<PackageRejectedException>(() => PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir,
            SerializerMode.Replace, limits: new PackageUnzipper.Limits(MaxZipBytes: 10, MaxUnzippedBytes: 1_000_000, MaxEntries: 100)));

        Assert.Contains("the limit is 10 bytes", ex.Message);
        AssertExistingTreeKept();
    }

    [Fact]
    public void UnzippedOverSizeLimit_IsRejected()
    {
        SeedExistingTree();
        var zip = MakeZip(a =>
        {
            AddEntry(a, "replace-manifest.json", "{}");
            AddEntry(a, "_content/big.yml", new string('x', 5000));
        });

        var ex = Assert.Throws<PackageRejectedException>(() => PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir,
            SerializerMode.Replace, limits: new PackageUnzipper.Limits(MaxZipBytes: 1_000_000, MaxUnzippedBytes: 1000, MaxEntries: 100)));

        Assert.Contains("unzips to more than 1000 bytes", ex.Message);
        AssertExistingTreeKept();
    }

    [Fact]
    public void TooManyEntries_IsRejected()
    {
        SeedExistingTree();
        var zip = MakeZip(a =>
        {
            AddEntry(a, "replace-manifest.json", "{}");
            for (var i = 0; i < 5; i++)
                AddEntry(a, $"_content/p{i}.yml", "x");
        });

        var ex = Assert.Throws<PackageRejectedException>(() => PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir,
            SerializerMode.Replace, limits: new PackageUnzipper.Limits(MaxZipBytes: 1_000_000, MaxUnzippedBytes: 1_000_000, MaxEntries: 3)));

        Assert.Contains("more than 3 files", ex.Message);
        AssertExistingTreeKept();
    }

    [Theory]
    [InlineData("merge-manifest.json|_content/A/area.yml", 0, "Call PackageUnzip with the Mode that matches")]
    [InlineData("Swift 2/area.yml|Swift 2/Home/page.yml", 0, "Pass AreaId")]
    [InlineData("_content/A/area.yml|_sql/T/R.yml", 0, "without replace-manifest.json at its root")]
    [InlineData("replace/replace-manifest.json|replace/_content/A/area.yml", 0, "not the folder itself")]
    [InlineData("readme.yml|notes/x.yml", 5, "not a serialized tree")]
    [InlineData("replace-manifest.json|readme.txt", 0, "no YAML files")]
    public void NotASerializedTreeForTheMode_IsRejected(string entries, int areaId, string expected)
    {
        SeedExistingTree();
        var zip = MakeZip(a =>
        {
            foreach (var name in entries.Split('|'))
                AddEntry(a, name, "x");
        });

        var ex = Assert.Throws<PackageRejectedException>(
            () => PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir, SerializerMode.Replace, areaId));

        Assert.Contains(expected, ex.Message);
        AssertExistingTreeKept();
    }

    [Fact]
    public void CorruptZip_Throws_TargetUntouched()
    {
        SeedExistingTree();
        var zip = Path.Combine(_work, "corrupt.zip");
        File.WriteAllText(zip, "this is not a zip");

        Assert.ThrowsAny<InvalidDataException>(
            () => PackageUnzipper.Unzip(zip, _replaceRoot, _serializerDir, SerializerMode.Replace));

        AssertExistingTreeKept();
    }

    // -------------------------------------------------------------------------
    // Command gates that run before any host access
    // -------------------------------------------------------------------------

    [Fact]
    public void Command_InvalidMode_ReturnsInvalid()
    {
        var result = new PackageUnzipCommand { Mode = "bogus", FilePath = "/Files/x.zip" }.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Invalid mode", result.Message);
    }

    [Fact]
    public void Command_MissingFilePath_ReturnsInvalid()
    {
        var result = new PackageUnzipCommand { Mode = "merge" }.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.StartsWith("FilePath is required", result.Message);
    }

    [Theory]
    [InlineData("layer.zip", "/Files/System/Serializer/Upload/layer.zip")]
    [InlineData("  /Files/System/Serializer/Upload/layer.zip ", "/Files/System/Serializer/Upload/layer.zip")]
    [InlineData("\\Files\\Other\\layer.zip", "/Files/Other/layer.zip")]
    public void Command_ResolveVirtualPath(string input, string expected)
    {
        Assert.Equal(expected, PackageUnzipCommand.ResolveVirtualPath(input));
    }

    [Theory]
    [InlineData(@"C:\site\Files\System\Serializer\Upload\a.zip", true)]
    [InlineData(@"C:\site\Files", false)]
    [InlineData(@"C:\site\FilesOther\a.zip", false)]
    [InlineData(@"C:\site\web.config", false)]
    public void Command_IsInside_FilesRoot(string path, bool expected)
    {
        Assert.Equal(expected, PackageUnzipCommand.IsInside(path, @"C:\site\Files"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string MakeZip(Action<ZipArchive> build)
    {
        var zipPath = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".zip");
        using var fs = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        build(archive);
        return zipPath;
    }

    private string MakeModeTreeZip() => MakeZip(a =>
    {
        AddEntry(a, "replace-manifest.json", "{}");
        AddEntry(a, "_content/A/area.yml", "x");
    });

    private static void AddEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private void SeedExistingTree()
    {
        Directory.CreateDirectory(Path.Combine(_replaceRoot, "_content", "Existing"));
        File.WriteAllText(Path.Combine(_replaceRoot, "replace-manifest.json"), "existing");
        File.WriteAllText(Path.Combine(_replaceRoot, "_content", "Existing", "area.yml"), "existing");
    }

    private void AssertExistingTreeKept()
    {
        Assert.Equal(
            new[] { "_content/Existing/area.yml", "replace-manifest.json" },
            RelativeFiles(_replaceRoot));
        Assert.Empty(StagingLeftovers());
    }

    private IEnumerable<string> StagingLeftovers() =>
        Directory.GetDirectories(_serializerDir, ".unzip-*");

    private static List<string> RelativeFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
}
