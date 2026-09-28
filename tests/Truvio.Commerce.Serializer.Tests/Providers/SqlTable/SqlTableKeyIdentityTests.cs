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
/// Engine issue #38: <c>nameColumn</c> was the row identity on deserialize, so rows with a
/// repeated name and different keys were merged by the issue #20 same-identity rule and lost on
/// a blank target. The Distribution base ships 18 <c>EcomOrderStates</c> keyed by
/// <c>OrderStateId</c> with <c>nameColumn: OrderStateName</c>; <c>New</c> is OS1, OS8 and OS11,
/// <c>Draft</c> is OS5 and OS10, <c>Rejected</c> is OS3 and OS7, and only 14 rows landed. Row
/// identity is now the resolved key, always; <c>nameColumn</c> names the file and nothing else.
/// </summary>
[Trait("Category", "Issue38")]
public class SqlTableKeyIdentityTests : IDisposable
{
    private static readonly TableMetadata Metadata = new()
    {
        TableName = "EcomOrderStates",
        NameColumn = "OrderStateName",
        KeyColumns = new List<string> { "OrderStateId" },
        IdentityColumns = new List<string>(),
        AllColumns = new List<string> { "OrderStateId", "OrderStateName", "OrderStateOrderFlowId", "OrderStateDescription" }
    };

    private static readonly SqlTableEntry Entry = new()
    {
        EntryId = "sql/EcomOrderStates",
        Files = Array.Empty<string>(),
        Table = "EcomOrderStates",
        NameColumn = "OrderStateName"
    };

    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>The documents as the serializer names them when names repeat (FlatFileStore dedup suffix).</summary>
    private static readonly (string FileName, string Yaml)[] RepeatedNameDocuments =
    {
        ("New.yml", "OrderStateId: OS1\nOrderStateName: New\nOrderStateOrderFlowId: OF1\n"),
        ("New [03c2e7-1].yml", "OrderStateId: OS8\nOrderStateName: New\nOrderStateOrderFlowId: OF2\n"),
        ("New [03c2e7-2].yml", "OrderStateId: OS11\nOrderStateName: New\nOrderStateOrderFlowId: OF3\n"),
        ("Draft.yml", "OrderStateId: OS5\nOrderStateName: Draft\nOrderStateOrderFlowId: OF2\n"),
        ("Draft [5b1f0a-1].yml", "OrderStateId: OS10\nOrderStateName: Draft\nOrderStateOrderFlowId: OF3\n"),
        ("Rejected.yml", "OrderStateId: OS3\nOrderStateName: Rejected\nOrderStateOrderFlowId: OF1\n"),
        ("Rejected [9c44d2-1].yml", "OrderStateId: OS7\nOrderStateName: Rejected\nOrderStateOrderFlowId: OF2\n"),
    };

