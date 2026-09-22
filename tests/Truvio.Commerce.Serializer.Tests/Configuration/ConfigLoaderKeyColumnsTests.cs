using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// PR #22 review blocker: <c>keyColumns</c> and <c>replaceStrategy</c> were documented as
/// SqlTable predicate fields but the loader's raw DTO had neither, so System.Text.Json dropped
/// them silently and the serialized manifest entry carried an empty key. These tests load both
/// fields from JSON and pin the load-time rejections.
/// </summary>
[Trait("Category", "Issue21")]
public class ConfigLoaderKeyColumnsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _warnings = new();

    public ConfigLoaderKeyColumnsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigLoaderKeyColumns_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        ConfigLoader._testWarningSink.Value = _warnings.Add;
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

    private const string SqlTableBase =
        "\"name\": \"ds\", \"providerType\": \"SqlTable\", \"table\": \"DynamicStructures\"";

    [Fact]
    public void Load_SqlTablePredicate_MapsKeyColumnsAndReplaceStrategy()
    {
        var path = WriteConfig(
            "{ " + SqlTableBase + ", \"mode\": \"Replace\", " +
            "\"keyColumns\": [\"DynamicStructureUniqueId\", \" DynamicStructureName \", \"\"], " +
            "\"replaceStrategy\": \"truncate\" }");

        var p = Assert.Single(ConfigLoader.Load(path, identifierValidator: null).Predicates);

        Assert.Equal(new[] { "DynamicStructureUniqueId", "DynamicStructureName" }, p.KeyColumns);
        Assert.Equal("truncate", p.ReplaceStrategy);
    }

    [Fact]
    public void Load_AbsentFields_DefaultToEmptyAndNull()
    {
        var path = WriteConfig("{ " + SqlTableBase + ", \"mode\": \"Replace\" }");

        var p = Assert.Single(ConfigLoader.Load(path, identifierValidator: null).Predicates);

        Assert.Empty(p.KeyColumns);
        Assert.Null(p.ReplaceStrategy);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Load_UnknownReplaceStrategy_Throws_NamingTheValue()
    {
        var path = WriteConfig("{ " + SqlTableBase + ", \"mode\": \"Replace\", \"replaceStrategy\": \"wipe\" }");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, identifierValidator: null));

        Assert.Contains("'wipe'", ex.Message);
        Assert.Contains("truncate", ex.Message);
    }

    [Fact]
    public void Load_ReplaceStrategyUnderMerge_WarnsAndStillLoads()
    {
        var path = WriteConfig("{ " + SqlTableBase + ", \"mode\": \"Merge\", \"replaceStrategy\": \"truncate\" }");

        var p = Assert.Single(ConfigLoader.Load(path, identifierValidator: null).Predicates);

        Assert.Equal(SerializerMode.Merge, p.Mode);
        Assert.Contains(_warnings, w => w.Contains("'ds'") && w.Contains("ignored") && w.Contains("Merge"));
    }

    [Theory]
    [InlineData("\"keyColumns\": [\"PageUniqueId\"]")]
    [InlineData("\"replaceStrategy\": \"truncate\"")]
    public void Load_SqlTableOnlyFieldOnContentPredicate_Throws(string field)
    {
        var path = WriteConfig(
            "{ \"name\": \"pages\", \"mode\": \"Replace\", \"path\": \"/\", \"areaId\": 1, " + field + " }");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, identifierValidator: null));

        Assert.Contains("'pages'", ex.Message);
        Assert.Contains("SqlTable predicates only", ex.Message);
    }

    [Fact]
    public void Load_MisspelledKeyColumn_FailsIdentifierValidation()
    {
        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DynamicStructures" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DynamicStructureId", "DynamicStructureUniqueId", "DynamicStructureName"
            });
        var path = WriteConfig(
            "{ " + SqlTableBase + ", \"mode\": \"Merge\", \"keyColumns\": [\"DynamicStructureUniqeId\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, validator));

        Assert.Contains("DynamicStructureUniqeId", ex.Message);
    }

    [Fact]
    public void Load_ValidKeyColumn_PassesIdentifierValidation()
    {
        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DynamicStructures" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DynamicStructureUniqueId" });
        var path = WriteConfig(
            "{ " + SqlTableBase + ", \"mode\": \"Merge\", \"keyColumns\": [\"DynamicStructureUniqueId\"] }");

        var p = Assert.Single(ConfigLoader.Load(path, validator).Predicates);

        Assert.Equal(new[] { "DynamicStructureUniqueId" }, p.KeyColumns);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsKeyColumnsAndReplaceStrategy()
    {
        var path = Path.Combine(_tempDir, "Serializer.config.json");
        ConfigWriter.Save(new SerializerConfiguration
        {
            OutputDirectory = _tempDir,
            Predicates = new List<ProviderPredicateDefinition>
            {
                new()
                {
                    Name = "ds", Mode = SerializerMode.Replace, ProviderType = "SqlTable", Table = "DynamicStructures",
                    KeyColumns = new List<string> { "DynamicStructureUniqueId" }, ReplaceStrategy = "truncate"
                }
            }
        }, path);

        var p = Assert.Single(ConfigLoader.Load(path, identifierValidator: null).Predicates);

        Assert.Equal(new[] { "DynamicStructureUniqueId" }, p.KeyColumns);
        Assert.Equal("truncate", p.ReplaceStrategy);
    }
}
