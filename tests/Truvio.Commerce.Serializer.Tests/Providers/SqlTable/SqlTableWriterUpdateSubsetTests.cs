using System.Data;
using System.Reflection;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Phase 39 D-17 (<see href="../../../../.planning/phases/39-merge-mode-field-level-merge-replace-merge-split-intent-is-fiel/39-02-PLAN.md"/>):
/// unit coverage for the narrowed-UPDATE path that the SqlTableProvider Merge-fill branch drives.
/// </summary>
[Trait("Category", "Phase39")]
public class SqlTableWriterUpdateSubsetTests
{
    private static TableMetadata CreateEcomOrderFlowMetadata() => new()
    {
        TableName = "EcomOrderFlow",
        NameColumn = "OrderFlowName",
        KeyColumns = new List<string> { "OrderFlowId" },
        IdentityColumns = new List<string> { "OrderFlowId" },
        AllColumns = new List<string> { "OrderFlowId", "OrderFlowName", "OrderFlowDescription" }
    };

    private static TableMetadata CreateCompositeKeyMetadata() => new()
    {
        TableName = "EcomPriceMatrix",
        NameColumn = "ShopId",
        KeyColumns = new List<string> { "ShopId", "LanguageId" },
        IdentityColumns = new List<string>(),
        AllColumns = new List<string> { "ShopId", "LanguageId", "MatrixValue" }
    };

