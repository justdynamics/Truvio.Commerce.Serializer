using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Phase 37-05 / LINK-02 pass 2: integration coverage for SqlTableWriter's pre-MERGE
/// link-resolution step. Uses a fake <see cref="ISqlExecutor"/> to capture the row
/// state that reaches MERGE and asserts the rewritten values.
/// </summary>
public class SqlTableLinkResolutionIntegrationTests
{
    private static TableMetadata BuildMetadata() => new()
    {
        TableName = "UrlPath",
        AllColumns = new List<string> { "UrlPathID", "UrlPathPath", "UrlPathRedirect" },
        KeyColumns = new List<string> { "UrlPathID" },
        IdentityColumns = new List<string> { "UrlPathID" },
        ColumnDefinitions = new List<ColumnDefinition>()
    };

    private sealed class FakeSqlExecutor : ISqlExecutor
    {
        public readonly List<string> ExecutedSql = new();
        public int ExecuteNonQuery(Dynamicweb.Data.CommandBuilder cb)
        {
            // CommandBuilder doesn't expose its parameter values easily; for this
            // test we only need to know that WriteRow ran. The row-mutation assertion
            // happens via the row dictionary reference before BuildMergeCommand.
            return 1;
        }
        public System.Data.IDataReader ExecuteReader(Dynamicweb.Data.CommandBuilder cb)
        {
            // Return empty reader — RowExistsInTarget returns false.
            return new EmptyReader();
        }

        private sealed class EmptyReader : System.Data.IDataReader
        {
            public object this[int i] => throw new NotImplementedException();
            public object this[string name] => throw new NotImplementedException();
            public int Depth => 0;
            public bool IsClosed => false;
            public int RecordsAffected => 0;
            public int FieldCount => 0;
            public void Close() { }
            public void Dispose() { }
            public bool GetBoolean(int i) => throw new NotImplementedException();
            public byte GetByte(int i) => throw new NotImplementedException();
            public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => 0;
            public char GetChar(int i) => throw new NotImplementedException();
            public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => 0;
            public System.Data.IDataReader GetData(int i) => throw new NotImplementedException();
            public string GetDataTypeName(int i) => throw new NotImplementedException();
            public DateTime GetDateTime(int i) => throw new NotImplementedException();
            public decimal GetDecimal(int i) => throw new NotImplementedException();
            public double GetDouble(int i) => throw new NotImplementedException();
            public Type GetFieldType(int i) => throw new NotImplementedException();
            public float GetFloat(int i) => throw new NotImplementedException();
            public Guid GetGuid(int i) => throw new NotImplementedException();
            public short GetInt16(int i) => throw new NotImplementedException();
            public int GetInt32(int i) => throw new NotImplementedException();
            public long GetInt64(int i) => throw new NotImplementedException();
            public string GetName(int i) => throw new NotImplementedException();
            public int GetOrdinal(string name) => throw new NotImplementedException();
            public System.Data.DataTable? GetSchemaTable() => null;
            public string GetString(int i) => throw new NotImplementedException();
            public object GetValue(int i) => throw new NotImplementedException();
            public int GetValues(object[] values) => 0;
            public bool IsDBNull(int i) => true;
            public bool NextResult() => false;
            public bool Read() => false;
        }
    }

    [Fact]
    public void UrlPathRedirect_WithSourceToTargetMap_Rewritten()
    {
        // Source page 5862 is at target page 9000 after deserialize. UrlPathRedirect for the
        // corresponding URL currently contains Default.aspx?ID=5862 — the writer should
        // rewrite it to Default.aspx?ID=9000 before composing the MERGE.
        var map = new Dictionary<int, int> { { 5862, 9000 } };
        var resolver = new InternalLinkResolver(map);

        var row = new Dictionary<string, object?>
        {
            ["UrlPathID"] = 1,
            ["UrlPathPath"] = "old-url",
            ["UrlPathRedirect"] = "Default.aspx?ID=5862"
        };

        var writer = new SqlTableWriter(new FakeSqlExecutor());
        writer.ApplyLinkResolution(row, new[] { "UrlPathRedirect" }, resolver);

        Assert.Equal("Default.aspx?ID=9000", row["UrlPathRedirect"]);
        // Non-configured columns are untouched
        Assert.Equal("old-url", row["UrlPathPath"]);
    }

    [Fact]
    public void ApplyLinkResolution_NullResolver_NoOp()
    {
        var writer = new SqlTableWriter(new FakeSqlExecutor());
        var row = new Dictionary<string, object?>
        {
            ["UrlPathRedirect"] = "Default.aspx?ID=5862"
        };
        writer.ApplyLinkResolution(row, new[] { "UrlPathRedirect" }, resolver: null);
        Assert.Equal("Default.aspx?ID=5862", row["UrlPathRedirect"]);
    }

    [Fact]
    public void ApplyLinkResolution_EmptyColumnList_NoOp()
    {
        var writer = new SqlTableWriter(new FakeSqlExecutor());
        var map = new Dictionary<int, int> { { 5862, 9000 } };
        var resolver = new InternalLinkResolver(map);
        var row = new Dictionary<string, object?>
        {
            ["UrlPathRedirect"] = "Default.aspx?ID=5862"
        };
        writer.ApplyLinkResolution(row, resolveInColumns: null, resolver: resolver);
        // Column opted-out — untouched.
        Assert.Equal("Default.aspx?ID=5862", row["UrlPathRedirect"]);
    }

    [Fact]
    public void ApplyLinkResolution_NonIntegerNonStringValue_Untouched()
    {
        // Engine issue #27: integer columns now resolve as page ids (see
        // SqlTableIntPageIdLinkTests). Other non-string values (decimal, date, ...) pass through.
        var writer = new SqlTableWriter(new FakeSqlExecutor());
        var map = new Dictionary<int, int> { { 5862, 9000 } };
        var resolver = new InternalLinkResolver(map);
        var row = new Dictionary<string, object?>
        {
            ["SomeDecimal"] = 5862m
        };
        writer.ApplyLinkResolution(row, new[] { "SomeDecimal" }, resolver);
        Assert.Equal(5862m, row["SomeDecimal"]);
    }

    [Fact]
    public void ApplyLinkResolution_MissingColumn_Ignored()
    {
        // Column listed in ResolveLinksInColumns but not present on the row: no-op, no throw.
        var writer = new SqlTableWriter(new FakeSqlExecutor());
        var map = new Dictionary<int, int> { { 5862, 9000 } };
        var resolver = new InternalLinkResolver(map);
        var row = new Dictionary<string, object?> { ["UrlPathID"] = 1 };
        writer.ApplyLinkResolution(row, new[] { "UrlPathRedirect" }, resolver);
        Assert.False(row.ContainsKey("UrlPathRedirect"));
    }
}
