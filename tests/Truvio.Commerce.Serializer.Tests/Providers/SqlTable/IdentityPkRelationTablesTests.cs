using System.Data;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// LRN-hosted-publish-10: identity-PK relation rows. Auto-ids are environment-local; a payload
/// carrying explicit auto-ids collided with the target's rows and the relation rows silently
/// never landed (0 failed reported; add-to-cart refused). Known relation tables must
/// match/insert by natural key with the target assigning the auto-id; unknown identity-PK
/// relation tables must WARN on an auto-id collision.
/// </summary>
public class IdentityPkRelationTablesTests
{
    private static TableMetadata VariantRelationMetadata(
        List<string>? keyColumns = null,
        List<string>? identityColumns = null,
        List<string>? allColumns = null) => new()
    {
        TableName = "EcomVariantOptionsProductRelation",
        KeyColumns = keyColumns ?? ["VariantOptionsProductRelationAutoId"],
        IdentityColumns = identityColumns ?? ["VariantOptionsProductRelationAutoId"],
        AllColumns = allColumns ??
        [
            "VariantOptionsProductRelationAutoId",
            "VariantOptionsProductRelationProductID",
            "VariantOptionsProductRelationVariantID"
        ]
    };

    // === Knowledge-base guards ===

    [Fact]
    public void GetNaturalKey_VariantCombinationTable_IdentityOnlyPk_ReturnsProductVariantPair()
    {
        var naturalKey = IdentityPkRelationTables.GetNaturalKey(VariantRelationMetadata());

        Assert.NotNull(naturalKey);
        Assert.Equal(2, naturalKey!.Count);
        Assert.Contains("VariantOptionsProductRelationProductID", naturalKey);
        Assert.Contains("VariantOptionsProductRelationVariantID", naturalKey);
    }

    [Fact]
    public void GetNaturalKey_PkIsNaturalPairAlready_ReturnsNull_LegacyPathCorrect()
    {
        // On schemas where the PK is already the composite natural pair, nothing must change.
        var metadata = VariantRelationMetadata(
            keyColumns: ["VariantOptionsProductRelationProductID", "VariantOptionsProductRelationVariantID"]);

        Assert.Null(IdentityPkRelationTables.GetNaturalKey(metadata));
    }

    [Fact]
    public void GetNaturalKey_UnknownTable_ReturnsNull()
    {
        var metadata = new TableMetadata
        {
            TableName = "EcomSomeOtherRelation",
            KeyColumns = ["AutoId"],
            IdentityColumns = ["AutoId"],
            AllColumns = ["AutoId", "AId", "BId"]
        };

        Assert.Null(IdentityPkRelationTables.GetNaturalKey(metadata));
    }

    [Fact]
    public void GetNaturalKey_MappedColumnMissingOnLiveSchema_ReturnsNull_SafeFallback()
    {
        // Schema-variance guard: if the live table doesn't carry the mapped natural-key
        // columns, the mapping must deactivate rather than emit broken SQL.
        var metadata = VariantRelationMetadata(
            allColumns: ["VariantOptionsProductRelationAutoId", "SomeRenamedColumn"]);

        Assert.Null(IdentityPkRelationTables.GetNaturalKey(metadata));
    }

    [Fact]
    public void IsIdentityOnlyPk_TrueForAutoIdPk_FalseForNaturalPk_FalseForKeyless()
    {
        Assert.True(IdentityPkRelationTables.IsIdentityOnlyPk(VariantRelationMetadata()));
        Assert.False(IdentityPkRelationTables.IsIdentityOnlyPk(VariantRelationMetadata(
            keyColumns: ["VariantOptionsProductRelationProductID", "VariantOptionsProductRelationVariantID"])));
        Assert.False(IdentityPkRelationTables.IsIdentityOnlyPk(VariantRelationMetadata(
            keyColumns: [])));
    }

    // === MERGE shape in natural-key mode ===

