using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Tests.TestHelpers;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Phase 40 (D-01..D-04) flat-shape ConfigLoader tests. Every test fixture uses the new
/// flat shape — single top-level <c>predicates</c> array with per-entry <c>mode</c>. Tests
/// covering the legacy section shape behaviors (Replace/Merge sections, legacy migration,
/// section-level rejection) live in <see cref="ReplaceModeConfigLoaderTests"/>.
/// </summary>
public class ConfigLoaderTests : ConfigLoaderValidatorFixtureBase
{
    private readonly string _tempDir;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigLoaderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfigFile(string json)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    // -------------------------------------------------------------------------
    // Valid config loading
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_ValidConfig_ReturnsSerializerConfiguration()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "logLevel": "info",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1,
                  "excludes": ["/Customer Center/Archive"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal("/serialization", config.OutputDirectory);
        Assert.Single(config.Predicates);
        Assert.Equal("Customer Center", config.Predicates[0].Name);
        Assert.Equal(SerializerMode.Replace, config.Predicates[0].Mode);
        Assert.Equal("/Customer Center", config.Predicates[0].Path);
        Assert.Equal(1, config.Predicates[0].AreaId);
        Assert.Single(config.Predicates[0].Excludes);
        Assert.Equal("/Customer Center/Archive", config.Predicates[0].Excludes[0]);
    }

