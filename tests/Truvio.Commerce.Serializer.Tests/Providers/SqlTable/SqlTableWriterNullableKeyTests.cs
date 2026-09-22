using System.Data;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// PR #22 review finding (KeyResolution all-columns fallback): an inferred key on a table with
/// no primary key can include a NULLABLE column. A NULL in it was bound as '', so the row never
/// matched its NULL target row and a changed row inserted a duplicate on every Replace. With the
/// live NOT NULL set known, a NULL key on a nullable column now matches with IS NULL and inserts
/// NULL. Without the set, or on a NOT NULL column, the historical '' binding is unchanged.
/// </summary>
[Trait("Category", "Issue21")]
public class SqlTableWriterNullableKeyTests
{
    private static TableMetadata HeapMetadata() => new()
    {
        TableName = "HeapTable",
        KeyColumns = new List<string> { "Name", "Note" },   // the all-columns fallback
        IdentityColumns = new List<string>(),
        AllColumns = new List<string> { "Name", "Note" }
    };

    private static HashSet<string> NotNull(params string[] cols) => new(cols, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> Row(object? note) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = "Alpha",
        ["Note"] = note
    };

    [Fact]
    public void BuildMergeCommand_NullOnNullableKey_MatchesWithIsNull_AndInsertsNull()
    {
        var writer = new SqlTableWriter(new Mock<ISqlExecutor>().Object);

        var sql = writer.BuildMergeCommand(Row(null), HeapMetadata(), NotNull("Name")).ToString();

        Assert.Contains("target.[Note] IS NULL", sql);
        Assert.DoesNotContain("target.[Note] = source.[Note]", sql);
        Assert.Contains("target.[Name] = source.[Name]", sql);
        Assert.Contains("VALUES( ISNULL(source.[Name], ''),NULL )", sql);
    }

    [Fact]
    public void BuildMergeCommand_NullableKeyAbsentFromRow_MatchesWithIsNull_AndInsertsNull()
    {
        var writer = new SqlTableWriter(new Mock<ISqlExecutor>().Object);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "Alpha" };

        var sql = writer.BuildMergeCommand(row, HeapMetadata(), NotNull("Name")).ToString();

        Assert.Contains("target.[Note] IS NULL", sql);
        Assert.DoesNotContain("source.[Note]", sql);
        Assert.Contains("INSERT ( [Name],[Note] )", sql);
        Assert.Contains("VALUES( ISNULL(source.[Name], ''),NULL )", sql);
    }

    [Fact]
    public void BuildMergeCommand_NonNullValueOnNullableKey_KeepsEqualityMatch()
    {
        var writer = new SqlTableWriter(new Mock<ISqlExecutor>().Object);

        var sql = writer.BuildMergeCommand(Row("x"), HeapMetadata(), NotNull("Name")).ToString();

        Assert.Contains("target.[Note] = source.[Note]", sql);
        Assert.DoesNotContain("IS NULL", sql);
    }

    [Fact]
    public void BuildMergeCommand_NullKey_NotNullColumnOrUnknownNullability_KeepsHistoricalBinding()
    {
        var writer = new SqlTableWriter(new Mock<ISqlExecutor>().Object);

        var notNullSql = writer.BuildMergeCommand(Row(null), HeapMetadata(), NotNull("Name", "Note")).ToString();
        var unknownSql = writer.BuildMergeCommand(Row(null), HeapMetadata()).ToString();

        Assert.Contains("target.[Note] = source.[Note]", notNullSql);
        Assert.DoesNotContain("IS NULL", notNullSql);
        Assert.Contains("target.[Note] = source.[Note]", unknownSql);
        Assert.DoesNotContain("IS NULL", unknownSql);
    }

    [Fact]
    public void WriteRow_NullOnNullableKey_ExistenceProbeUsesIsNull()
    {
        var executor = new Mock<ISqlExecutor>();
        var probes = new List<string>();
        var reader = new Mock<IDataReader>();
        reader.Setup(r => r.Read()).Returns(false);
        executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => probes.Add(cb.ToString()))
            .Returns(reader.Object);
        var writer = new SqlTableWriter(executor.Object);

        writer.WriteRow(Row(null), HeapMetadata(), isDryRun: true, notNullColumns: NotNull("Name"));

        var probe = Assert.Single(probes);
        Assert.Contains("[Note] IS NULL", probe);
        Assert.DoesNotContain("[Note] = ", probe);
    }

    [Fact]
    public void UpdateColumnSubset_NullKey_MatchesNullOrHistoricalEmptyString()
    {
        var executor = new Mock<ISqlExecutor>();
        CommandBuilder? captured = null;
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => captured = cb)
            .Returns(1);
        var writer = new SqlTableWriter(executor.Object);
        var row = Row(null);
        row["Extra"] = "fill";

        writer.UpdateColumnSubset("HeapTable", new[] { "Name", "Note" }, row, new[] { "Extra" }, isDryRun: false);

        Assert.NotNull(captured);
        Assert.Contains("([Note] IS NULL OR [Note]=", captured!.ToString());
    }
}