    [Fact]
    public void BuildMergeCommand_NaturalKeyMode_MatchesOnPair_NoIdentityInsert_TargetAssignsAutoId()
    {
        // The provider transformation: KeyColumns become the natural pair, the identity column
        // is stripped from the row. The resulting MERGE must match on the pair, never write
        // the auto-id, and not toggle IDENTITY_INSERT.
        var metadata = VariantRelationMetadata() with
        {
            KeyColumns = ["VariantOptionsProductRelationProductID", "VariantOptionsProductRelationVariantID"]
        };
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["VariantOptionsProductRelationProductID"] = "PROD1",
            ["VariantOptionsProductRelationVariantID"] = "VO3"
            // auto-id stripped by the provider
        };

        var writer = new SqlTableWriter(new Mock<ISqlExecutor>().Object);
        var sql = writer.BuildMergeCommand(row, metadata).ToString();

        Assert.DoesNotContain("IDENTITY_INSERT", sql);
        Assert.DoesNotContain("VariantOptionsProductRelationAutoId", sql);
        Assert.Contains("target.[VariantOptionsProductRelationProductID] = source.[VariantOptionsProductRelationProductID]", sql);
        Assert.Contains("target.[VariantOptionsProductRelationVariantID] = source.[VariantOptionsProductRelationVariantID]", sql);
    }

    // === Provider-level behavior ===

    [Fact]
    public void Deserialize_VariantCombinationTable_CollidingAutoIds_InsertByNaturalKey_AutoIdStripped()
    {
        // Payload rows carry auto-ids 1..2 which collide with the target's own (stock) rows.
        // The provider must switch to natural-key identity: the pairs don't exist on target,
        // so both rows write — WITHOUT the auto-id column, with natural-key KeyColumns.
        var yamlRows = new[]
        {
            Row(("VariantOptionsProductRelationAutoId", 1), ("VariantOptionsProductRelationProductID", "PROD1"), ("VariantOptionsProductRelationVariantID", "VO1")),
            Row(("VariantOptionsProductRelationAutoId", 2), ("VariantOptionsProductRelationProductID", "PROD1"), ("VariantOptionsProductRelationVariantID", "VO2"))
        };
        var existingDbRows = new[]
        {
            Row(("VariantOptionsProductRelationAutoId", 1), ("VariantOptionsProductRelationProductID", "BIKE1"), ("VariantOptionsProductRelationVariantID", "COLOR1")),
            Row(("VariantOptionsProductRelationAutoId", 2), ("VariantOptionsProductRelationProductID", "BIKE1"), ("VariantOptionsProductRelationVariantID", "COLOR2"))
        };

        var (provider, writer, inputRoot, logs) = CreateProvider(VariantRelationMetadata(), yamlRows, existingDbRows);

        var writtenRows = new List<Dictionary<string, object?>>();
        var writtenMetadata = new List<TableMetadata>();
        writer.Setup(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(), false, It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()))
            .Callback((Dictionary<string, object?> r, TableMetadata m, bool _, Action<string>? _, HashSet<string>? _) =>
            {
                writtenRows.Add(r);
                writtenMetadata.Add(m);
            })
            .Returns(WriteOutcome.Created);

        var result = provider.Deserialize(VariantRelationEntry, inputRoot, log: logs.Add);

        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, writtenRows.Count);
        Assert.All(writtenRows, r => Assert.False(r.ContainsKey("VariantOptionsProductRelationAutoId"),
            "auto-id must be stripped so the target assigns its own"));
        Assert.All(writtenMetadata, m =>
        {
            Assert.Contains("VariantOptionsProductRelationProductID", m.KeyColumns);
            Assert.Contains("VariantOptionsProductRelationVariantID", m.KeyColumns);
            Assert.DoesNotContain("VariantOptionsProductRelationAutoId", m.KeyColumns);
        });
        Assert.Contains(logs, l => l.Contains("matching rows by natural key"));
        // Natural-key mode is the fix, not the symptom: no collision WARNING should fire.
        Assert.DoesNotContain(logs, l => l.Contains("WARNING") && l.Contains("auto-id collision"));

        Cleanup(inputRoot);
    }

    [Fact]
    public void Deserialize_VariantCombinationTable_PairAlreadyOnTargetUnderDifferentAutoId_Skips()
    {
        // The same relation (PROD1, VO1) exists on target under a different auto-id.
        // Natural-key identity makes this an unchanged row: skip, no write, no duplicate.
        var yamlRows = new[]
        {
            Row(("VariantOptionsProductRelationAutoId", 7), ("VariantOptionsProductRelationProductID", "PROD1"), ("VariantOptionsProductRelationVariantID", "VO1"))
        };
        var existingDbRows = new[]
        {
            Row(("VariantOptionsProductRelationAutoId", 3), ("VariantOptionsProductRelationProductID", "PROD1"), ("VariantOptionsProductRelationVariantID", "VO1"))
        };

        var (provider, writer, inputRoot, logs) = CreateProvider(VariantRelationMetadata(), yamlRows, existingDbRows);

        var result = provider.Deserialize(VariantRelationEntry, inputRoot, log: logs.Add);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Failed);
        writer.Verify(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(), It.IsAny<bool>(), It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()), Times.Never);

        Cleanup(inputRoot);
    }

    [Fact]
    public void Deserialize_UnmappedIdentityPkRelationTable_AutoIdCollision_Warns()
    {
        // Identity-PK table named like a relation table but NOT in the knowledge base:
        // an auto-id collision (same auto-id, different content) must WARN — the silent
        // "0 failed" class is what made LRN-10 invisible. Strict mode escalates WARNINGs.
        var metadata = new TableMetadata
        {
            TableName = "EcomCustomProductRelation",
            KeyColumns = ["CustomRelationAutoId"],
            IdentityColumns = ["CustomRelationAutoId"],
            AllColumns = ["CustomRelationAutoId", "AId", "BId"]
        };
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomCustomProductRelation",
            Files = Array.Empty<string>(),
            Table = "EcomCustomProductRelation"
        };
        var yamlRows = new[] { Row(("CustomRelationAutoId", 1), ("AId", "X"), ("BId", "Y")) };
        var existingDbRows = new[] { Row(("CustomRelationAutoId", 1), ("AId", "OTHER"), ("BId", "ROW")) };

        var (provider, writer, inputRoot, logs) = CreateProvider(metadata, yamlRows, existingDbRows);
        writer.Setup(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(), false, It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()))
            .Returns(WriteOutcome.Updated);

        provider.Deserialize(entry, inputRoot, log: logs.Add);

        var warning = Assert.Single(logs, l => l.Contains("auto-id collision"));
        Assert.Contains("WARNING", warning);
        Assert.Contains("EcomCustomProductRelation", warning);

        Cleanup(inputRoot);
    }

    [Fact]
    public void Deserialize_UnmappedIdentityPkRelationTable_WithNameColumn_AutoIdCollision_StillWarns()
    {
        // Engine issue #38: row identity is the key whether or not a nameColumn is set, so the
        // write binds by the auto-id and a nameColumn no longer exempts the table from the
        // collision warning (it used to, because the name was the identity).
        var metadata = new TableMetadata
        {
            TableName = "EcomCustomProductRelation",
            NameColumn = "AId",
            KeyColumns = ["CustomRelationAutoId"],
            IdentityColumns = ["CustomRelationAutoId"],
            AllColumns = ["CustomRelationAutoId", "AId", "BId"]
        };
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomCustomProductRelation",
            Files = Array.Empty<string>(),
            Table = "EcomCustomProductRelation",
            NameColumn = "AId"
        };
        var yamlRows = new[] { Row(("CustomRelationAutoId", 1), ("AId", "X"), ("BId", "Y")) };
        var existingDbRows = new[] { Row(("CustomRelationAutoId", 1), ("AId", "OTHER"), ("BId", "ROW")) };

        var (provider, writer, inputRoot, logs) = CreateProvider(metadata, yamlRows, existingDbRows);
        writer.Setup(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(), false, It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()))
            .Returns(WriteOutcome.Updated);

        provider.Deserialize(entry, inputRoot, log: logs.Add);

        Assert.Contains("WARNING", Assert.Single(logs, l => l.Contains("auto-id collision")));

        Cleanup(inputRoot);
    }

    [Fact]
    public void Deserialize_NonRelationIdentityPkTable_Collision_DoesNotWarn()
    {
        // Same collision shape on a non-relation identity-PK table (e.g. EcomOrderFlow):
        // a changed row under the same auto-id is the normal update path — no warning spam.
        var metadata = new TableMetadata
        {
            TableName = "EcomOrderFlow",
            KeyColumns = ["OrderFlowId"],
            IdentityColumns = ["OrderFlowId"],
            AllColumns = ["OrderFlowId", "OrderFlowName", "OrderFlowDescription"]
        };
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomOrderFlow",
            Files = Array.Empty<string>(),
            Table = "EcomOrderFlow"
        };
        var yamlRows = new[] { Row(("OrderFlowId", 1), ("OrderFlowName", "Checkout"), ("OrderFlowDescription", "new")) };
        var existingDbRows = new[] { Row(("OrderFlowId", 1), ("OrderFlowName", "Checkout"), ("OrderFlowDescription", "old")) };

        var (provider, writer, inputRoot, logs) = CreateProvider(metadata, yamlRows, existingDbRows);
        writer.Setup(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(), false, It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()))
            .Returns(WriteOutcome.Updated);

        var result = provider.Deserialize(entry, inputRoot, log: logs.Add);

        Assert.Equal(1, result.Updated);
        Assert.DoesNotContain(logs, l => l.Contains("auto-id collision"));

        Cleanup(inputRoot);
    }

    #region Fixture helpers

    private static readonly SqlTableEntry VariantRelationEntry = new()
    {
        EntryId = "sql/EcomVariantOptionsProductRelation",
        Files = Array.Empty<string>(),
        Table = "EcomVariantOptionsProductRelation"
    };

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in cells)
            row[key] = value;
        return row;
    }

    private static void Cleanup(string inputRoot)
    {
        try { Directory.Delete(inputRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Wire a provider around the given metadata with YAML rows on disk and mocked existing
    /// DB rows — same pattern as <see cref="SqlTableProviderDeserializeTests"/>, parametrised
    /// over the table shape.
    /// </summary>
    private static (SqlTableProvider provider, Mock<SqlTableWriter> writer, string inputRoot, List<string> logs)
        CreateProvider(
            TableMetadata metadata,
            IEnumerable<Dictionary<string, object?>> yamlRows,
            IEnumerable<Dictionary<string, object?>> existingDbRows)
    {
        var mockExecutor = new Mock<ISqlExecutor>();

        var mockMetadataReader = new Mock<DataGroupMetadataReader>(mockExecutor.Object) { CallBase = false };
        mockMetadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>()))
            .Returns(metadata);
        mockMetadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
        mockMetadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>()))
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        mockMetadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var columns = metadata.AllColumns.ToArray();
        var existingList = existingDbRows.ToList();
        var dbReaderMock = CreateMockDataReader(
            columns,
            existingList.Select(r => columns.Select(c => r.GetValueOrDefault(c) ?? DBNull.Value).ToArray()).ToArray());
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(dbReaderMock.Object);

        var tableReader = new SqlTableReader(mockExecutor.Object);
        var fileStore = new FlatFileStore();

        var tempDir = Path.Combine(Path.GetTempPath(), $"truvio_lrn10_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var identityReader = new SqlTableReader(new Mock<ISqlExecutor>().Object);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in yamlRows)
        {
            var identity = identityReader.GenerateRowIdentity(row, metadata);
            fileStore.WriteRow(tempDir, metadata.TableName, identity, row, usedNames);
        }
        fileStore.WriteMeta(tempDir, metadata.TableName, metadata);

        var writerMock = new Mock<SqlTableWriter>(mockExecutor.Object) { CallBase = false };

        var schemaCache = new TargetSchemaCache(_ =>
            (new HashSet<string>(metadata.AllColumns, StringComparer.OrdinalIgnoreCase),
             new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var provider = new SqlTableProvider(
            mockMetadataReader.Object, tableReader, fileStore, writerMock.Object, schemaCache);

        return (provider, writerMock, tempDir, new List<string>());
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