    [Fact]
    public void BlankTarget_RepeatedNamesDifferentKeys_EveryRowIsWritten()
    {
        var (provider, writer, inputRoot) = CreateProvider(targetRows: null, RepeatedNameDocuments);
        var written = CaptureWrites(writer);
        var lines = new List<string>();

        var result = provider.Deserialize(Entry, inputRoot, log: lines.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(7, written.Count);
        Assert.Equal(
            new[] { "OS1", "OS10", "OS11", "OS3", "OS5", "OS7", "OS8" },
            written.Select(r => (string)r["OrderStateId"]!).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(7, result.Created);
        Assert.Equal(0, result.Skipped);
        Assert.DoesNotContain(lines, l => l.Contains("later-layer-wins"));
        Assert.DoesNotContain(lines, l => l.Contains("duplicate-identity"));
    }

    [Fact]
    public void BlankTarget_RepeatedNames_EachRowKeepsItsOwnColumns()
    {
        var (provider, writer, inputRoot) = CreateProvider(targetRows: null, RepeatedNameDocuments);
        var written = CaptureWrites(writer);

        provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.SourceWins);

        var byKey = written.ToDictionary(r => (string)r["OrderStateId"]!, StringComparer.Ordinal);
        Assert.Equal("OF1", byKey["OS1"]["OrderStateOrderFlowId"]);
        Assert.Equal("OF2", byKey["OS8"]["OrderStateOrderFlowId"]);
        Assert.Equal("OF3", byKey["OS11"]["OrderStateOrderFlowId"]);
    }

    [Fact]
    public void SameKey_TwoDocuments_StillMergedLaterLayerWins_WithANameColumnSet()
    {
        // Issue #20 is kept: a true same-key pair (a partial override layered over a full row)
        // is one row, even though the documents carry a nameColumn.
        var (provider, writer, inputRoot) = CreateProvider(targetRows: null,
            ("New.yml", "OrderStateId: OS1\nOrderStateName: New\nOrderStateOrderFlowId: OF1\nOrderStateDescription: Base\n"),
            ("zz-override.yml", "OrderStateId: OS1\nOrderStateDescription: Override\n"));
        var written = CaptureWrites(writer);
        var lines = new List<string>();

        provider.Deserialize(Entry, inputRoot, log: lines.Add, strategy: ConflictStrategy.SourceWins);

        var row = Assert.Single(written);
        Assert.Equal("New", row["OrderStateName"]);
        Assert.Equal("Override", row["OrderStateDescription"]);
        Assert.Contains(lines, l => l.Contains("later-layer-wins") && l.Contains("'OS1'"));
    }

    [Fact]
    public void SameName_DifferentKey_IsNotSkipped_AgainstTheTargetRowCarryingThatName()
    {
        // The target holds OS1 'New'. The payload's OS8 'New' has the same content except its key:
        // matched by name it was "unchanged" and skipped, so OS8 never reached the target.
        var target = new[]
        {
            Row("OS1", "New", "OF1", null)
        };
        var (provider, writer, inputRoot) = CreateProvider(target,
            ("New.yml", "OrderStateId: OS1\nOrderStateName: New\nOrderStateOrderFlowId: OF1\nOrderStateDescription: \n"),
            ("New [03c2e7-1].yml", "OrderStateId: OS8\nOrderStateName: New\nOrderStateOrderFlowId: OF1\nOrderStateDescription: \n"));
        var written = CaptureWrites(writer);

        var result = provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.SourceWins);

        var row = Assert.Single(written);
        Assert.Equal("OS8", row["OrderStateId"]);
        Assert.Equal(1, result.Skipped);   // OS1 against OS1: unchanged
    }

    [Fact]
    public void Merge_SameName_DifferentKey_IsNotFilledIntoTheTargetRowCarryingThatName()
    {
        // Under Merge a name match used to fill the TARGET row with that name (OS8) with the
        // payload row's values. OS1 is not on the target, so it is written as its own row.
        var target = new[]
        {
            Row("OS8", "New", "OF2", null)
        };
        var (provider, writer, inputRoot) = CreateProvider(target,
            ("New.yml", "OrderStateId: OS1\nOrderStateName: New\nOrderStateOrderFlowId: OF1\nOrderStateDescription: From payload\n"));
        var written = CaptureWrites(writer);

        provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.DestinationWins);

