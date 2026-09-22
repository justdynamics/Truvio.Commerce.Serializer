using System.Data;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Engine issue #20: two documents in one <c>_sql/&lt;Table&gt;/</c> directory carrying the SAME
/// row identity — the layered-composition case, a partial override row next to a full row.
/// Both used to take the "not in the target snapshot" path and the file that happened to sort
/// last won, so a partial document could write a row carrying only its own columns. The rule is
/// now explicit: same identity in one pass is merged later-layer-wins, column by column, before
/// anything is written, and the outcome is the same under any culture.
/// </summary>
[Trait("Category", "Issue20")]
public class SqlTableSameIdentityDocumentTests : IDisposable
{
    private static readonly TableMetadata Metadata = new()
    {
        TableName = "TestTable",
        NameColumn = "Name",
        KeyColumns = new List<string> { "Name" },
        IdentityColumns = new List<string>(),
        AllColumns = new List<string> { "Name", "Title", "Color" }
    };

    private static readonly SqlTableEntry Entry = new()
    {
        EntryId = "sql/TestTable",
        Files = Array.Empty<string>(),
        Table = "TestTable",
        NameColumn = "Name"
    };

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void SameIdentity_PartialAfterFull_WritesOneRow_WithTheLaterDocumentsColumnsWinning()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            ("a-full.yml", "Name: row-1\nTitle: Full title\nColor: blue\n"),
            ("zz-partial.yml", "Name: row-1\nColor: champagne\n"));

        var written = CaptureWrites(writer);

        var result = provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.SourceWins);

        var row = Assert.Single(written);
        Assert.Equal("row-1", row["Name"]);
        Assert.Equal("Full title", row["Title"]);      // kept from the earlier, fuller document
        Assert.Equal("champagne", row["Color"]);       // overridden by the later document
        Assert.Equal(1, result.Created);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SameIdentity_FullAfterPartial_WritesOneRow_WithTheLaterDocumentsColumnsWinning()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            ("a-partial.yml", "Name: row-1\nColor: champagne\n"),
            ("zz-full.yml", "Name: row-1\nTitle: Full title\nColor: blue\n"));

        var written = CaptureWrites(writer);

        provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.SourceWins);

        var row = Assert.Single(written);
        Assert.Equal("Full title", row["Title"]);
        Assert.Equal("blue", row["Color"]);            // the later document wins in both directions
    }

    [Fact]
    public void DistinctIdentities_AreNotCollapsed()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            ("a.yml", "Name: row-1\nTitle: One\nColor: blue\n"),
            ("b.yml", "Name: row-2\nTitle: Two\nColor: red\n"));

        var written = CaptureWrites(writer);

        provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(2, written.Count);
        Assert.Equal(new[] { "row-1", "row-2" }, written.Select(r => (string)r["Name"]!));
    }

    [Fact]
    public void SameIdentity_LogsTheMergeSoItIsNotSilent()
    {
        var (provider, _, inputRoot) = CreateProvider(
            ("a-full.yml", "Name: row-1\nTitle: Full title\nColor: blue\n"),
            ("zz-partial.yml", "Name: row-1\nColor: champagne\n"));

        var lines = new List<string>();
        provider.Deserialize(Entry, inputRoot, log: lines.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Contains(lines, l => l.Contains("later-layer-wins") && l.Contains("row-1"));
    }

    // -----------------------------------------------------------------------
    // Harness — mirrors SqlTableProviderMergeModeTests.CreateProviderWithFiles, but writes
    // the row documents under explicit file names so two of them can share one identity.
    // -----------------------------------------------------------------------

    private (SqlTableProvider provider, Mock<SqlTableWriter> writer, string inputRoot) CreateProvider(
        params (string FileName, string Yaml)[] documents)
    {
        var executor = new Mock<ISqlExecutor>();

        var metadataReader = new Mock<DataGroupMetadataReader>(executor.Object) { CallBase = false };
        metadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(Metadata);
        metadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
        metadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>()))
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "nvarchar",
                ["Title"] = "nvarchar",
                ["Color"] = "nvarchar"
            });
        metadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Empty target table: every row takes the insert path.
        executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>())).Returns(EmptyReader().Object);

        var tempDir = Path.Combine(Path.GetTempPath(), $"sametable_{Guid.NewGuid():N}");
        _tempDirs.Add(tempDir);
        var tableDir = Path.Combine(tempDir, "_sql", Metadata.TableName);
        Directory.CreateDirectory(tableDir);
        foreach (var (fileName, yaml) in documents)
            File.WriteAllText(Path.Combine(tableDir, fileName), yaml);

        var schemaCache = new TargetSchemaCache(_ =>
            (new HashSet<string>(Metadata.AllColumns, StringComparer.OrdinalIgnoreCase),
             new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
             {
                 ["Name"] = "nvarchar",
                 ["Title"] = "nvarchar",
                 ["Color"] = "nvarchar"
             }));

        var writer = new Mock<SqlTableWriter>(executor.Object) { CallBase = false };
        var provider = new SqlTableProvider(
            metadataReader.Object, new SqlTableReader(executor.Object), new FlatFileStore(),
            writer.Object, schemaCache);

        return (provider, writer, tempDir);
    }

    private static List<Dictionary<string, object?>> CaptureWrites(Mock<SqlTableWriter> writer)
    {
        var written = new List<Dictionary<string, object?>>();
        writer.Setup(w => w.WriteRow(
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<TableMetadata>(),
                It.IsAny<bool>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<HashSet<string>?>()))
            .Callback((Dictionary<string, object?> row, TableMetadata _, bool __, Action<string>? ___, HashSet<string>? ____) =>
                written.Add(new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase)))
            .Returns(WriteOutcome.Created);
        return written;
    }

    private static Mock<IDataReader> EmptyReader()
    {
        var mock = new Mock<IDataReader>();
        mock.Setup(r => r.Read()).Returns(false);
        mock.Setup(r => r.FieldCount).Returns(0);
        mock.Setup(r => r.Dispose());
        return mock;
    }
}
