using System.Data;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

[Trait("Category", "Phase29")]
public class SqlTableProviderSerializeTests
{
    private static readonly TableMetadata TestMetadata = new()
    {
        TableName = "TestTable",
        NameColumn = "Name",
        KeyColumns = new List<string> { "Id" },
        IdentityColumns = new List<string> { "Id" },
        AllColumns = new List<string> { "Id", "Name", "Description", "SettingsXml" }
    };

    [Fact]
    public void Serialize_ExcludeFields_OmitsColumns()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = 1,
            ["Name"] = "Test",
            ["Description"] = "To be excluded",
            ["SettingsXml"] = "<root />"
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Test",
            ProviderType = "SqlTable",
            Table = "TestTable",
            NameColumn = "Name",
            ExcludeFields = new List<string> { "Description" }
        };

        var (provider, _, outputRoot) = CreateProviderForSerialize(new[] { row });

        var result = provider.Serialize(predicate, outputRoot);

        Assert.Equal(1, result.RowsSerialized);

        // Read the written YAML file and verify excluded column is absent
        var fileStore = new FlatFileStore();
        var rows = fileStore.ReadAllRows(outputRoot, "TestTable").ToList();
        Assert.Single(rows);
        Assert.False(rows[0].ContainsKey("Description"), "Excluded field 'Description' should not be in serialized output");
        Assert.True(rows[0].ContainsKey("Id"));
        Assert.True(rows[0].ContainsKey("Name"));
    }

    /// <summary>
    /// Engine issue #37: an excludeFields entry naming a column this host does not have is a
    /// no-op at serialize; the columns it does have are still left out of every row.
    /// </summary>
    [Fact]
    public void Serialize_ExcludeFields_ColumnAbsentOnHost_IsIgnored_PresentColumnStillOmitted()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = 1,
            ["Name"] = "Test",
            ["Description"] = "To be excluded",
            ["SettingsXml"] = "<root />"
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Test",
            ProviderType = "SqlTable",
            Table = "TestTable",
            NameColumn = "Name",
            ExcludeFields = new List<string> { "MyDouble", "Description" }
        };

        var (provider, _, outputRoot) = CreateProviderForSerialize(new[] { row });

        var lines = new List<string>();
        var result = provider.Serialize(predicate, outputRoot, lines.Add);

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.RowsSerialized);
        var line = Assert.Single(lines, l => l.Contains("excludeFields names column(s)"));
        Assert.Contains("MyDouble", line);
        Assert.DoesNotContain("Description", line);
        Assert.False(line.StartsWith("WARNING"));
        var written = Assert.Single(new FlatFileStore().ReadAllRows(outputRoot, "TestTable"));
        Assert.False(written.ContainsKey("Description"));
        Assert.False(written.ContainsKey("MyDouble"));
        Assert.True(written.ContainsKey("SettingsXml"));
    }

    [Fact]
    public void Serialize_ExcludeXmlElements_StripsElements()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = 1,
            ["Name"] = "Test",
            ["Description"] = "Desc",
            ["SettingsXml"] = "<root><pagesize>10</pagesize><layout>grid</layout></root>"
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Test",
            ProviderType = "SqlTable",
            Table = "TestTable",
            NameColumn = "Name",
            XmlColumns = new List<string> { "SettingsXml" },
            ExcludeXmlElements = new List<string> { "pagesize" }
        };

        var (provider, _, outputRoot) = CreateProviderForSerialize(new[] { row });

        var result = provider.Serialize(predicate, outputRoot);

        Assert.Equal(1, result.RowsSerialized);

        var fileStore = new FlatFileStore();
        var rows = fileStore.ReadAllRows(outputRoot, "TestTable").ToList();
        Assert.Single(rows);
        var settingsXml = rows[0]["SettingsXml"]?.ToString() ?? "";
        Assert.DoesNotContain("pagesize", settingsXml);
        Assert.Contains("layout", settingsXml);
    }

    [Fact]
    public void Serialize_NoExcludeFields_AllColumnsPresent()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = 1,
            ["Name"] = "Test",
            ["Description"] = "Full row",
            ["SettingsXml"] = "<root />"
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Test",
            ProviderType = "SqlTable",
            Table = "TestTable",
            NameColumn = "Name"
            // No ExcludeFields, no ExcludeXmlElements
        };

        var (provider, _, outputRoot) = CreateProviderForSerialize(new[] { row });

        var result = provider.Serialize(predicate, outputRoot);

        Assert.Equal(1, result.RowsSerialized);

        var fileStore = new FlatFileStore();
        var rows = fileStore.ReadAllRows(outputRoot, "TestTable").ToList();
        Assert.Single(rows);
        Assert.True(rows[0].ContainsKey("Id"));
        Assert.True(rows[0].ContainsKey("Name"));
        Assert.True(rows[0].ContainsKey("Description"));
        Assert.True(rows[0].ContainsKey("SettingsXml"));
    }

    // -------------------------------------------------------------------------
    // Phase 37-03: RuntimeExcludes auto-applies at serialize; IncludeFields opts back in
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_RuntimeExcludes_EcomShops_AutoStripsIndexColumns()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ShopId"] = "Shop1",
            ["ShopName"] = "Default",
            ["ShopIndexRepository"] = "ProductsBackend",
            ["ShopIndexName"] = "Products.index",
            ["ShopIndexDocumentType"] = "Product"
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Shops",
            ProviderType = "SqlTable",
            Table = "EcomShops",
            NameColumn = "ShopName"
            // no explicit ExcludeFields, no IncludeFields
        };

        var metadata = new TableMetadata
        {
            TableName = "EcomShops",
            NameColumn = "ShopName",
            KeyColumns = new List<string> { "ShopId" },
            IdentityColumns = new List<string>(),
            AllColumns = new List<string> { "ShopId", "ShopName", "ShopIndexRepository", "ShopIndexName", "ShopIndexDocumentType" }
        };

        var (provider, _, outputRoot) = CreateProviderForSerializeCustom(new[] { row }, metadata,
            new[] { "ShopId", "ShopName", "ShopIndexRepository", "ShopIndexName", "ShopIndexDocumentType" });

        var result = provider.Serialize(predicate, outputRoot);

        Assert.Equal(1, result.RowsSerialized);

        var fileStore = new FlatFileStore();
        var rows = fileStore.ReadAllRows(outputRoot, "EcomShops").ToList();
        Assert.Single(rows);
        Assert.False(rows[0].ContainsKey("ShopIndexRepository"));
        Assert.False(rows[0].ContainsKey("ShopIndexName"));
        Assert.False(rows[0].ContainsKey("ShopIndexDocumentType"));
        // Non-runtime columns still present
        Assert.True(rows[0].ContainsKey("ShopName"));
    }

    [Fact]
    public void Serialize_RuntimeExcludes_IncludeFieldsOptsBackIn()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ShopId"] = "Shop1",
            ["ShopName"] = "Default",
            ["ShopIndexRepository"] = "ProductsBackend",
            ["ShopIndexName"] = "Products.index",
            ["ShopIndexDocumentType"] = "Product"
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Shops",
            ProviderType = "SqlTable",
            Table = "EcomShops",
            NameColumn = "ShopName",
            IncludeFields = new List<string> { "ShopIndexRepository" }
        };

        var metadata = new TableMetadata
        {
            TableName = "EcomShops",
            NameColumn = "ShopName",
            KeyColumns = new List<string> { "ShopId" },
            IdentityColumns = new List<string>(),
            AllColumns = new List<string> { "ShopId", "ShopName", "ShopIndexRepository", "ShopIndexName", "ShopIndexDocumentType" }
        };

        var (provider, _, outputRoot) = CreateProviderForSerializeCustom(new[] { row }, metadata,
            new[] { "ShopId", "ShopName", "ShopIndexRepository", "ShopIndexName", "ShopIndexDocumentType" });

        _ = provider.Serialize(predicate, outputRoot);

        var fileStore = new FlatFileStore();
        var rows = fileStore.ReadAllRows(outputRoot, "EcomShops").ToList();
        Assert.True(rows[0].ContainsKey("ShopIndexRepository"), "IncludeFields should re-include ShopIndexRepository");
        Assert.False(rows[0].ContainsKey("ShopIndexName"));
        Assert.False(rows[0].ContainsKey("ShopIndexDocumentType"));
    }

    [Fact]
    public void Serialize_PassesWhereClauseToReader()
    {
        // Verify SqlTableProvider forwards predicate.Where to SqlTableReader.ReadAllRows.
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = 1,
            ["Name"] = "Admin",
            ["Description"] = "",
            ["SettingsXml"] = ""
        };

        var predicate = new ProviderPredicateDefinition
        {
            Name = "Roles",
            ProviderType = "SqlTable",
            Table = "TestTable",
            NameColumn = "Name",
            Where = "Id = 1"
        };

        var (provider, mockExecutor, outputRoot) = CreateProviderForSerialize(new[] { row });

        _ = provider.Serialize(predicate, outputRoot);

        // Capture the CommandBuilder from the reader path and verify WHERE was composed.
        // (We already proved the SQL composition in SqlTableReaderWhereClauseTests; here we
        // just confirm the provider passed Where through rather than dropping it.)
        mockExecutor.Verify(
            x => x.ExecuteReader(It.Is<Dynamicweb.Data.CommandBuilder>(
                cb => cb.ToString().Contains("WHERE Id = 1"))),
            Times.AtLeastOnce);
    }

    private static (SqlTableProvider provider, Mock<ISqlExecutor> executor, string outputRoot)
        CreateProviderForSerializeCustom(
            IEnumerable<Dictionary<string, object?>> rows,
            TableMetadata metadata,
            string[] columnNames)
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        var mockMetadataReader = new Mock<DataGroupMetadataReader>(mockExecutor.Object) { CallBase = false };
        mockMetadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(metadata);

        var rowList = rows.ToList();
        var dbReaderMock = CreateMockDataReader(
            columnNames,
            rowList.Select(r => columnNames.Select(col => r.GetValueOrDefault(col) ?? DBNull.Value).ToArray()).ToArray());
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<Dynamicweb.Data.CommandBuilder>()))
            .Returns(dbReaderMock.Object);

        var tableReader = new SqlTableReader(mockExecutor.Object);
        var fileStore = new FlatFileStore();

        var tempDir = Path.Combine(Path.GetTempPath(), $"contentsync_serialize_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var writerMock = new Mock<SqlTableWriter>(mockExecutor.Object) { CallBase = false };
        var provider = new SqlTableProvider(mockMetadataReader.Object, tableReader, fileStore, writerMock.Object);
        return (provider, mockExecutor, tempDir);
    }

    #region Helper Methods

    private static (SqlTableProvider provider, Mock<ISqlExecutor> executor, string outputRoot)
        CreateProviderForSerialize(IEnumerable<Dictionary<string, object?>> rows)
    {
        var mockExecutor = new Mock<ISqlExecutor>();

        // DataGroupMetadataReader mock
        var mockMetadataReader = new Mock<DataGroupMetadataReader>(mockExecutor.Object) { CallBase = false };
        mockMetadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(TestMetadata);

        // SqlTableReader mock -- ReadAllRows returns the test rows
        var rowList = rows.ToList();
        var dbReaderMock = CreateMockDataReader(
            new[] { "Id", "Name", "Description", "SettingsXml" },
            rowList.Select(r => new object[]
            {
                r.GetValueOrDefault("Id") ?? DBNull.Value,
                r.GetValueOrDefault("Name") ?? DBNull.Value,
                r.GetValueOrDefault("Description") ?? DBNull.Value,
                r.GetValueOrDefault("SettingsXml") ?? DBNull.Value
            }).ToArray());
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<Dynamicweb.Data.CommandBuilder>()))
            .Returns(dbReaderMock.Object);

        var tableReader = new SqlTableReader(mockExecutor.Object);
        var fileStore = new FlatFileStore();

        // Output directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"contentsync_serialize_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var writerMock = new Mock<SqlTableWriter>(mockExecutor.Object) { CallBase = false };
        var provider = new SqlTableProvider(mockMetadataReader.Object, tableReader, fileStore, writerMock.Object);

        return (provider, mockExecutor, tempDir);
    }

    private static Mock<IDataReader> CreateMockDataReader(string[] columns, object[][] rows)
    {
        var mock = new Mock<IDataReader>();
        var rowIndex = -1;

        mock.Setup(r => r.Read()).Returns(() =>
        {
            rowIndex++;
            return rowIndex < rows.Length;
        });

        mock.Setup(r => r.FieldCount).Returns(columns.Length);
        for (int i = 0; i < columns.Length; i++)
        {
            var idx = i;
            mock.Setup(r => r.GetName(idx)).Returns(columns[idx]);
            mock.Setup(r => r.GetValue(idx)).Returns(() =>
                rowIndex >= 0 && rowIndex < rows.Length ? rows[rowIndex][idx] : DBNull.Value);
        }

        mock.Setup(r => r[It.IsAny<string>()]).Returns((string col) =>
        {
            var colIndex = Array.IndexOf(columns, col);
            return rowIndex >= 0 && rowIndex < rows.Length && colIndex >= 0
                ? rows[rowIndex][colIndex]
                : DBNull.Value;
        });

        mock.Setup(r => r.Dispose());
        return mock;
    }

    #endregion
}
