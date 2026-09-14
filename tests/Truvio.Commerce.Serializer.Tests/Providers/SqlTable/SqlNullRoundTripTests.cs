using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Issue #18: a SQL NULL survives serialize and deserialize. Rows are written through
/// <see cref="FlatFileStore"/> as Serialize writes them, deserialized by <see cref="SqlTableProvider"/>
/// with the real <see cref="SqlTableWriter"/>, and each captured command is resolved to the value
/// SQL Server receives per column: a placeholder resolves to its bound parameter (the same way
/// CommandBuilder shares parameters), the NULL literal to null. Before the fix a NULL column bound
/// after an empty string resolved to '' (1900-01-01 on datetime, 0 on decimal).
/// </summary>
public class SqlNullRoundTripTests : IDisposable
{
    private const string Table = "EcomPrices";

    private static readonly TableMetadata Metadata = new()
    {
        TableName = Table,
        KeyColumns = new List<string> { "PriceId" },
        IdentityColumns = new List<string>(),
        // Nullable columns are interleaved with empty strings on both sides of them.
        AllColumns = new List<string>
        {
            "PriceId", "PriceProductId", "PriceProductVariantId", "PriceValidTo",
            "PriceUserCustomerNumber", "PriceShopId", "PriceQuantity", "PriceAmount", "PriceValidFrom"
        }
    };

