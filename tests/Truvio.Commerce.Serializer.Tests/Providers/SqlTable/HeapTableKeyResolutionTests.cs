using System.Data;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Foundry #1305: a table with no PRIMARY KEY took a truncate-and-insert path in EVERY mode,
/// so a Merge of one DynamicStructures row deleted the host's other Dynamic Workspaces and the
/// run still reported "1 created, 0 failed". These tests pin the fix: a resolved match key, an
/// upsert in both modes, and no DELETE without the explicit replaceStrategy opt-in.
///
/// <para>
/// The writer here is the real <see cref="SqlTableWriter"/> over a mocked
/// <see cref="ISqlExecutor"/>, so every statement the run composes is observable. The existing
/// SqlTable suites mock the writer, which is exactly why the truncate never showed up in a test.
/// </para>
/// </summary>
[Trait("Category", "HeapKeyResolution")]
public class HeapTableKeyResolutionTests
{
    private const string Table = "DynamicStructures";

    private static readonly string[] Columns =
        ["DynamicStructureId", "DynamicStructureUniqueId", "DynamicStructureTitle"];

    /// <summary>The live DW 10.28 shape: an identity column and no primary-key index.</summary>
    private static TableMetadata Heap => new()
    {
        TableName = Table,
        KeyColumns = [],
        IdentityColumns = ["DynamicStructureId"],
        AllColumns = [.. Columns]
    };

    /// <summary>The same table on a platform version that declares a primary key.</summary>
    private static TableMetadata Keyed => Heap with { KeyColumns = ["DynamicStructureId"] };