    private static Dictionary<string, object?> CreateSampleRow() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["OrderFlowId"] = "FLOW-1",
        ["OrderFlowName"] = "Test Flow",
        ["OrderFlowDescription"] = "desc"
    };

    [Fact]
    public void UpdateColumnSubset_SingleColumn_GeneratesValidUpdate()
    {
        var executor = new Mock<ISqlExecutor>();
        CommandBuilder? captured = null;
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => captured = cb)
            .Returns(1);

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: false);

        Assert.Equal(WriteOutcome.Updated, outcome);
        Assert.NotNull(captured);
        var sql = captured!.ToString();
        Assert.Contains("UPDATE [EcomOrderFlow]", sql);
        Assert.Contains("SET", sql);
        Assert.Contains("[OrderFlowName]", sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("[OrderFlowId]", sql);
    }

    [Fact]
    public void UpdateColumnSubset_MultipleColumns_CommaSeparatedSet()
    {
        var executor = new Mock<ISqlExecutor>();
        CommandBuilder? captured = null;
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => captured = cb)
            .Returns(1);

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName", "OrderFlowDescription" }, isDryRun: false);

        Assert.Equal(WriteOutcome.Updated, outcome);
        Assert.NotNull(captured);
        var sql = captured!.ToString();
        Assert.Contains("[OrderFlowName]", sql);
        Assert.Contains("[OrderFlowDescription]", sql);
        // Comma-separated SET list (at least one comma between two SET assignments).
        Assert.Matches(@"\[OrderFlowName\][^,]*,[^,]*\[OrderFlowDescription\]|\[OrderFlowDescription\][^,]*,[^,]*\[OrderFlowName\]", sql);
    }

    [Fact]
    public void UpdateColumnSubset_CompositeKey_AndConjunctionInWhere()
    {
        var executor = new Mock<ISqlExecutor>();
        CommandBuilder? captured = null;
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => captured = cb)
            .Returns(1);

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateCompositeKeyMetadata();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ShopId"] = "SHOP1",
            ["LanguageId"] = "LANG1",
            ["MatrixValue"] = "10.0"
        };

        writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "MatrixValue" }, isDryRun: false);

        Assert.NotNull(captured);
        var sql = captured!.ToString();
        Assert.Contains("WHERE", sql);
        Assert.Contains("[ShopId]", sql);
        Assert.Contains("[LanguageId]", sql);
        Assert.Contains(" AND ", sql);
    }

    [Fact]
    public void UpdateColumnSubset_NullValue_BindsDBNull()
    {
        var executor = new Mock<ISqlExecutor>();
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>())).Returns(1);

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrderFlowId"] = "FLOW-1",
            ["OrderFlowName"] = null,
            ["OrderFlowDescription"] = "desc"
        };

        // Should not throw — null is written as the NULL literal (issue #18: never a parameter).
        var outcome = writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: false);

        Assert.Equal(WriteOutcome.Updated, outcome);
        executor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Once);
    }

    [Fact]
    public void UpdateColumnSubset_DoesNotIncludeIdentityInsert()
    {
        var executor = new Mock<ISqlExecutor>();
        CommandBuilder? captured = null;
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => captured = cb)
            .Returns(1);

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: false);

        Assert.NotNull(captured);
        Assert.DoesNotContain("SET IDENTITY_INSERT", captured!.ToString());
    }

    [Fact]
    public void UpdateColumnSubset_EmptyColumnsToUpdate_NoSqlEmitted_ReturnsUpdated()
    {
        var executor = new Mock<ISqlExecutor>();
        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        // Empty subset — caller pre-filtered to no-op per D-17 contract.
        var outcome = writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            Array.Empty<string>(), isDryRun: false);

        Assert.Equal(WriteOutcome.Updated, outcome);
        executor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Never);
    }

    [Fact]
    public void UpdateColumnSubset_DryRun_DoesNotCallExecuteNonQuery()
    {
        var executor = new Mock<ISqlExecutor>();
        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: true);

        Assert.Equal(WriteOutcome.Updated, outcome);
        executor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Never);
    }

    [Fact]
    public void UpdateColumnSubset_LogsDryRunLine_WhenDryRunAndLogProvided()
    {
        var executor = new Mock<ISqlExecutor>();
        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();
        var logs = new List<string>();

        writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: true, log: logs.Add);

        Assert.Contains(logs, l => l.Contains("[DRY-RUN]") && l.Contains("UPDATE [EcomOrderFlow]"));
    }

    [Fact]
    public void UpdateColumnSubset_ExceptionDuringExecute_ReturnsFailed_LogsError()
    {
        var executor = new Mock<ISqlExecutor>();
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Throws(new InvalidOperationException("boom"));

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();
        var logs = new List<string>();

        var outcome = writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: false, log: logs.Add);

        Assert.Equal(WriteOutcome.Failed, outcome);
        Assert.Contains(logs, l => l.Contains("ERROR") && l.Contains("boom"));
    }

    [Fact]
    public void UpdateColumnSubset_ValueWithQuote_IsParameterized_NotInlined()
    {
        var executor = new Mock<ISqlExecutor>();
        CommandBuilder? captured = null;
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(cb => captured = cb)
            .Returns(1);

        var writer = new SqlTableWriter(executor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrderFlowId"] = "FLOW-1",
            ["OrderFlowName"] = "O'Malley",
            ["OrderFlowDescription"] = "desc"
        };

        writer.UpdateColumnSubset(
            metadata.TableName, metadata.KeyColumns, row,
            new[] { "OrderFlowName" }, isDryRun: false);

        Assert.NotNull(captured);
        var sql = captured!.ToString();
        // The value must be bound via the {0}-placeholder CommandBuilder path, never inlined
        // as a literal SQL string — so the raw SQL should NOT contain the literal O'Malley
        // quote-wrapped value. CommandBuilder replaces {0} with parameter tokens (e.g., @P1).
        Assert.DoesNotContain("'O''Malley'", sql);
        Assert.DoesNotContain("'O'Malley'", sql);
    }

    [Fact]
    public void UpdateColumnSubset_IsVirtual_CanBeMocked()
    {
        var method = typeof(SqlTableWriter).GetMethod(
            "UpdateColumnSubset",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(method!.IsVirtual, "UpdateColumnSubset must be virtual so Mock<SqlTableWriter> { CallBase = false } can stub it.");
    }
}