        var row = Assert.Single(written);
        Assert.Equal("OS1", row["OrderStateId"]);
        writer.Verify(w => w.UpdateColumnSubset(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<Dictionary<string, object?>>(),
            It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public void Merge_SameKey_FillsTheTargetRowWithThatKey()
    {
        var target = new[]
        {
            Row("OS1", "New", "OF1", null)
        };
        var (provider, writer, inputRoot) = CreateProvider(target,
            ("New.yml", "OrderStateId: OS1\nOrderStateName: New\nOrderStateOrderFlowId: OF1\nOrderStateDescription: From payload\n"));
        CaptureWrites(writer);
        Dictionary<string, object?>? filled = null;
        writer.Setup(w => w.UpdateColumnSubset(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<Action<string>?>()))
            .Callback((string _, IReadOnlyList<string> __, Dictionary<string, object?> row, IEnumerable<string> ___, bool ____, Action<string>? _____) =>
                filled = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase))
            .Returns(WriteOutcome.Updated);

        var result = provider.Deserialize(Entry, inputRoot, strategy: ConflictStrategy.DestinationWins);

        Assert.NotNull(filled);
        Assert.Equal("OS1", filled!["OrderStateId"]);
        Assert.Equal("From payload", filled["OrderStateDescription"]);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void KeyIdentity_IgnoresNameColumn_RowIdentityStillNamesTheFile()
    {
        var reader = new SqlTableReader(new Mock<ISqlExecutor>().Object);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrderStateId"] = "OS8",
            ["OrderStateName"] = "New"
        };

        Assert.Equal("OS8", reader.GenerateKeyIdentity(row, Metadata));
        Assert.Equal("New", reader.GenerateRowIdentity(row, Metadata));
    }

    [Fact]
    public void Serialize_RepeatedNames_StillWritesNameBasedFiles()
    {
        // The file naming a layer ships is unchanged: names, with the dedup suffix on a repeat.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"keyid_ser_{Guid.NewGuid():N}");
        _tempDirs.Add(outputRoot);
        var table = OrderStatesTable(Row("OS1", "New", "OF1", null), Row("OS8", "New", "OF2", null));

        var executor = new Mock<ISqlExecutor>();
        executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>())).Returns(() => table.CreateDataReader());
        var metadataReader = new Mock<DataGroupMetadataReader>(executor.Object) { CallBase = false };
        metadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(Metadata);
        var provider = new SqlTableProvider(metadataReader.Object, new SqlTableReader(executor.Object),
            new FlatFileStore(), new SqlTableWriter(executor.Object));

        provider.Serialize(new ProviderPredicateDefinition
        {
            Name = "EcomOrderStates",
            ProviderType = "SqlTable",
            Table = "EcomOrderStates",
            NameColumn = "OrderStateName"
        }, outputRoot);

        var files = Directory.GetFiles(Path.Combine(outputRoot, "_sql", "EcomOrderStates"), "*.yml")
            .Select(Path.GetFileName)
            .Where(f => f != "_meta.yml")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains("New.yml", files);
        Assert.Single(files, f => f!.StartsWith("New [", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private static object?[] Row(string id, string name, string flow, string? description) =>
        new object?[] { id, name, flow, description };

    private static DataTable OrderStatesTable(params object?[][] rows)
    {
        var table = new DataTable("EcomOrderStates");
        foreach (var col in Metadata.AllColumns)
            table.Columns.Add(col, typeof(string));
        foreach (var r in rows)
            table.Rows.Add(r.Select(v => v ?? (object)DBNull.Value).ToArray());
        return table;
    }

    private (SqlTableProvider provider, Mock<SqlTableWriter> writer, string inputRoot) CreateProvider(
        object?[][]? targetRows,
        params (string FileName, string Yaml)[] documents)
    {
        var executor = new Mock<ISqlExecutor>();
        var columnTypes = Metadata.AllColumns.ToDictionary(c => c, _ => "nvarchar", StringComparer.OrdinalIgnoreCase);

        var metadataReader = new Mock<DataGroupMetadataReader>(executor.Object) { CallBase = false };
        metadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(Metadata);
        metadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
        metadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>()))
            .Returns(new Dictionary<string, string>(columnTypes, StringComparer.OrdinalIgnoreCase));
        metadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // The only reader the deserialize opens is the target snapshot (SELECT * FROM the table).
        var target = OrderStatesTable(targetRows ?? Array.Empty<object?[]>());
        executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>())).Returns(() => target.CreateDataReader());

        var tempDir = Path.Combine(Path.GetTempPath(), $"keyid_{Guid.NewGuid():N}");
        _tempDirs.Add(tempDir);
        var tableDir = Path.Combine(tempDir, "_sql", Metadata.TableName);
        Directory.CreateDirectory(tableDir);
        foreach (var (fileName, yaml) in documents)
            File.WriteAllText(Path.Combine(tableDir, fileName), yaml);

        var schemaCache = new TargetSchemaCache(_ =>
            (new HashSet<string>(Metadata.AllColumns, StringComparer.OrdinalIgnoreCase),
             new Dictionary<string, string>(columnTypes, StringComparer.OrdinalIgnoreCase)));

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
}