    private static readonly Dictionary<string, string> ColumnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PriceId"] = "nvarchar",
        ["PriceProductId"] = "nvarchar",
        ["PriceProductVariantId"] = "nvarchar",
        ["PriceValidTo"] = "datetime",
        ["PriceUserCustomerNumber"] = "nvarchar",
        ["PriceShopId"] = "nvarchar",
        ["PriceQuantity"] = "decimal",
        ["PriceAmount"] = "decimal",
        ["PriceValidFrom"] = "datetime"
    };

    private static readonly SqlTableEntry Entry = new()
    {
        EntryId = "sql/EcomPrices",
        Files = Array.Empty<string>(),
        Table = Table
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "truvio-null-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Source row as SqlTableReader reads it: DBNull already mapped to null.</summary>
    private static Dictionary<string, object?> SourceRow(string variantId) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PriceId"] = "TC-PRICE-1",
        ["PriceProductId"] = "TCPROD0001",
        ["PriceProductVariantId"] = variantId,           // "" on a non-variant row
        ["PriceValidTo"] = null,                          // nullable datetime NULL
        ["PriceUserCustomerNumber"] = null,               // nullable nvarchar NULL
        ["PriceShopId"] = "",                             // empty string after the NULLs
        ["PriceQuantity"] = null,                         // nullable decimal NULL
        ["PriceAmount"] = 12.5m,
        ["PriceValidFrom"] = new DateTime(2026, 1, 1)
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var mode in new[] { SerializerMode.Merge, SerializerMode.Replace })
            foreach (var variantId in new[] { "", "TCVO-TIER-ADV" })
                foreach (var targetHasRow in new[] { false, true })
                    yield return new object[] { mode, variantId, targetHasRow };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NullColumns_RoundTrip_AsNull_EmptyStringsStayEmpty(SerializerMode mode, string variantId, bool targetHasRow)
    {
        var source = SourceRow(variantId);
        new FlatFileStore().WriteRow(_root, Table, "TC-PRICE-1", source, mode: mode);

        // Target row, when present, has every non-key column NULL (Merge fills all of them).
        var existing = targetHasRow
            ? Metadata.AllColumns.ToDictionary(c => c, c => c == "PriceId" ? (object?)"TC-PRICE-1" : null, StringComparer.OrdinalIgnoreCase)
            : null;

        var written = Deserialize(mode, existing);

        var expectedColumns = targetHasRow
            ? Metadata.AllColumns.Where(c => c != "PriceId")        // UPDATE branch: every non-key column
            : Metadata.AllColumns;                                  // INSERT branch: every column
        foreach (var column in expectedColumns)
        {
            Assert.True(written.ContainsKey(column), $"{column} was not written ({mode}, variant '{variantId}', target row {targetHasRow})");
            Assert.Equal(source[column], written[column]);
        }
    }

    [Fact]
    public void Yaml_NullIsAnEmptyScalar_EmptyStringIsQuoted_ShapeUnchanged()
    {
        new FlatFileStore().WriteRow(_root, Table, "TC-PRICE-1", SourceRow(""), mode: SerializerMode.Merge);

        var lines = File.ReadAllLines(Path.Combine(_root, "_sql", Table, "TC-PRICE-1.yml")).Select(l => l.TrimEnd()).ToList();
        Assert.Contains("\"PriceValidTo\":", lines);
        Assert.Contains("\"PriceQuantity\":", lines);
        Assert.Contains("\"PriceProductVariantId\": \"\"", lines);

        var (row, _) = Assert.Single(new FlatFileStore().ReadAllDocuments(_root, Table));
        Assert.Null(row["PriceValidTo"]);
        Assert.Null(row["PriceUserCustomerNumber"]);
        Assert.Equal("", row["PriceProductVariantId"]);
    }

    [Fact]
    public void BuildMergeCommand_BindsNoNullParameter_AndEmptyStringKeepsItsOwnValue()
    {
        var row = SourceRow("");
        var cb = new SqlTableWriter(new Mock<ISqlExecutor>().Object).BuildMergeCommand(row, Metadata);

        Assert.DoesNotContain(Parameters(cb), p => p is null or DBNull);
        var sql = cb.ToString();
        Assert.Contains("[PriceValidTo] = NULL", sql);
        Assert.DoesNotContain("source.[PriceValidTo]", sql);
    }

    [Fact]
    public void BuildMergeCommand_NotNullColumnWithNull_InsertsEmptyString()
    {
        var row = SourceRow("");
        var cb = new SqlTableWriter(new Mock<ISqlExecutor>().Object)
            .BuildMergeCommand(row, Metadata, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PriceUserCustomerNumber" });

        Assert.Equal("", EvaluateMerge(cb).Insert["PriceUserCustomerNumber"]);
        Assert.Null(EvaluateMerge(cb).Insert["PriceValidTo"]);
    }

    [Fact]
    public void CommandBuilderValues_NullAfterEmptyString_IsNotTheEmptyStringParameter()
    {
        var cb = new CommandBuilder();
        CommandBuilderValues.AddValue(cb, "SELECT ", "");
        CommandBuilderValues.AddValue(cb, ", ", DBNull.Value);
        CommandBuilderValues.AddValue(cb, ", ", null);
        CommandBuilderValues.AddValue(cb, ", ", "");

        Assert.Equal(new object?[] { "", null, null, "" }, ResolveList(cb.ToString(), Parameters(cb)));
    }

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    /// <summary>Runs SqlTableProvider.Deserialize and returns column -> value as SQL Server receives it.</summary>
    private Dictionary<string, object?> Deserialize(SerializerMode mode, Dictionary<string, object?>? existingRow)
    {
        var executor = new Mock<ISqlExecutor>();
        var captured = new List<CommandBuilder>();
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>()))
            .Callback<CommandBuilder>(captured.Add)
            .Returns(1);
        executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(() => ExistingRows(existingRow).CreateDataReader());

        var metadataReader = new Mock<DataGroupMetadataReader>(executor.Object) { CallBase = false };
        metadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>())).Returns(Metadata);
        metadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
        metadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>())).Returns(ColumnTypes);
        metadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PriceId" });

        var schemaCache = new TargetSchemaCache(_ =>
            (new HashSet<string>(Metadata.AllColumns, StringComparer.OrdinalIgnoreCase),
             new Dictionary<string, string>(ColumnTypes, StringComparer.OrdinalIgnoreCase)));
        var provider = new SqlTableProvider(metadataReader.Object, new SqlTableReader(executor.Object),
            new FlatFileStore(), new SqlTableWriter(executor.Object), schemaCache);

        var strategy = mode == SerializerMode.Merge ? ConflictStrategy.DestinationWins : ConflictStrategy.SourceWins;
        var result = provider.Deserialize(Entry, _root, strategy: strategy);
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.Failed);

        var write = Assert.Single(captured, c => c.ToString().Contains("MERGE [") || c.ToString().Contains($"UPDATE [{Table}]"));
        var sql = write.ToString();
        if (sql.Contains("MERGE ["))
        {
            var merge = EvaluateMerge(write);
            return existingRow is null ? merge.Insert : merge.Update;
        }
        return EvaluateUpdate(write);
    }

    private static DataTable ExistingRows(Dictionary<string, object?>? row)
    {
        var table = new DataTable();
        foreach (var column in Metadata.AllColumns)
            table.Columns.Add(column, typeof(object));
        if (row is not null)
            table.Rows.Add(Metadata.AllColumns.Select(c => row[c] ?? DBNull.Value).ToArray());
        return table;
    }

    private static List<object?> Parameters(CommandBuilder cb)
    {
        var infos = (IEnumerable)typeof(CommandBuilder)
            .GetProperty("ParameterInfos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(cb)!;
        return infos.Cast<object>()
            .Select(p => p.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(p))
            .Select(v => v is DBNull ? null : v)
            .ToList();
    }

    private static readonly Regex Placeholder = new(@"^\{(\d+)\}$");

    private static object? ResolveToken(string token, IReadOnlyList<object?> parameters)
    {
        token = token.Trim();
        if (token == "NULL") return null;
        if (token == "''") return "";
        var m = Placeholder.Match(token);
        Assert.True(m.Success, $"Unexpected value token '{token}'");
        return parameters[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)];
    }

    private static List<object?> ResolveList(string sql, IReadOnlyList<object?> parameters) =>
        sql["SELECT".Length..].Split(',').Select(t => ResolveToken(t, parameters)).ToList();

    private static (Dictionary<string, object?> Insert, Dictionary<string, object?> Update) EvaluateMerge(CommandBuilder cb)
    {
        var sql = cb.ToString();
        var parameters = Parameters(cb);

        var select = Between(sql, "USING (SELECT", ") AS source (").Split(',').Select(t => ResolveToken(t, parameters)).ToList();
        var sourceColumns = Between(sql, ") AS source (", ")").Split(',').Select(c => c.Trim().Trim('[', ']')).ToList();
        var source = sourceColumns.Zip(select).ToDictionary(p => p.First, p => p.Second, StringComparer.OrdinalIgnoreCase);

        object? Resolve(string expression)
        {
            expression = expression.Trim();
            if (expression == "NULL") return null;
            if (expression == "''") return "";
            var isNull = Regex.Match(expression, @"^ISNULL\(source\.\[(\w+)\], ''\)$");
            if (isNull.Success) return source[isNull.Groups[1].Value] ?? "";
            var src = Regex.Match(expression, @"^source\.\[(\w+)\]$");
            Assert.True(src.Success, $"Unexpected expression '{expression}'");
            return source[src.Groups[1].Value];
        }

        var insertColumns = Between(sql, "WHEN NOT MATCHED THEN INSERT (", ")").Split(',').Select(c => c.Trim().Trim('[', ']')).ToList();
        var insertValues = Regex.Matches(Between(sql, "VALUES(", ");"), @"ISNULL\(source\.\[\w+\], ''\)|source\.\[\w+\]|NULL|''")
            .Select(m => Resolve(m.Value)).ToList();
        var insert = insertColumns.Zip(insertValues).ToDictionary(p => p.First, p => p.Second, StringComparer.OrdinalIgnoreCase);

        var update = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(Between(sql, "WHEN MATCHED THEN UPDATE SET", "WHEN NOT MATCHED"), @"\[(\w+)\] = (NULL|source\.\[\w+\])"))
            update[m.Groups[1].Value] = Resolve(m.Groups[2].Value);

        return (insert, update);
    }

    private static Dictionary<string, object?> EvaluateUpdate(CommandBuilder cb)
    {
        var sql = cb.ToString();
        var parameters = Parameters(cb);
        var set = Between(sql, "SET", "WHERE");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(set, @"\[(\w+)\]=\s*(NULL|\{\d+\})"))
            values[m.Groups[1].Value] = ResolveToken(m.Groups[2].Value, parameters);
        return values;
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found in: {text}");
        from += start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to >= 0, $"'{end}' not found after '{start}' in: {text}");
        return text[from..to];
    }
}
