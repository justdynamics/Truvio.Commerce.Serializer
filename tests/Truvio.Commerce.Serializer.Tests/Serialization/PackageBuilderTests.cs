using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Serialization;

/// <summary>
/// Scope surgery + asset bundling for PackageDownload. Pure filesystem tests —
/// the serializer itself is exercised by the E2E pipeline.
/// </summary>
public class PackageBuilderTests : IDisposable
{
    private readonly string _root;

    public PackageBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pkgbuilder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeExportTree()
    {
        // <root>/Swift 2/Posts (root page, sourcePageId 100) with one grid row and one child page.
        var posts = Path.Combine(_root, "Swift 2", "Posts");
        Directory.CreateDirectory(posts);
        File.WriteAllText(Path.Combine(_root, "Swift 2", "area.yml"), "\"name\": \"Swift 2\"\n");
        File.WriteAllText(Path.Combine(posts, "page.yml"),
            "\"sourcePageId\": 100\n\"menuText\": \"Posts\"\n\"isStructuralStub\": false\n");

        var gridRow = Path.Combine(posts, "grid-row-1");
        Directory.CreateDirectory(gridRow);
        File.WriteAllText(Path.Combine(gridRow, "grid-row.yml"), "\"name\": \"row\"\n");

        var child = Path.Combine(posts, "Article");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "page.yml"),
            "\"sourcePageId\": 101\n\"menuText\": \"Article\"\n\"isStructuralStub\": false\n");

        return posts;
    }

    [Fact]
    public void ApplyScope_PageAndSubpages_LeavesTreeIntact()
    {
        var posts = MakeExportTree();
        PackageBuilder.ApplyScope(_root, rootPageId: 100, PackageBuilder.ScopePageAndSubpages);

        Assert.True(File.Exists(Path.Combine(posts, "page.yml")));
        Assert.True(File.Exists(Path.Combine(posts, "Article", "page.yml")));
        Assert.True(File.Exists(Path.Combine(posts, "grid-row-1", "grid-row.yml")));
    }

    [Fact]
    public void ApplyScope_PageOnly_DropsChildPages_KeepsGridRows()
    {
        var posts = MakeExportTree();
        PackageBuilder.ApplyScope(_root, rootPageId: 100, PackageBuilder.ScopePageOnly);

        Assert.True(File.Exists(Path.Combine(posts, "page.yml")));
        Assert.False(Directory.Exists(Path.Combine(posts, "Article")));
        Assert.True(File.Exists(Path.Combine(posts, "grid-row-1", "grid-row.yml")));
    }

    [Fact]
    public void ApplyScope_SubpagesOnly_StubsRootPage_DropsItsGridRows()
    {
        var posts = MakeExportTree();
        PackageBuilder.ApplyScope(_root, rootPageId: 100, PackageBuilder.ScopeSubpagesOnly);

        var rootYaml = File.ReadAllText(Path.Combine(posts, "page.yml"));
        Assert.Contains("\"isStructuralStub\": true", rootYaml);
        Assert.False(Directory.Exists(Path.Combine(posts, "grid-row-1")));
        Assert.True(File.Exists(Path.Combine(posts, "Article", "page.yml")));
    }

    [Fact]
    public void ApplyScope_UnknownScope_Throws()
    {
        MakeExportTree();
        Assert.Throws<InvalidOperationException>(
            () => PackageBuilder.ApplyScope(_root, rootPageId: 100, "Bogus"));
    }

    [Fact]
    public void BundleReferencedAssets_CopiesReferencedFiles_SkipsTemplatesSystemAndMissing()
    {
        var posts = MakeExportTree();
        File.WriteAllText(Path.Combine(posts, "grid-row-1", "para.yml"),
            "\"Image\": \"/Files/Images/bikes/hero.jpg\"\n" +
            "\"Doc\": \"/Files/Documents/manual.pdf\"\n" +
            "\"Layout\": \"/Files/Templates/Designs/Swift/foo.cshtml\"\n" +
            "\"Sys\": \"/Files/System/Serializer/whatever.yml\"\n" +
            "\"Gone\": \"/Files/Images/missing.png\"\n");

        var filesRoot = Path.Combine(_root, "filesroot");
        Directory.CreateDirectory(Path.Combine(filesRoot, "Images", "bikes"));
        Directory.CreateDirectory(Path.Combine(filesRoot, "Documents"));
        Directory.CreateDirectory(Path.Combine(filesRoot, "Templates", "Designs", "Swift"));
        File.WriteAllText(Path.Combine(filesRoot, "Images", "bikes", "hero.jpg"), "img");
        File.WriteAllText(Path.Combine(filesRoot, "Documents", "manual.pdf"), "pdf");
        File.WriteAllText(Path.Combine(filesRoot, "Templates", "Designs", "Swift", "foo.cshtml"), "tpl");

        var copied = PackageBuilder.BundleReferencedAssets(_root, filesRoot);

        Assert.Equal(2, copied);
        Assert.True(File.Exists(Path.Combine(_root, "_assets", "Images", "bikes", "hero.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "_assets", "Documents", "manual.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_root, "_assets", "Templates")));
        Assert.False(Directory.Exists(Path.Combine(_root, "_assets", "System")));
    }
}
