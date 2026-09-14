using System.Data;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

[Trait("Category", "Phase13")]
public class SqlTableWriterTests
{
    private static TableMetadata CreateEcomOrderFlowMetadata() => new()
    {
        TableName = "EcomOrderFlow",
        NameColumn = "OrderFlowName",
        KeyColumns = new List<string> { "OrderFlowId" },
        IdentityColumns = new List<string> { "OrderFlowId" },
        AllColumns = new List<string> { "OrderFlowId", "OrderFlowName", "OrderFlowDescription" }
    };

    private static TableMetadata CreateNonIdentityPkMetadata() => new()
    {
        TableName = "EcomCountry",
        NameColumn = "Name",
        KeyColumns = new List<string> { "Name" },
        IdentityColumns = new List<string> { "AutoId" },
        AllColumns = new List<string> { "AutoId", "Name", "Code" }
    };

    private static Dictionary<string, object?> CreateSampleRow() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["OrderFlowId"] = 1,
        ["OrderFlowName"] = "Checkout",
        ["OrderFlowDescription"] = "Main checkout flow"
    };

    private static Mock<IDataReader> CreateEmptyReader()
    {
        var mock = new Mock<IDataReader>();
        mock.Setup(r => r.Read()).Returns(false);
        mock.Setup(r => r.Dispose());
        return mock;
    }

    private static Mock<IDataReader> CreateSingleRowReader()
    {
        var mock = new Mock<IDataReader>();
        var called = false;
        mock.Setup(r => r.Read()).Returns(() =>
        {
            if (!called) { called = true; return true; }
            return false;
        });
        mock.Setup(r => r.Dispose());
        return mock;
    }

    [Fact]
    public void BuildMergeCommand_GeneratesValidMerge()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var cb = writer.BuildMergeCommand(row, metadata);

        // CommandBuilder produces SQL text; verify via ToString
        var sql = cb.ToString();
        Assert.Contains("MERGE [EcomOrderFlow] AS target", sql);
        Assert.Contains("SET IDENTITY_INSERT [EcomOrderFlow] ON", sql);
        Assert.Contains("SET IDENTITY_INSERT [EcomOrderFlow] OFF", sql);
        Assert.Contains("WHEN MATCHED THEN UPDATE SET", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", sql);
        Assert.Contains("source", sql);
    }

    [Fact]
    public void BuildMergeCommand_NoIdentityInsert_WhenIdentityNotInPK()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateNonIdentityPkMetadata();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AutoId"] = 5,
            ["Name"] = "Denmark",
            ["Code"] = "DK"
        };

        var cb = writer.BuildMergeCommand(row, metadata);

        var sql = cb.ToString();
        Assert.DoesNotContain("SET IDENTITY_INSERT", sql);
        Assert.Contains("MERGE [EcomCountry] AS target", sql);
    }

    [Fact]
    public void WriteRow_DryRun_DoesNotCallExecuteNonQuery()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        // RowExistsInTarget needs ExecuteReader
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(CreateEmptyReader().Object);

        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.WriteRow(row, metadata, isDryRun: true);

        Assert.Equal(WriteOutcome.Created, outcome);
        mockExecutor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Never);
    }

    [Fact]
    public void WriteRow_DryRun_ReportsUpdatedWhenRowExists()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(CreateSingleRowReader().Object);

        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.WriteRow(row, metadata, isDryRun: true);

        Assert.Equal(WriteOutcome.Updated, outcome);
        mockExecutor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Never);
    }

    [Fact]
    public void WriteRow_Execute_CallsExecuteNonQuery()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(CreateEmptyReader().Object);
        mockExecutor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Returns(1);

        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.WriteRow(row, metadata, isDryRun: false);

        Assert.Equal(WriteOutcome.Created, outcome);
        mockExecutor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Once);
    }

    [Fact]
    public void WriteRow_NullValues_MappedToDbNull()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(CreateEmptyReader().Object);
        mockExecutor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Returns(1);

        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrderFlowId"] = 1,
            ["OrderFlowName"] = "Checkout",
            ["OrderFlowDescription"] = null // NULL value
        };

        // Should not throw — null is written as the NULL literal (issue #18: never a parameter)
        var outcome = writer.WriteRow(row, metadata, isDryRun: false);

        Assert.Equal(WriteOutcome.Created, outcome);
        mockExecutor.Verify(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()), Times.Once);
    }

    [Fact]
    public void WriteRow_OnException_ReturnsFailed()
    {
        var mockExecutor = new Mock<ISqlExecutor>();
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Throws(new Exception("DB connection failed"));

        var writer = new SqlTableWriter(mockExecutor.Object);
        var metadata = CreateEcomOrderFlowMetadata();
        var row = CreateSampleRow();

        var outcome = writer.WriteRow(row, metadata, isDryRun: false);

        Assert.Equal(WriteOutcome.Failed, outcome);
    }
}
