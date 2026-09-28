using System.Data;
using System.Globalization;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Foundry #1322: <c>raiseOnlyColumns</c>. <c>EcomNumbers.NumberCounter</c> lagging its tables
/// (OS counter 4 while states run to OS14) makes Dynamicweb mint an existing id and overwrite a
/// row. A counter a layer ships therefore only RAISES the target value: a matched row writes the
/// larger of target and shipped, a new row inserts as shipped, other columns follow the mode,
/// and a lower shipped counter alone is not a change. Harness copied from
/// <see cref="SqlTableProviderMergeModeTests"/>.
/// </summary>
[Trait("Category", "Foundry1322")]
public class SqlTableProviderRaiseOnlyColumnsTests
{
    private static readonly TableMetadata NumbersMetadata = new()
    {
        TableName = "EcomNumbers",
        KeyColumns = new List<string> { "NumberId" },
        IdentityColumns = new List<string>(),
        AllColumns = new List<string> { "NumberId", "NumberDescription", "NumberCounter" }
    };

    private static readonly Dictionary<string, string> NumbersColumnTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NumberId"] = "nvarchar",
            ["NumberDescription"] = "nvarchar",
            ["NumberCounter"] = "int"
        };

    private static SqlTableEntry Entry(params string[] raiseOnlyColumns) => new()
    {
        EntryId = "sql/EcomNumbers",
        Files = Array.Empty<string>(),
        Table = "EcomNumbers",
        RaiseOnlyColumns = raiseOnlyColumns
    };

    private static readonly SqlTableEntry CounterEntry = Entry("NumberCounter");

    // -----------------------------------------------------------------------
    // Replace (SourceWins)
    // -----------------------------------------------------------------------

    [Fact]
    public void Replace_ShippedHigherThanTarget_WritesShipped()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 14) },
            existingDbRows: new[] { Row("OS", "Order states", 4) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var result = provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(1, result.Updated);
        Assert.Equal(14, Counter(Assert.Single(written)));
    }

    [Fact]
    public void Replace_ShippedLowerThanTarget_OnlyDifference_TargetKept_RowSkippedNotUpdated()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var result = provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.SourceWins);

        Assert.Empty(written);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Replace_ShippedLowerThanTarget_OtherColumnDiffers_OtherColumnWritten_CounterKept()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states (new)", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var result = provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(1, result.Updated);
        var row = Assert.Single(written);
        Assert.Equal(14, Counter(row));
        Assert.Equal("Order states (new)", row["NumberDescription"]);
    }

    [Fact]
    public void Replace_ShippedEqualToTarget_Skipped()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 14) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var logs = new List<string>();
        var result = provider.Deserialize(CounterEntry, inputRoot, log: logs.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Empty(written);
        Assert.Equal(1, result.Skipped);
        Assert.Contains("  [EcomNumbers] raiseOnlyColumns NumberCounter: 0 raised, 1 kept (target higher or equal)", logs);
    }

    [Fact]
    public void Replace_NoTargetRow_InsertsAsShipped()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("PAY", "Payments", 3) },
            existingDbRows: Array.Empty<Dictionary<string, object?>>());
        var written = CaptureWriteRow(writer, WriteOutcome.Created);

        var logs = new List<string>();
        var result = provider.Deserialize(CounterEntry, inputRoot, log: logs.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(1, result.Created);
        Assert.Equal(3, Counter(Assert.Single(written)));
        Assert.Contains(logs, l => l.Contains("raiseOnlyColumns NumberCounter: 0 raised, 0 kept")
                                   && l.Contains("1 inserted as shipped"));
    }

    [Fact]
    public void Replace_ShippedNull_TargetSet_TargetKept()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states (new)", null) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.SourceWins);

        var row = Assert.Single(written);
        Assert.Equal(14, Counter(row));
    }

    [Fact]
    public void Replace_TargetNull_ShippedSet_WritesShipped()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 14) },
            existingDbRows: new[] { Row("OS", "Order states", null) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var logs = new List<string>();
        provider.Deserialize(CounterEntry, inputRoot, log: logs.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(14, Counter(Assert.Single(written)));
        Assert.Contains("  [EcomNumbers] raiseOnlyColumns NumberCounter: 1 raised, 0 kept (target higher or equal)", logs);
    }

    [Fact]
    public void Replace_DryRun_ReportsTheEffectiveValue()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states (new)", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var logs = new List<string>();
        provider.Deserialize(CounterEntry, inputRoot, log: logs.Add, isDryRun: true,
            strategy: ConflictStrategy.SourceWins);

        Assert.Equal(14, Counter(Assert.Single(written)));
        writer.Verify(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(),
            true, It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()), Times.Once);
        Assert.Contains(logs, l => l.Contains("OS raiseOnly NumberCounter: shipped 4, target 14 kept"));
    }

    // -----------------------------------------------------------------------
    // The info line
    // -----------------------------------------------------------------------

    [Fact]
    public void InfoLine_SummarisesRaisedAndKept_OneLinePerEntry_NotAWarning()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[]
            {
                Row("OS", "Order states", 14),     // raised from 4
                Row("PAY", "Payments", 1),         // kept 3
                Row("SHIP", "Shipping", 13)        // equal, kept
            },
            existingDbRows: new[]
            {
                Row("OS", "Order states", 4),
                Row("PAY", "Payments", 3),
                Row("SHIP", "Shipping", 13)
            });
        CaptureWriteRow(writer, WriteOutcome.Updated);

        var logs = new List<string>();
        provider.Deserialize(CounterEntry, inputRoot, log: logs.Add, strategy: ConflictStrategy.SourceWins);

        var line = Assert.Single(logs, l => l.Contains("raiseOnlyColumns"));
        Assert.Equal("  [EcomNumbers] raiseOnlyColumns NumberCounter: 1 raised, 2 kept (target higher or equal)", line);
        Assert.DoesNotContain(logs, l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Merge (DestinationWins)
    // -----------------------------------------------------------------------

    [Fact]
    public void Merge_ShippedHigherThanSetTarget_CounterRaised()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 14) },
            existingDbRows: new[] { Row("OS", "Order states", 4) });
        var subsets = CaptureUpdateColumnSubset(writer);

        var result = provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.DestinationWins);

        Assert.Equal(1, result.Updated);
        var (row, columns) = Assert.Single(subsets);
        Assert.Equal(new[] { "NumberCounter" }, columns);
        Assert.Equal(14, Counter(row));
    }

    [Fact]
    public void Merge_ShippedLowerThanTarget_OnlyDifference_Skipped()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var subsets = CaptureUpdateColumnSubset(writer);

        var result = provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.DestinationWins);

        Assert.Empty(subsets);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Merge_ShippedLower_UnsetOtherColumn_FillsOtherColumnOnly_CounterKept()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 4) },
            existingDbRows: new[] { Row("OS", null, 14) });
        var subsets = CaptureUpdateColumnSubset(writer);

        provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.DestinationWins);

        var (row, columns) = Assert.Single(subsets);
        Assert.Equal(new[] { "NumberDescription" }, columns);
        Assert.Equal(14, Counter(row));
    }

    [Fact]
    public void Merge_TargetZero_ShippedHigher_Raised()
    {
        // A blank wizard DB: every counter starts at 0.
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 14) },
            existingDbRows: new[] { Row("OS", "Order states", 0) });
        var subsets = CaptureUpdateColumnSubset(writer);

        provider.Deserialize(CounterEntry, inputRoot, strategy: ConflictStrategy.DestinationWins);

        var (row, columns) = Assert.Single(subsets);
        Assert.Contains("NumberCounter", columns);
        Assert.Equal(14, Counter(row));
    }

    [Fact]
    public void Merge_DryRun_LogsWouldRaise()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 14) },
            existingDbRows: new[] { Row("OS", "Order states", 4) });
        var subsets = CaptureUpdateColumnSubset(writer);

        var logs = new List<string>();
        var result = provider.Deserialize(CounterEntry, inputRoot, log: logs.Add, isDryRun: true,
            strategy: ConflictStrategy.DestinationWins);

        Assert.Empty(subsets);
        Assert.Equal(1, result.Updated);
        Assert.Contains(logs, l => l.Contains("would raise [EcomNumbers.NumberCounter]") && l.Contains("'14'"));
    }

    // -----------------------------------------------------------------------
    // replaceStrategy: truncate reads the target first too
    // -----------------------------------------------------------------------

    [Fact]
    public void Truncate_ReinsertsTheRaisedValue()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        List<Dictionary<string, object?>>? inserted = null;
        writer.Setup(w => w.TruncateAndInsertAll(It.IsAny<List<Dictionary<string, object?>>>(),
                It.IsAny<TableMetadata>(), It.IsAny<bool>(), It.IsAny<Action<string>?>()))
            .Callback((List<Dictionary<string, object?>> rows, TableMetadata _, bool _, Action<string>? _) => inserted = rows)
            .Returns(1);

        provider.Deserialize(CounterEntry with { ReplaceStrategy = "truncate" }, inputRoot,
            strategy: ConflictStrategy.SourceWins);

        Assert.NotNull(inserted);
        Assert.Equal(14, Counter(Assert.Single(inserted!)));
    }

    // -----------------------------------------------------------------------
    // Field absent: behaviour unchanged
    // -----------------------------------------------------------------------

    [Fact]
    public void FieldAbsent_ShippedLowerThanTarget_WrittenAsShipped_NoRaiseOnlyLine()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var logs = new List<string>();
        var result = provider.Deserialize(Entry(), inputRoot, log: logs.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(1, result.Updated);
        Assert.Equal(4, Counter(Assert.Single(written)));
        Assert.DoesNotContain(logs, l => l.Contains("raiseOnly"));
    }

    [Fact]
    public void KeyColumnListed_IsIgnoredWithAnInfoLine()
    {
        var (provider, writer, inputRoot) = CreateProvider(
            yamlRows: new[] { Row("OS", "Order states", 4) },
            existingDbRows: new[] { Row("OS", "Order states", 14) });
        var written = CaptureWriteRow(writer, WriteOutcome.Updated);

        var logs = new List<string>();
        provider.Deserialize(Entry("NumberId"), inputRoot, log: logs.Add, strategy: ConflictStrategy.SourceWins);

        Assert.Equal(4, Counter(Assert.Single(written)));
        Assert.Contains(logs, l => l.Contains("raiseOnlyColumns NumberId ignored"));
        Assert.DoesNotContain(logs, l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // The rule itself: null handling
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(4, 14, 14)]
    [InlineData(14, 4, 14)]
    [InlineData(7, 7, 7)]
    [InlineData(null, 14, 14)]
    [InlineData(14, null, 14)]
    [InlineData(null, null, null)]
    public void Rule_TakesTheLargerValue_NullNeverWins(int? shipped, int? target, int? expected)
    {
        var rule = new RaiseOnlyColumns(new[] { "NumberCounter" });
        var incoming = Row("OS", "x", shipped);

        rule.Apply(incoming, Row("OS", "x", target));

        Assert.Equal(expected, incoming["NumberCounter"] is null ? null : Convert.ToInt32(incoming["NumberCounter"]));
    }

    [Fact]
    public void Rule_NoTargetRow_LeavesShippedAndCountsAnInsert()
    {
        var rule = new RaiseOnlyColumns(new[] { "NumberCounter" });
        var incoming = Row("OS", "x", 3);

        rule.Apply(incoming, target: null);

        Assert.Equal(3, incoming["NumberCounter"]);
        Assert.Equal((0, 0, 1), rule.CountsFor("NumberCounter"));
    }

    [Fact]
    public void Rule_ComparesAcrossNumericRepresentations()
    {
        var rule = new RaiseOnlyColumns(new[] { "NumberCounter" });
        var incoming = Row("OS", "x", null);
        incoming["NumberCounter"] = "9";           // a YAML scalar before coercion
        var target = Row("OS", "x", null);
        target["NumberCounter"] = 10L;              // a bigint read back from SQL

        rule.Apply(incoming, target);

        Assert.Equal(10L, incoming["NumberCounter"]);
    }

    #region Helpers

    private static Dictionary<string, object?> Row(string id, string? description, int? counter)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["NumberId"] = id,
            ["NumberDescription"] = description,
            ["NumberCounter"] = counter
        };

    private static int? Counter(Dictionary<string, object?> row) =>
        row.TryGetValue("NumberCounter", out var v) && v is not null
            ? Convert.ToInt32(v, CultureInfo.InvariantCulture)
            : null;

    private static List<Dictionary<string, object?>> CaptureWriteRow(Mock<SqlTableWriter> writer, WriteOutcome outcome)
    {
        var written = new List<Dictionary<string, object?>>();
        writer.Setup(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(),
                It.IsAny<bool>(), It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()))
            .Callback((Dictionary<string, object?> row, TableMetadata _, bool _, Action<string>? _, HashSet<string>? _) =>
                written.Add(new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase)))
            .Returns(outcome);
        return written;
    }

    private static List<(Dictionary<string, object?> Row, List<string> Columns)> CaptureUpdateColumnSubset(
        Mock<SqlTableWriter> writer)
    {
        var calls = new List<(Dictionary<string, object?>, List<string>)>();
        writer.Setup(w => w.UpdateColumnSubset(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<Dictionary<string, object?>>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(), It.IsAny<Action<string>?>()))
            .Callback((string _, IReadOnlyList<string> _, Dictionary<string, object?> row, IEnumerable<string> cols,
                    bool _, Action<string>? _) =>
                calls.Add((new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase), cols.ToList())))
            .Returns(WriteOutcome.Updated);
        return calls;
    }

    private static (SqlTableProvider provider, Mock<SqlTableWriter> writer, string inputRoot)
        CreateProvider(
            IEnumerable<Dictionary<string, object?>> yamlRows,
            IEnumerable<Dictionary<string, object?>> existingDbRows)
    {
        var meta = NumbersMetadata;
        var types = NumbersColumnTypes;

        var mockExecutor = new Mock<ISqlExecutor>();
        var mockMetadataReader = new Mock<DataGroupMetadataReader>(mockExecutor.Object) { CallBase = false };
        mockMetadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(meta);
        mockMetadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
        mockMetadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>())).Returns(types);
        mockMetadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var existingList = existingDbRows.ToList();
        var dbReaderMock = CreateMockDataReader(
            meta.AllColumns.ToArray(),
            existingList.Select(r => meta.AllColumns
                .Select(col => r.TryGetValue(col, out var v) ? v ?? DBNull.Value : DBNull.Value)
                .ToArray()).ToArray());
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(dbReaderMock.Object);

        var tableReader = new SqlTableReader(mockExecutor.Object);
        var fileStore = new FlatFileStore();

        var tempDir = Path.Combine(Path.GetTempPath(), $"contentsync_raiseonly_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var identityReader = new SqlTableReader(new Mock<ISqlExecutor>().Object);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in yamlRows)
            fileStore.WriteRow(tempDir, meta.TableName, identityReader.GenerateRowIdentity(row, meta), row, usedNames);
        fileStore.WriteMeta(tempDir, meta.TableName, meta);

        var writerMock = new Mock<SqlTableWriter>(mockExecutor.Object) { CallBase = false };
        var schemaCache = new TargetSchemaCache(_ =>
            (new HashSet<string>(meta.AllColumns, StringComparer.OrdinalIgnoreCase),
             new Dictionary<string, string>(types, StringComparer.OrdinalIgnoreCase)));
        var provider = new SqlTableProvider(
            mockMetadataReader.Object, tableReader, fileStore, writerMock.Object, schemaCache);

        return (provider, writerMock, tempDir);
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
