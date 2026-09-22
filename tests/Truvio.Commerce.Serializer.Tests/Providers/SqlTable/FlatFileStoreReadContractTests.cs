using System.Globalization;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Engine issue #20: the directory-read contract for <c>_sql/&lt;Table&gt;/</c>.
/// Order is ordinal by file name (the old <c>OrderBy(f =&gt; f)</c> used the culture-sensitive
/// default comparer, so the order could differ between hosts), and a document the manifest
/// entry's <c>files[]</c> does not name is reported instead of applied unnoticed.
/// </summary>
[Trait("Category", "Issue20")]
public class FlatFileStoreReadContractTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FlatFileStore _store = new();

    public FlatFileStoreReadContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FlatFileStoreReadContract_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempDir, "_sql", "T"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteDoc(string fileName, string name) =>
        File.WriteAllText(Path.Combine(_tempDir, "_sql", "T", fileName), $"Name: {name}\n");

    private List<string> ReadNames(IReadOnlyCollection<string>? manifestFiles = null, Action<string>? log = null) =>
        _store.ReadAllDocuments(_tempDir, "T", manifestFiles, log)
            .Select(d => (string)d.Row["Name"]!)
            .ToList();

    [Theory]
    [InlineData("en-US")]
    [InlineData("da-DK")]
    [InlineData("tr-TR")]
    public void ReadAllDocuments_OrdinalOrder_IsTheSameUnderEveryCulture(string culture)
    {
        // Under the culture-sensitive default comparer "a.yml" sorts before "Z.yml"; ordinal
        // puts uppercase first. The read order must not depend on the host's culture.
        WriteDoc("Z.yml", "upper-z");
        WriteDoc("a.yml", "lower-a");

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(new[] { "upper-z", "lower-a" }, ReadNames());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ReadAllDocuments_SkipsMetaFile()
    {
        WriteDoc("row.yml", "row");
        File.WriteAllText(Path.Combine(_tempDir, "_sql", "T", "_meta.yml"), "TableName: T\n");

        Assert.Equal(new[] { "row" }, ReadNames());
    }

    [Fact]
    public void ReadAllDocuments_DocumentNotNamedByManifestFiles_IsWarnedAbout_AndStillApplied()
    {
        WriteDoc("row.yml", "row");
        WriteDoc("zz-override.yml", "override");

        var lines = new List<string>();
        var names = ReadNames(manifestFiles: new[] { "_sql/T/row.yml" }, log: lines.Add);

        Assert.Equal(new[] { "row", "override" }, names);
        var warning = Assert.Single(lines.Where(l => l.StartsWith("WARNING:", StringComparison.Ordinal)));
        Assert.Contains("zz-override.yml", warning);
        Assert.DoesNotContain("row.yml,", warning);
    }

    [Fact]
    public void ReadAllDocuments_EveryDocumentNamedByManifestFiles_IsSilent()
    {
        WriteDoc("row.yml", "row");
        WriteDoc("zz-override.yml", "override");

        var lines = new List<string>();
        ReadNames(manifestFiles: new[] { "_sql/T/row.yml", "_sql\\T\\zz-override.yml" }, log: lines.Add);

        Assert.Empty(lines);
    }

    [Fact]
    public void ReadAllDocuments_NoManifestFiles_IsSilent()
    {
        WriteDoc("row.yml", "row");

        var lines = new List<string>();
        ReadNames(manifestFiles: null, log: lines.Add);

        Assert.Empty(lines);
    }
}
