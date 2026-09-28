using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Foundry #1322: <c>raiseOnlyColumns</c> on a SqlTable predicate. Every listed column must exist
/// on the host table (strict, like nameColumn), must be a numeric SQL type, and must not be a key
/// column. A violation is a config error naming the column; an empty list equals absent.
/// </summary>
[Trait("Category", "Foundry1322")]
public class ConfigLoaderRaiseOnlyColumnsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _lines = new();

    public ConfigLoaderRaiseOnlyColumnsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigLoaderRaiseOnly_" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>EcomNumbers as a DW10 host has it: NumberId is the primary key.</summary>
    private static SqlIdentifierValidator NumbersHostValidator() => new(
        tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EcomNumbers" },
        columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NumberId", "NumberDescription", "NumberCounter", "NumberPrefix"
        },
        columnTypeLoader: _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NumberId"] = "nvarchar",
            ["NumberDescription"] = "nvarchar",
            ["NumberCounter"] = "int",
            ["NumberPrefix"] = "nvarchar"
        },
        primaryKeyLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NumberId" });

    private const string NumbersBase =
        "\"name\": \"EcomNumbers\", \"mode\": \"Merge\", \"providerType\": \"SqlTable\", \"table\": \"EcomNumbers\"";

    [Fact]
    public void Load_ValidRaiseOnlyColumn_MapsTheField()
    {
        var path = WriteConfig("{ " + NumbersBase + ", \"raiseOnlyColumns\": [\"NumberCounter\", \" \"] }");

        var p = Assert.Single(ConfigLoader.Load(path, NumbersHostValidator()).Predicates);

        Assert.Equal(new[] { "NumberCounter" }, p.RaiseOnlyColumns);
        Assert.Empty(_lines);
    }

    [Fact]
    public void Load_AbsentOrEmptyList_IsEmpty()
    {
        var absent = Assert.Single(ConfigLoader.Load(WriteConfig("{ " + NumbersBase + " }"), NumbersHostValidator()).Predicates);
        Assert.Empty(absent.RaiseOnlyColumns);

        var empty = Assert.Single(ConfigLoader.Load(
            WriteConfig("{ " + NumbersBase + ", \"raiseOnlyColumns\": [] }"), NumbersHostValidator()).Predicates);
        Assert.Empty(empty.RaiseOnlyColumns);
    }

    [Fact]
    public void Load_MissingColumn_IsAConfigError()
    {
        var path = WriteConfig("{ " + NumbersBase + ", \"raiseOnlyColumns\": [\"NumberCountr\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, NumbersHostValidator()));

        Assert.Contains("raiseOnlyColumns", ex.Message);
        Assert.Contains("[EcomNumbers].[NumberCountr]", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Load_NonNumericColumn_IsAConfigError()
    {
        var path = WriteConfig("{ " + NumbersBase + ", \"raiseOnlyColumns\": [\"NumberPrefix\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, NumbersHostValidator()));

        Assert.Contains("[EcomNumbers].[NumberPrefix]", ex.Message);
        Assert.Contains("'nvarchar'", ex.Message);
        Assert.Contains("numeric", ex.Message);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("smallint")]
    [InlineData("tinyint")]
    [InlineData("decimal")]
    [InlineData("numeric")]
    [InlineData("float")]
    [InlineData("real")]
    public void Load_EveryNumericType_IsAccepted(string sqlType)
    {
        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EcomNumbers" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NumberId", "NumberCounter" },
            columnTypeLoader: _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NumberId"] = "nvarchar",
                ["NumberCounter"] = sqlType
            },
            primaryKeyLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NumberId" });
        var path = WriteConfig("{ " + NumbersBase + ", \"raiseOnlyColumns\": [\"NumberCounter\"] }");

        var p = Assert.Single(ConfigLoader.Load(path, validator).Predicates);

        Assert.Equal(new[] { "NumberCounter" }, p.RaiseOnlyColumns);
    }

    [Fact]
    public void Load_PrimaryKeyColumn_IsAConfigError()
    {
        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EcomNumbers" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NumberId", "NumberCounter" },
            columnTypeLoader: _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NumberId"] = "int",
                ["NumberCounter"] = "int"
            },
            primaryKeyLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NumberId" });
        var path = WriteConfig("{ " + NumbersBase + ", \"raiseOnlyColumns\": [\"NumberId\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, validator));

        Assert.Contains("[EcomNumbers].[NumberId]", ex.Message);
        Assert.Contains("key column", ex.Message);
    }

    [Theory]
    [InlineData("\"nameColumn\": \"NumberCounter\"")]
    [InlineData("\"keyColumns\": [\"NumberCounter\"]")]
    public void Load_DeclaredKeyColumn_IsAConfigError(string keyDeclaration)
    {
        var path = WriteConfig("{ " + NumbersBase + ", " + keyDeclaration + ", \"raiseOnlyColumns\": [\"NumberCounter\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, NumbersHostValidator()));

        Assert.Contains("[EcomNumbers].[NumberCounter]", ex.Message);
        Assert.Contains("key column", ex.Message);
    }

    [Fact]
    public void Load_OnContentPredicate_Throws()
    {
        var path = WriteConfig(
            "{ \"name\": \"pages\", \"mode\": \"Replace\", \"path\": \"/\", \"areaId\": 1, " +
            "\"raiseOnlyColumns\": [\"PageId\"] }");

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path, identifierValidator: null));

        Assert.Contains("'pages'", ex.Message);
        Assert.Contains("raiseOnlyColumns", ex.Message);
        Assert.Contains("SqlTable predicates only", ex.Message);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRaiseOnlyColumns()
    {
        var path = Path.Combine(_tempDir, "Serializer.config.json");
        ConfigWriter.Save(new SerializerConfiguration
        {
            OutputDirectory = _tempDir,
            Predicates = new List<ProviderPredicateDefinition>
            {
                new()
                {
                    Name = "EcomNumbers", Mode = SerializerMode.Merge, ProviderType = "SqlTable", Table = "EcomNumbers",
                    RaiseOnlyColumns = new List<string> { "NumberCounter" }
                }
            }
        }, path);

        Assert.Contains("\"raiseOnlyColumns\"", File.ReadAllText(path));
        var p = Assert.Single(ConfigLoader.Load(path, NumbersHostValidator()).Predicates);
        Assert.Equal(new[] { "NumberCounter" }, p.RaiseOnlyColumns);
    }
}
