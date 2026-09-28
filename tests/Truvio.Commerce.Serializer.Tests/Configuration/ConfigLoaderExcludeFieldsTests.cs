using Truvio.Commerce.Serializer.Configuration;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Engine issue #37: an <c>excludeFields</c> entry naming a column the host schema lacks made
/// the whole configuration invalid, so every config load on that host failed (the admin
/// Serializer screens answered HTTP 500 on a blank DW10 database for a DW9-era column such as
/// <c>EcomProducts.MyDouble</c>). Such an entry is satisfied by definition: it is an info line
/// at load. The columns the engine must read keep the strict check.
/// </summary>
[Trait("Category", "Issue37")]
public class ConfigLoaderExcludeFieldsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _lines = new();

    public ConfigLoaderExcludeFieldsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigLoaderExcludeFields_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        ConfigLoader._testWarningSink.Value = _lines.Add;
    }

    public void Dispose()
    {
        ConfigLoader._testWarningSink.Value = null;
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string predicateJson)
    {
        var path = Path.Combine(_tempDir, "Serializer.config.json");
        File.WriteAllText(path,
            "{ \"outputDirectory\": \"" + _tempDir.Replace("\\", "\\\\") + "\", " +
            "\"predicates\": [ " + predicateJson + " ] }");
        return path;
    }

    /// <summary>A blank DW10 host: EcomProducts without the DW9-era test column MyDouble.</summary>
    private static SqlIdentifierValidator BlankHostValidator() => new(
        tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EcomProducts" },
        columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ProductId", "ProductLanguageId", "ProductVariantId", "ProductName", "ProductNumber"
        });

    private const string ProbeBase =
        "\"name\": \"probe\", \"mode\": \"Replace\", \"providerType\": \"SqlTable\", \"table\": \"EcomProducts\"";

    [Fact]
    public void Load_ExcludeFieldAbsentOnHost_Loads_AndKeepsTheEntry()
    {
        var path = WriteConfig("{ " + ProbeBase + ", \"excludeFields\": [\"MyDouble\"] }");

        var config = ConfigLoader.Load(path, BlankHostValidator());

        var p = Assert.Single(config.Predicates);
        Assert.Equal(new[] { "MyDouble" }, p.ExcludeFields);
    }

    [Fact]
    public void Load_ExcludeFieldAbsentOnHost_LogsAnInfoLineNamingTheColumn_NotAWarning()
    {
        var path = WriteConfig("{ " + ProbeBase + ", \"excludeFields\": [\"MyDouble\", \"ProductNumber\"] }");

        ConfigLoader.Load(path, BlankHostValidator());

        var line = Assert.Single(_lines);
        Assert.StartsWith("[Serializer] Info:", line);
        Assert.Contains("MyDouble", line);
        Assert.Contains("[EcomProducts]", line);
        Assert.DoesNotContain("ProductNumber", line);   // present on the host: nothing to say
        Assert.DoesNotContain("WARNING", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ExcludeFieldsAllPresent_LogsNothing()
    {
        var path = WriteConfig("{ " + ProbeBase + ", \"excludeFields\": [\"ProductNumber\"] }");

        ConfigLoader.Load(path, BlankHostValidator());

        Assert.Empty(_lines);
    }

    [Theory]
    [InlineData("\"includeFields\": [\"MyDouble\"]")]
    [InlineData("\"xmlColumns\": [\"MyDouble\"]")]
    [InlineData("\"resolveLinksInColumns\": [\"MyDouble\"]")]
    [InlineData("\"nameColumn\": \"MyDouble\"")]
    [InlineData("\"where\": \"MyDouble = 1\"")]
    public void Load_ColumnsTheEngineReads_StillFailOnAMissingColumn(string field)
    {
        var path = WriteConfig("{ " + ProbeBase + ", " + field + ", \"excludeFields\": [\"MyDouble\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, BlankHostValidator()));

        Assert.Contains("MyDouble", ex.Message);
    }

    [Fact]
    public void Load_MissingTable_StillFails_EvenWithOnlyExcludeFields()
    {
        var path = WriteConfig(
            "{ \"name\": \"probe\", \"mode\": \"Replace\", \"providerType\": \"SqlTable\", " +
            "\"table\": \"NotARealTable\", \"excludeFields\": [\"MyDouble\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, BlankHostValidator()));

        Assert.Contains("NotARealTable", ex.Message);
    }
}