    private static Dictionary<string, object?> Row(int id, string uniqueId, string title) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DynamicStructureId"] = id,
            ["DynamicStructureUniqueId"] = uniqueId,
            ["DynamicStructureTitle"] = title
        };

    // ---------- the matrix: {PK, declared keyColumns, unique index, keyless heap} x {Replace, Merge} ----------

    public static IEnumerable<object[]> KeyShapeByMode()
    {
        foreach (var shape in new[] { "PrimaryKey", "DeclaredKeyColumns", "UniqueIndex", "KeylessHeap" })
        {
            yield return [shape, ConflictStrategy.SourceWins];
            yield return [shape, ConflictStrategy.DestinationWins];
        }
    }

    [Theory]
    [MemberData(nameof(KeyShapeByMode))]
    public void EveryKeyShape_InEveryMode_IssuesNoDelete(string shape, ConflictStrategy strategy)
    {
        var (metadata, entry, uniqueIndexes) = ShapeFixture(shape);

        var harness = new Harness(
            metadata,
            entry,
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: [Row(1, "bikes", "Bikes - sorted by Brand & Type")],
            uniqueIndexes: uniqueIndexes);

        var result = harness.Deserialize(strategy);

        Assert.Empty(result.Errors);
        Assert.Equal(0, result.Deleted);
        Assert.DoesNotContain(harness.Statements, s => s.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(KeyShapeByMode))]
    public void EveryInferredKeyShape_WarnsWithTheResolution(string shape, ConflictStrategy strategy)
    {
        var (metadata, entry, uniqueIndexes) = ShapeFixture(shape);

        var harness = new Harness(
            metadata,
            entry,
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: [],
            uniqueIndexes: uniqueIndexes);

        var result = harness.Deserialize(strategy);

        if (shape == "PrimaryKey")
        {
            // A declared key stays silent, because nothing was inferred.
            Assert.Empty(result.Warnings);
            return;
        }

        if (shape == "DeclaredKeyColumns")
        {
            // Engine issue #30: an entry-declared key is deliberate, so no warning reaches the
            // strict-mode escalator; the resolution is still logged as an info line.
            Assert.Empty(result.Warnings);
            Assert.DoesNotContain(harness.LogLines, l => l.Contains("WARNING"));
            Assert.Contains(harness.LogLines, l =>
                l.Contains("[" + Table + "] has no primary key; rows matched by the declared keyColumns (DynamicStructureUniqueId)"));
            return;
        }

        var expectedResolution = shape switch
        {
            "UniqueIndex" => "unique index (UX_DynamicStructures_UniqueId)",
            _ => "all columns"
        };

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(
            $"[{Table}] has no primary key; rows matched by {expectedResolution}; " +
            "target rows not in the payload are preserved.",
            warning);

        // The WARNING prefix is what the orchestrator's strict-mode escalator keys on.
        Assert.Contains(harness.LogLines, l => l.TrimStart().StartsWith("WARNING: [" + Table + "] has no primary key"));
    }

    private static (TableMetadata Metadata, SqlTableEntry Entry, List<UniqueIndexDefinition> Indexes) ShapeFixture(
        string shape) => shape switch
    {
        "PrimaryKey" => (Keyed, BaseEntry(), []),
        "DeclaredKeyColumns" => (
            Heap,
            BaseEntry() with { KeyColumns = ["DynamicStructureUniqueId"] },
            []),
        "UniqueIndex" => (
            Heap,
            BaseEntry(),
            [new UniqueIndexDefinition
            {
                Name = "UX_DynamicStructures_UniqueId",
                Columns = ["DynamicStructureUniqueId"]
            }]),
        "KeylessHeap" => (Heap, BaseEntry(), []),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown key shape")
    };

    private static SqlTableEntry BaseEntry() => new()
    {
        EntryId = $"sql/{Table}",
        Files = [],
        Table = Table
    };

    // ---------- the #1305 regression ----------

    [Fact]
    public void Merge_OnAHeap_KeepsThePreExistingRowsAndAddsThePayloadRow()
    {
        // The field case: the host carried three workspaces, the layer carried one.
        var existing = new[]
        {
            Row(1, "bikes-brand-type", "Bikes - sorted by Brand & Type"),
            Row(3, "bikes-type-additionals", "Bikes - sorted by Type & Additionals")
        };

        var harness = new Harness(
            Heap,
            BaseEntry(),
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: existing);

        var result = harness.Deserialize(ConflictStrategy.DestinationWins);

        // Nothing was deleted, so the two pre-existing rows plus the one inserted row is three.
        Assert.Equal(0, result.Deleted);
        Assert.DoesNotContain(harness.Statements, s => s.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Failed);
        Assert.Equal(existing.Length + result.Created, 3);

        // The one write is a MERGE that only inserts on a non-match.
        var merge = Assert.Single(harness.Statements, s => s.Contains("MERGE ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", merge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON ()", merge);
    }

    [Fact]
    public void Merge_OnAHeap_DoesNotForceIdentityInsert()
    {
        var harness = new Harness(
            Heap,
            BaseEntry(),
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: []);

        harness.Deserialize(ConflictStrategy.DestinationWins);

        // Auto-ids are environment-local: the target assigns its own.
        Assert.DoesNotContain(harness.Statements, s => s.Contains("IDENTITY_INSERT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllColumnsMatch_SkipsAnIdenticalRow()
    {
        var row = Row(100170, "truvio-pim", "Truvio PIM - by data model");

        var harness = new Harness(
            Heap,
            BaseEntry(),
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: [row]);

        var result = harness.Deserialize(ConflictStrategy.SourceWins);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Empty(harness.Statements.Where(s => s.Contains("MERGE ", StringComparison.OrdinalIgnoreCase)));
    }

    // ---------- replaceStrategy: truncate ----------

    [Fact]
    public void ReplaceStrategyTruncate_UnderReplace_IssuesExactlyOneDelete()
    {
        var harness = new Harness(
            Heap,
            BaseEntry() with { ReplaceStrategy = "truncate" },
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: [Row(1, "bikes", "Bikes")],
            deleteRowCount: 2);

        var result = harness.Deserialize(ConflictStrategy.SourceWins);

        var deletes = harness.Statements
            .Where(s => s.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(deletes);
        Assert.Contains($"DELETE FROM [{Table}]", deletes[0]);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.Created);
        Assert.Contains(result.Warnings, w => w.Contains("replaceStrategy: truncate"));
    }

    [Fact]
    public void ReplaceStrategyTruncate_UnderMerge_IsIgnoredWithAWarning()
    {
        var harness = new Harness(
            Heap,
            BaseEntry() with { ReplaceStrategy = "truncate" },
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: [Row(1, "bikes", "Bikes")]);

        var result = harness.Deserialize(ConflictStrategy.DestinationWins);

        Assert.DoesNotContain(harness.Statements, s => s.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, result.Deleted);
        Assert.Contains(result.Warnings, w => w.Contains("ignored under Merge"));
    }

    [Fact]
    public void ReplaceStrategy_UnknownValue_FailsTheEntry()
    {
        var harness = new Harness(
            Heap,
            BaseEntry() with { ReplaceStrategy = "wipe" },
            yamlRows: [Row(100170, "truvio-pim", "Truvio PIM - by data model")],
            existingDbRows: []);

        var result = harness.Deserialize(ConflictStrategy.SourceWins);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("replaceStrategy 'wipe'"));
        Assert.Empty(harness.Statements);
    }

    // ---------- the empty-key guard ----------

    [Fact]
    public void BuildMergeCommand_WithNoKeyColumns_ThrowsInsteadOfEmittingOnParens()
    {
        var writer = new SqlTableWriter(new Mock<ISqlExecutor>().Object);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            writer.BuildMergeCommand(Row(1, "a", "b"), Heap));

        Assert.Contains("key column list is empty", ex.Message);
        Assert.Contains(Table, ex.Message);
    }

    // ---------- the harness ----------

    /// <summary>
    /// Wires a provider with the REAL <see cref="SqlTableWriter"/> so every composed statement
    /// lands in <see cref="Statements"/>. Readers are dispatched by SQL substring, the style the
    /// rest of the SqlTable suite uses.
    /// </summary>
    private sealed class Harness
    {
        private readonly SqlTableProvider _provider;
        private readonly SqlTableEntry _entry;
        private readonly string _inputRoot;

        public List<string> Statements { get; } = [];
        public List<string> LogLines { get; } = [];

        public Harness(
            TableMetadata metadata,
            SqlTableEntry entry,
            IEnumerable<Dictionary<string, object?>> yamlRows,
            IEnumerable<Dictionary<string, object?>> existingDbRows,
            IReadOnlyList<UniqueIndexDefinition>? uniqueIndexes = null,
            int deleteRowCount = 0)
        {
            _entry = entry;

            var existing = existingDbRows.ToList();
            var executor = new Mock<ISqlExecutor>();

            executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
                .Returns((CommandBuilder cb) =>
                {
                    var sql = cb.ToString();
                    // RowExistsInTarget: answered as "no such row" so a payload row counts as
                    // Created. The test bodies assert on statements, not on that split.
                    if (sql.Contains("SELECT 1 FROM", StringComparison.OrdinalIgnoreCase))
                        return CreateReader(metadata.AllColumns, []).Object;
                    return CreateReader(metadata.AllColumns, existing).Object;
                });

            executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
                .Returns((CommandBuilder cb) =>
                {
                    var sql = cb.ToString();
                    Statements.Add(sql);
                    return sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) ? deleteRowCount : 1;
                });

            var metadataReader = new Mock<DataGroupMetadataReader>(executor.Object) { CallBase = false };
            metadataReader
                .Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
                .Returns(metadata);
            metadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
            metadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>()))
                .Returns(ColumnTypes());
            metadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
                .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            metadataReader.Setup(x => x.GetUniqueIndexes(It.IsAny<string>()))
                .Returns((uniqueIndexes ?? []).ToList());

            var tableReader = new SqlTableReader(executor.Object);
            var fileStore = new FlatFileStore();

            _inputRoot = Path.Combine(Path.GetTempPath(), $"heapkey_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_inputRoot);

            var identityReader = new SqlTableReader(new Mock<ISqlExecutor>().Object);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in yamlRows)
            {
                var identity = identityReader.GenerateRowIdentity(row, metadata);
                fileStore.WriteRow(_inputRoot, metadata.TableName, identity, row, usedNames);
            }
            fileStore.WriteMeta(_inputRoot, metadata.TableName, metadata);

            var schemaCache = new TargetSchemaCache(_ =>
                (new HashSet<string>(metadata.AllColumns, StringComparer.OrdinalIgnoreCase), ColumnTypes()));

            _provider = new SqlTableProvider(
                metadataReader.Object,
                tableReader,
                fileStore,
                new SqlTableWriter(executor.Object),
                schemaCache);
        }

        public ProviderDeserializeResult Deserialize(ConflictStrategy strategy) =>
            _provider.Deserialize(_entry, _inputRoot, LogLines.Add, isDryRun: false, strategy);

        private static Dictionary<string, string> ColumnTypes() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["DynamicStructureId"] = "int",
            ["DynamicStructureUniqueId"] = "nvarchar",
            ["DynamicStructureTitle"] = "nvarchar"
        };
    }

    private static Mock<IDataReader> CreateReader(
        IReadOnlyList<string> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var mock = new Mock<IDataReader>();
        var rowIndex = -1;

        mock.Setup(r => r.Read()).Returns(() =>
        {
            rowIndex++;
            return rowIndex < rows.Count;
        });

        mock.Setup(r => r.FieldCount).Returns(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            var idx = i;
            mock.Setup(r => r.GetName(idx)).Returns(columns[idx]);
            mock.Setup(r => r.GetValue(idx)).Returns(() =>
                rowIndex >= 0 && rowIndex < rows.Count
                    ? rows[rowIndex].GetValueOrDefault(columns[idx]) ?? DBNull.Value
                    : DBNull.Value);
        }

        mock.Setup(r => r[It.IsAny<string>()]).Returns((string col) =>
            rowIndex >= 0 && rowIndex < rows.Count
                ? rows[rowIndex].GetValueOrDefault(col) ?? DBNull.Value
                : DBNull.Value);

        mock.Setup(r => r.Dispose());
        return mock;
    }
}