    [Fact]
    public void Load_IncludeLanguageLayers_BindsToPredicate()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Site",
                  "mode": "Replace",
                  "path": "/",
                  "areaId": 3,
                  "includeLanguageLayers": true
                },
                {
                  "name": "Posts",
                  "mode": "Merge",
                  "path": "/Posts",
                  "areaId": 3
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.True(config.Predicates[0].IncludeLanguageLayers);
        Assert.False(config.Predicates[1].IncludeLanguageLayers);
    }

    [Fact]
    public void Load_NullExcludes_DefaultsToEmptyList()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.Predicates[0].Excludes);
        Assert.Empty(config.Predicates[0].Excludes);
    }

    // -------------------------------------------------------------------------
    // Missing file
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException_WithPath()
    {
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.json");

        var ex = Assert.Throws<FileNotFoundException>(() => ConfigLoader.Load(nonExistentPath));

        Assert.Contains(nonExistentPath, ex.Message);
    }

    // -------------------------------------------------------------------------
    // Missing required fields
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_MissingOutputDirectory_ThrowsInvalidOperationException_WithFieldName()
    {
        var json = """
            {
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));

        Assert.Contains("outputDirectory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MissingPredicatesKey_ReturnsEmptyList()
    {
        var json = """
            {
              "outputDirectory": "/serialization"
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.Predicates);
        Assert.Empty(config.Predicates);
    }

    [Fact]
    public void Load_EmptyPredicates_ReturnsEmptyList()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": []
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.Predicates);
        Assert.Empty(config.Predicates);
    }

    [Fact]
    public void Load_PredicateMissingPath_ThrowsInvalidOperationException_WithFieldName()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));

        Assert.Contains("path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_PredicateMissingAreaId_ThrowsInvalidOperationException_WithFieldName()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));

        Assert.Contains("areaId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_PredicateMissingName_ThrowsInvalidOperationException_WithFieldName()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));

        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // OutputDirectory existence validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_NonExistentOutputDirectory_EmitsWarning()
    {
        var nonExistentDir = Path.Combine(_tempDir, "nonexistent_" + Guid.NewGuid().ToString("N")[..8]);
        var json = $$"""
            {
              "outputDirectory": "{{nonExistentDir.Replace("\\", "\\\\")}}",
              "predicates": [
                {
                  "name": "Test",
                  "mode": "Replace",
                  "path": "/Test",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var originalError = Console.Error;
        var errorCapture = new StringWriter();
        Console.SetError(errorCapture);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(nonExistentDir, config.OutputDirectory);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var errorOutput = errorCapture.ToString();
        Assert.Contains("does not exist", errorOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nonExistentDir, errorOutput);
    }

    [Fact]
    public void Load_ExistingOutputDirectory_NoWarning()
    {
        var existingDir = Path.Combine(_tempDir, "existing_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(existingDir);
        var json = $$"""
            {
              "outputDirectory": "{{existingDir.Replace("\\", "\\\\")}}",
              "predicates": [
                {
                  "name": "Test",
                  "mode": "Replace",
                  "path": "/Test",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var originalError = Console.Error;
        var errorCapture = new StringWriter();
        Console.SetError(errorCapture);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(existingDir, config.OutputDirectory);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var errorOutput = errorCapture.ToString();
        Assert.DoesNotContain(existingDir, errorOutput);
    }

    // -------------------------------------------------------------------------
    // ProviderPredicateDefinition basics
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_ContentPredicate_WithoutProviderType_DefaultsToContent()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Single(config.Predicates);
        Assert.IsType<ProviderPredicateDefinition>(config.Predicates[0]);
        Assert.Equal("Content", config.Predicates[0].ProviderType);
        Assert.Equal("/Customer Center", config.Predicates[0].Path);
        Assert.Equal(1, config.Predicates[0].AreaId);
    }

    [Fact]
    public void Load_SqlTablePredicate_ReturnsProviderPredicateDefinitionWithTableFields()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Order Flows",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "nameColumn": "OrderFlowName",
                  "compareColumns": "OrderFlowName,OrderFlowDescription"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Single(config.Predicates);
        Assert.Equal("SqlTable", config.Predicates[0].ProviderType);
        Assert.Equal("EcomOrderFlow", config.Predicates[0].Table);
        Assert.Equal("OrderFlowName", config.Predicates[0].NameColumn);
        Assert.Equal("OrderFlowName,OrderFlowDescription", config.Predicates[0].CompareColumns);
    }

    [Fact]
    public void Load_MixedPredicates_ReturnsBothWithCorrectProviderTypes()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1
                },
                {
                  "name": "Order Flows",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "nameColumn": "OrderFlowName"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.Predicates.Count);
        Assert.Equal("Content", config.Predicates[0].ProviderType);
        Assert.Equal("SqlTable", config.Predicates[1].ProviderType);
    }

    [Fact]
    public void Load_SqlTablePredicate_WithServiceCaches_DeserializesServiceCaches()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Order Flows",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "nameColumn": "OrderFlowName",
                  "serviceCaches": ["Dynamicweb.Ecommerce.Orders.PaymentService", "Dynamicweb.Ecommerce.Orders.ShippingService"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.Predicates[0].ServiceCaches.Count);
        Assert.Equal("Dynamicweb.Ecommerce.Orders.PaymentService", config.Predicates[0].ServiceCaches[0]);
        Assert.Equal("Dynamicweb.Ecommerce.Orders.ShippingService", config.Predicates[0].ServiceCaches[1]);
    }

    [Fact]
    public void Load_SqlTablePredicate_WithoutServiceCaches_DefaultsToEmptyList()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Order Flows",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "nameColumn": "OrderFlowName"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.Predicates[0].ServiceCaches);
        Assert.Empty(config.Predicates[0].ServiceCaches);
    }

    [Fact]
    public void Load_SqlTablePredicate_MissingPath_DoesNotThrow()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Order Flows",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "nameColumn": "OrderFlowName"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);
        Assert.Single(config.Predicates);
    }

    // -------------------------------------------------------------------------
    // XmlColumns config mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_SqlTablePredicate_WithXmlColumns_DeserializesXmlColumns()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Shipping Methods",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomShippings",
                  "nameColumn": "ShippingName",
                  "xmlColumns": ["ShippingXml", "SettingsXml"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.Predicates[0].XmlColumns.Count);
        Assert.Equal("ShippingXml", config.Predicates[0].XmlColumns[0]);
        Assert.Equal("SettingsXml", config.Predicates[0].XmlColumns[1]);
    }

    [Fact]
    public void Load_SqlTablePredicate_WithoutXmlColumns_DefaultsToEmptyList()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Order Flows",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "nameColumn": "OrderFlowName"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.Predicates[0].XmlColumns);
        Assert.Empty(config.Predicates[0].XmlColumns);
    }

    // -------------------------------------------------------------------------
    // ExcludeFields / ExcludeXmlElements config mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_ContentPredicate_WithExcludeFields_DeserializesExcludeFields()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1,
                  "excludeFields": ["NavigationTag", "AreaDomain"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.Predicates[0].ExcludeFields.Count);
        Assert.Equal("NavigationTag", config.Predicates[0].ExcludeFields[0]);
        Assert.Equal("AreaDomain", config.Predicates[0].ExcludeFields[1]);
    }

    [Fact]
    public void Load_ContentPredicate_WithExcludeXmlElements_DeserializesExcludeXmlElements()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1,
                  "excludeXmlElements": ["sort", "pagesize"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.Predicates[0].ExcludeXmlElements.Count);
        Assert.Equal("sort", config.Predicates[0].ExcludeXmlElements[0]);
        Assert.Equal("pagesize", config.Predicates[0].ExcludeXmlElements[1]);
    }

    [Fact]
    public void Load_ContentPredicate_WithoutExcludeFields_DefaultsToEmptyLists()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Customer Center",
                  "mode": "Replace",
                  "path": "/Customer Center",
                  "areaId": 1
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.Predicates[0].ExcludeFields);
        Assert.Empty(config.Predicates[0].ExcludeFields);
        Assert.NotNull(config.Predicates[0].ExcludeXmlElements);
        Assert.Empty(config.Predicates[0].ExcludeXmlElements);
    }

    // -------------------------------------------------------------------------
    // Top-level typed exclusion dictionaries (Phase 40 D-04 — flat at top level)
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_ConfigWithExcludeFieldsByItemType_DeserializesDictionary()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "excludeFieldsByItemType": {
                "Swift_PageItemType": ["NavigationTag", "AreaDomain"],
                "Swift_ParagraphItemType": ["ModuleSettings"]
              },
              "predicates": []
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.ExcludeFieldsByItemType.Count);
        Assert.Equal(new List<string> { "NavigationTag", "AreaDomain" }, config.ExcludeFieldsByItemType["Swift_PageItemType"]);
        Assert.Equal(new List<string> { "ModuleSettings" }, config.ExcludeFieldsByItemType["Swift_ParagraphItemType"]);
    }

    [Fact]
    public void Load_ConfigWithExcludeXmlElementsByType_DeserializesDictionary()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "excludeXmlElementsByType": {
                "Dynamicweb.Frontend.ContentPage": ["sort", "pagesize"]
              },
              "predicates": []
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Single(config.ExcludeXmlElementsByType);
        Assert.Equal(new List<string> { "sort", "pagesize" }, config.ExcludeXmlElementsByType["Dynamicweb.Frontend.ContentPage"]);
    }

    [Fact]
    public void Load_ConfigWithoutTypedDictionaries_DefaultsToEmptyDictionaries()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": []
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.NotNull(config.ExcludeFieldsByItemType);
        Assert.Empty(config.ExcludeFieldsByItemType);
        Assert.NotNull(config.ExcludeXmlElementsByType);
        Assert.Empty(config.ExcludeXmlElementsByType);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsTypedDictionaries()
    {
        var config = new SerializerConfiguration
        {
            OutputDirectory = _tempDir,
            ExcludeFieldsByItemType = new Dictionary<string, List<string>>
            {
                ["Swift_PageItemType"] = new List<string> { "NavigationTag" }
            },
            ExcludeXmlElementsByType = new Dictionary<string, List<string>>
            {
                ["Dynamicweb.Frontend.ContentPage"] = new List<string> { "sort" }
            }
        };
        var path = Path.Combine(_tempDir, "roundtrip.json");
        ConfigWriter.Save(config, path);

        var reloaded = ConfigLoader.Load(path);

        Assert.Single(reloaded.ExcludeFieldsByItemType);
        Assert.Equal(new List<string> { "NavigationTag" }, reloaded.ExcludeFieldsByItemType["Swift_PageItemType"]);
        Assert.Single(reloaded.ExcludeXmlElementsByType);
        Assert.Equal(new List<string> { "sort" }, reloaded.ExcludeXmlElementsByType["Dynamicweb.Frontend.ContentPage"]);
    }

    // -------------------------------------------------------------------------
    // Phase 37-03: Where + IncludeFields on SqlTable predicates
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_WithWhereClause_And_IncludeFields_RoundTrips()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "AccessUser-Roles",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "AccessUser",
                  "where": "AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')",
                  "includeFields": ["AccessUserHostingId", "AccessUserHostingName"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Single(config.Predicates);
        var pred = config.Predicates[0];
        Assert.Equal("AccessUser-Roles", pred.Name);
        Assert.Equal("AccessUser", pred.Table);
        Assert.Equal("AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')", pred.Where);
        Assert.Equal(2, pred.IncludeFields.Count);
        Assert.Equal("AccessUserHostingId", pred.IncludeFields[0]);
        Assert.Equal("AccessUserHostingName", pred.IncludeFields[1]);
    }

    [Fact]
    public void Load_WithFixtureValidator_BadTableIdentifier_Throws()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                { "name": "Bad", "mode": "Replace", "providerType": "SqlTable", "table": "NotARealTable" }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, validator));
        Assert.Contains("INFORMATION_SCHEMA", ex.Message);
        Assert.Contains("NotARealTable", ex.Message);
    }

    /// <summary>
    /// Engine issue #37: an excludeFields column the host lacks no longer fails the load (see
    /// ConfigLoaderExcludeFieldsTests). A bad includeFields column still does.
    /// </summary>
    [Fact]
    public void Load_WithFixtureValidator_BadIncludeFieldIdentifier_Throws()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "X", "mode": "Replace", "providerType": "SqlTable", "table": "AccessUser",
                  "includeFields": ["NonExistentColumn"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUserType" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, validator));
        Assert.Contains("NonExistentColumn", ex.Message);
    }

    [Fact]
    public void Load_WithFixtureValidator_BadWhereIdentifier_Throws()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "X", "mode": "Replace", "providerType": "SqlTable", "table": "AccessUser",
                  "where": "NonExistentColumn = 1"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUserType" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, validator));
        Assert.Contains("NonExistentColumn", ex.Message);
    }

    [Fact]
    public void Load_WithFixtureValidator_AggregatesMultipleErrors()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                { "name": "A", "mode": "Replace", "providerType": "SqlTable", "table": "UnknownTableA" },
                { "name": "B", "mode": "Replace", "providerType": "SqlTable", "table": "UnknownTableB" },
                { "name": "C", "mode": "Replace", "providerType": "SqlTable", "table": "UnknownTableC" }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigLoader.Load(path, validator));
        Assert.Contains("UnknownTableA", ex.Message);
        Assert.Contains("UnknownTableB", ex.Message);
        Assert.Contains("UnknownTableC", ex.Message);
    }

    [Fact]
    public void Load_WithFixtureValidator_ValidSqlTableConfig_LoadsSuccessfully()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "AU", "mode": "Replace", "providerType": "SqlTable", "table": "AccessUser",
                  "where": "AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')",
                  "excludeFields": ["AccessUserPassword"],
                  "includeFields": ["AccessUserHostingName"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var validator = new SqlIdentifierValidator(
            tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
            columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AccessUserType", "AccessUserUserName", "AccessUserPassword", "AccessUserHostingName"
            });

        var config = ConfigLoader.Load(path, validator);

        Assert.Single(config.Predicates);
    }

    [Fact]
    public void Load_WithoutWhereOrIncludeFields_DefaultsNullAndEmpty()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Plain",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "AccessUser"
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        var pred = config.Predicates[0];
        Assert.Null(pred.Where);
        Assert.NotNull(pred.IncludeFields);
        Assert.Empty(pred.IncludeFields);
    }

    // -------------------------------------------------------------------------
    // Phase 37-04 CACHE-01: ServiceCaches validation against DwCacheServiceRegistry
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Phase37-04")]
    public void Load_UnknownServiceCache_ThrowsWithSupportedList()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Payments",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomPayments",
                  "serviceCaches": ["Dynamicweb.Ecommerce.Orders.NotARealService"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));
        Assert.Contains("ServiceCaches validation failed", ex.Message);
        Assert.Contains("Dynamicweb.Ecommerce.Orders.NotARealService", ex.Message);
        Assert.Contains("'Payments'", ex.Message);
        Assert.Contains("Supported", ex.Message);
    }

    [Fact]
    [Trait("Category", "Phase37-04")]
    public void Load_KnownServiceCaches_ShortAndFullNames_Loads()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Payments",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomPayments",
                  "serviceCaches": [
                    "PaymentService",
                    "Dynamicweb.Ecommerce.Orders.ShippingService"
                  ]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);

        Assert.Equal(2, config.Predicates[0].ServiceCaches.Count);
    }

    [Fact]
    [Trait("Category", "Phase37-04")]
    public void Load_UnknownServiceCacheInBothReplaceAndMergePredicates_AggregatesErrors()
    {
        // Phase 40: scope is "predicates" (not "replace.predicates" / "merge.predicates"). Each
        // predicate's mode is irrelevant to ServiceCaches validation — a single flat loop reports
        // both predicates with their names.
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "BadReplace",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomPayments",
                  "serviceCaches": ["Nonexistent.ReplaceCache"]
                },
                {
                  "name": "BadMerge",
                  "mode": "Merge",
                  "providerType": "SqlTable",
                  "table": "EcomShippings",
                  "serviceCaches": ["Nonexistent.MergeCache"]
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));

        Assert.Contains("predicates 'BadReplace'", ex.Message);
        Assert.Contains("predicates 'BadMerge'", ex.Message);
        Assert.Contains("Nonexistent.ReplaceCache", ex.Message);
        Assert.Contains("Nonexistent.MergeCache", ex.Message);
    }

    [Fact]
    [Trait("Category", "Phase37-04")]
    public void Load_EmptyServiceCaches_Passes()
    {
        var json = """
            {
              "outputDirectory": "/serialization",
              "predicates": [
                {
                  "name": "Plain",
                  "mode": "Replace",
                  "providerType": "SqlTable",
                  "table": "EcomOrderFlow",
                  "serviceCaches": []
                }
              ]
            }
            """;
        var path = WriteConfigFile(json);

        var config = ConfigLoader.Load(path);
        Assert.Empty(config.Predicates[0].ServiceCaches);
    }

    // -------------------------------------------------------------------------
    // Phase 37-06: SC-3 — default-path 1-arg Load runs identifier validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_DefaultPath_MaliciousTableIdentifier_Throws()
    {
        var previousOverride = ConfigLoader.TestOverrideIdentifierValidator;
        try
        {
            ConfigLoader.TestOverrideIdentifierValidator = new SqlIdentifierValidator(
                tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
                columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var json = """
                {
                  "outputDirectory": "/serialization",
                  "predicates": [
                    {
                      "name": "MaliciousSqlTable",
                      "mode": "Replace",
                      "providerType": "SqlTable",
                      "table": "EcomOrders] WHERE 1=1; DROP TABLE Users; --"
                    }
                  ]
                }
                """;
            var path = WriteConfigFile(json);

            var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.Load(path));
            Assert.Contains("INFORMATION_SCHEMA", ex.Message);
            Assert.Contains("EcomOrders", ex.Message);
        }
        finally
        {
            ConfigLoader.TestOverrideIdentifierValidator = previousOverride;
        }
    }

    [Fact]
    [Trait("Category", "Phase37-06-StructuralIntegration")]
    public void Load_DefaultPath_NoTestOverride_ConstructsDefaultValidator()
    {
        var previousOverride = ConfigLoader.TestOverrideIdentifierValidator;
        var previousCallback = ConfigLoader._testDefaultValidatorConstructedCallback.Value;
        try
        {
            ConfigLoader.TestOverrideIdentifierValidator = null;

            var defaultValidatorConstructed = false;
            ConfigLoader._testDefaultValidatorConstructedCallback.Value =
                () => defaultValidatorConstructed = true;

            var json = """
                {
                  "outputDirectory": "/serialization",
                  "predicates": [
                    {
                      "name": "StructuralProof",
                      "mode": "Replace",
                      "providerType": "SqlTable",
                      "table": "NotARealTable"
                    }
                  ]
                }
                """;
            var path = WriteConfigFile(json);

            try { ConfigLoader.Load(path); } catch { /* intentionally swallow */ }

            Assert.True(
                defaultValidatorConstructed,
                "Expected the 1-arg ConfigLoader.Load(path) overload to invoke " +
                "_testDefaultValidatorConstructedCallback (proving it constructed a " +
                "default SqlIdentifierValidator).");
        }
        finally
        {
            ConfigLoader.TestOverrideIdentifierValidator = previousOverride;
            ConfigLoader._testDefaultValidatorConstructedCallback.Value = previousCallback;
        }
    }
}
