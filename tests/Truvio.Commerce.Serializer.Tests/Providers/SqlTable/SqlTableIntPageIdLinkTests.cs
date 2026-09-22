using System.Data;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Serialization;
using Dynamicweb.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers.SqlTable;

/// <summary>
/// Engine issue #27: a page id held in an INTEGER column listed in resolveLinksInColumns
/// (EmailMarketingEmail.EmailPageId / EmailUnsubscribePageId) resolves source -> local, keeps its
/// type, warns when it cannot resolve, and binds host-independently through the row's pageRefs
/// block (PageUniqueId) when one was recorded at serialize.
/// </summary>
public class SqlTableIntPageIdLinkTests : IDisposable
{
    private const string Table = "EmailMarketingEmail";
    private static readonly string[] LinkColumns = { "EmailPageId", "EmailUnsubscribePageId" };

    private static readonly Guid EmailPageGuid = Guid.Parse("3f1c2a4e-0000-4000-8000-000000005120");
    private static readonly Guid UnsubscribePageGuid = Guid.Parse("9ab2c3d4-0000-4000-8000-000000005121");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "truvio-intpage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------------------------------
    // Writer: id-map route
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void IntColumn_InMap_IsResolved_AndKeepsIntType()
    {
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 });
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 5120, ["EmailName"] = "Welcome" };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver);

        Assert.IsType<int>(row["EmailPageId"]);
        Assert.Equal(9000, row["EmailPageId"]);
        Assert.Equal("Welcome", row["EmailName"]);
        Assert.Equal(1, resolver.GetStats().resolved);
    }

    [Fact]
    public void BigintColumn_InMap_IsResolved_AndKeepsLongType()
    {
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 });
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 5120L };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver);

        Assert.IsType<long>(row["EmailPageId"]);
        Assert.Equal(9000L, row["EmailPageId"]);
    }

    [Fact]
    public void IntAndStringColumns_MixInOneList()
    {
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000, [5862] = 9100 });
        var row = new Dictionary<string, object?>
        {
            ["EmailPageId"] = 5120,
            ["Redirect"] = "Default.aspx?ID=5862"
        };

        Writer().ApplyLinkResolution(row, new[] { "EmailPageId", "Redirect" }, resolver);

        Assert.Equal(9000, row["EmailPageId"]);
        Assert.Equal("Default.aspx?ID=9100", row["Redirect"]);
    }

    [Fact]
    public void NullAndZero_AreLeftAlone_WithoutWarning()
    {
        var lines = new List<string>();
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 }, lines.Add);
        var row = new Dictionary<string, object?> { ["EmailPageId"] = null, ["EmailUnsubscribePageId"] = 0 };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver, null, null, lines.Add, Table);

        Assert.Null(row["EmailPageId"]);
        Assert.Equal(0, row["EmailUnsubscribePageId"]);
        Assert.DoesNotContain(lines, l => l.Contains("WARNING"));
        Assert.Equal(0, resolver.GetStats().resolved);
        Assert.Equal(0, resolver.GetStats().unresolved);
    }

    [Fact]
    public void UnresolvableId_WarnsLikeTheStringPath_AndEscalatesUnderStrictMode()
    {
        var escalator = new StrictModeEscalator(strict: true, log: null);
        var lines = new List<string>();
        void Log(string msg)
        {
            lines.Add(msg);
            // The orchestrator's log wrapper: every WARNING-prefixed line is recorded.
            if (msg.TrimStart().StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                escalator.RecordOnly(msg);
        }

        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 }, Log)
        {
            CurrentEntry = "sql/EmailMarketingEmail"
        };
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 4242 };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver, null, null, Log, Table);

        Assert.Equal(4242, row["EmailPageId"]);
        var warning = Assert.Single(lines, l => l.Contains("WARNING"));
        Assert.Contains("Unresolvable page ID 4242", warning);
        Assert.Contains("[EmailMarketingEmail].[EmailPageId]", warning);
        Assert.Equal(StrictModeWarningClass.UnresolvableLink, StrictModeWarningClass.Classify(warning));
        Assert.Equal(1, resolver.GetStats().unresolved);
        Assert.Throws<CumulativeStrictModeException>(() => escalator.AssertNoWarnings());
    }

    [Fact]
    public void AlreadyLocalId_IsLeftUnchanged_WithoutWarning()
    {
        var lines = new List<string>();
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 }, lines.Add);
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 9000 };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver);

        Assert.Equal(9000, row["EmailPageId"]);
        Assert.DoesNotContain(lines, l => l.Contains("WARNING"));
    }

    // ---------------------------------------------------------------------------------------
    // Writer: PageUniqueId route
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PageRefGuid_ResolvesToLocalId_WithoutAnIdMap()
    {
        // A layer page with sourcePageId 0 is absent from the id map; only the GUID binds it.
        var lines = new List<string>();
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 5120 };
        var refs = new Dictionary<string, Guid> { ["EmailPageId"] = EmailPageGuid };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver: null, refs,
            g => g == EmailPageGuid ? 777 : null, lines.Add, Table);

        Assert.Equal(777, row["EmailPageId"]);
        Assert.DoesNotContain(lines, l => l.Contains("WARNING"));
    }

    [Fact]
    public void PageRefGuid_WinsOverTheIdMap()
    {
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 });
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 5120 };
        var refs = new Dictionary<string, Guid> { ["EmailPageId"] = EmailPageGuid };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver, refs, _ => 777, null, Table);

        Assert.Equal(777, row["EmailPageId"]);
    }

    [Fact]
    public void PageRefGuid_NotOnHost_FallsBackToTheIdMap()
    {
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 });
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 5120 };
        var refs = new Dictionary<string, Guid> { ["EmailPageId"] = EmailPageGuid };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver, refs, _ => null, null, Table);

        Assert.Equal(9000, row["EmailPageId"]);
    }

    [Fact]
    public void PageRefGuid_NotOnHost_AndNoIdMap_Warns()
    {
        var lines = new List<string>();
        var row = new Dictionary<string, object?> { ["EmailPageId"] = 5120 };
        var refs = new Dictionary<string, Guid> { ["EmailPageId"] = EmailPageGuid };

        Writer().ApplyLinkResolution(row, LinkColumns, resolver: null, refs, _ => null, lines.Add, Table);

        Assert.Equal(5120, row["EmailPageId"]);
        var warning = Assert.Single(lines, l => l.Contains("WARNING"));
        Assert.Contains("Unresolvable page ID 5120", warning);
        Assert.Contains(EmailPageGuid.ToString(), warning);
        Assert.Equal(StrictModeWarningClass.UnresolvableLink, StrictModeWarningClass.Classify(warning));
    }

    [Fact]
    public void PageReferences_TakeFromRow_RemovesTheBlock_AndLeavesAScalarColumn()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EmailPageId"] = "5120",
            [PageReferences.Key] = new Dictionary<object, object> { ["EmailPageId"] = EmailPageGuid.ToString() }
        };
        var refs = PageReferences.TakeFromRow(row);
        Assert.False(row.ContainsKey(PageReferences.Key));
        Assert.Equal(EmailPageGuid, refs["emailpageid"]);

        var scalar = new Dictionary<string, object?> { [PageReferences.Key] = "a real column value" };
        Assert.Empty(PageReferences.TakeFromRow(scalar));
        Assert.Equal("a real column value", scalar[PageReferences.Key]);
    }

    // ---------------------------------------------------------------------------------------
    // Provider: serialize -> deserialize round trip
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_SerializeRecordsPageUniqueId_DeserializeBindsToTheLocalPage()
    {
        // Source host: pages 5120 / 5121. Row "Welcome" points at both; "Reminder" has null / 0.
        var sourceRows = new[]
        {
            EmailRow(100510, "Welcome", 5120, 5121),
            EmailRow(100511, "Reminder", null, 0)
        };
        var sourcePages = new Dictionary<int, Guid> { [5120] = EmailPageGuid, [5121] = UnsubscribePageGuid };

        var source = CreateProvider(sourceRows, sourcePages, new Dictionary<Guid, int>(), out _);
        var result = source.Serialize(Predicate(), _root);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.RowsSerialized);

        var welcomeYaml = File.ReadAllText(Path.Combine(_root, "_sql", Table, "Welcome.yml"));
        Assert.Contains("\"pageRefs\":", welcomeYaml);
        Assert.Contains(EmailPageGuid.ToString(), welcomeYaml);
        Assert.Contains(UnsubscribePageGuid.ToString(), welcomeYaml);
        var reminderYaml = File.ReadAllText(Path.Combine(_root, "_sql", Table, "Reminder.yml"));
        Assert.DoesNotContain("pageRefs", reminderYaml);

        // Target host: the same pages exist under different ids. No id map (layer pages with
        // sourcePageId 0), so only the recorded GUIDs can bind them.
        var targetPages = new Dictionary<Guid, int> { [EmailPageGuid] = 777, [UnsubscribePageGuid] = 778 };
        var deserializeLog = new List<string>();
        var target = CreateProvider(Array.Empty<Dictionary<string, object?>>(), new Dictionary<int, Guid>(),
            targetPages, out var written);
        var entry = (SqlTableEntry)result.Entry!;
        var outcome = target.Deserialize(entry, _root, deserializeLog.Add, linkResolver: null);

        Assert.Empty(outcome.Errors);
        Assert.DoesNotContain(deserializeLog, l => l.Contains("WARNING"));
        var welcome = Assert.Single(written, r => Equals(r["EmailName"], "Welcome"));
        Assert.IsType<int>(welcome["EmailPageId"]);
        Assert.Equal(777, welcome["EmailPageId"]);
        Assert.Equal(778, welcome["EmailUnsubscribePageId"]);
        Assert.False(welcome.ContainsKey(PageReferences.Key));
        var reminder = Assert.Single(written, r => Equals(r["EmailName"], "Reminder"));
        Assert.Null(reminder["EmailPageId"]);
        Assert.Equal(0, reminder["EmailUnsubscribePageId"]);
    }

    [Fact]
    public void Deserialize_DocumentWithoutPageRefs_StillReads_AndResolvesThroughTheIdMap()
    {
        // A row document written before 1.0.4 carries no pageRefs block.
        new FlatFileStore().WriteRow(_root, Table, "Welcome", EmailRow(100510, "Welcome", 5120, 5121));

        var lines = new List<string>();
        var target = CreateProvider(Array.Empty<Dictionary<string, object?>>(), new Dictionary<int, Guid>(),
            new Dictionary<Guid, int>(), out var written);
        var resolver = new InternalLinkResolver(new Dictionary<int, int> { [5120] = 9000 }, lines.Add);
        var entry = new SqlTableEntry
        {
            EntryId = "sql/" + Table,
            Files = new[] { $"_sql/{Table}/Welcome.yml" },
            Table = Table,
            ResolveLinksInColumns = LinkColumns
        };

        var outcome = target.Deserialize(entry, _root, lines.Add, linkResolver: resolver);

        Assert.Empty(outcome.Errors);
        var row = Assert.Single(written);
        Assert.Equal(9000, row["EmailPageId"]);
        // 5121 is in neither route: warned, left at the source id.
        Assert.Equal(5121, row["EmailUnsubscribePageId"]);
        Assert.Contains(lines, l => l.Contains("WARNING") && l.Contains("Unresolvable page ID 5121"));
    }

    // ---------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------

    private static SqlTableWriter Writer() => new(new Mock<ISqlExecutor>().Object);

    private static readonly TableMetadata Metadata = new()
    {
        TableName = Table,
        NameColumn = "EmailName",
        KeyColumns = new List<string> { "EmailId" },
        IdentityColumns = new List<string>(),
        AllColumns = new List<string> { "EmailId", "EmailName", "EmailPageId", "EmailUnsubscribePageId" }
    };

    private static readonly Dictionary<string, string> ColumnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EmailId"] = "int",
        ["EmailName"] = "nvarchar",
        ["EmailPageId"] = "int",
        ["EmailUnsubscribePageId"] = "int"
    };

    private static ProviderPredicateDefinition Predicate() => new()
    {
        Name = Table,
        ProviderType = "SqlTable",
        Table = Table,
        NameColumn = "EmailName",
        ResolveLinksInColumns = LinkColumns.ToList()
    };

    private static Dictionary<string, object?> EmailRow(int id, string name, int? pageId, int? unsubscribePageId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EmailId"] = id,
            ["EmailName"] = name,
            ["EmailPageId"] = pageId,
            ["EmailUnsubscribePageId"] = unsubscribePageId
        };

    /// <summary>SqlTableReader whose [Page] lookups come from in-memory maps.</summary>
    private sealed class FakePageReader : SqlTableReader
    {
        private readonly Dictionary<int, Guid> _sourcePages;
        private readonly Dictionary<Guid, int> _localPages;

        public FakePageReader(ISqlExecutor executor, Dictionary<int, Guid> sourcePages, Dictionary<Guid, int> localPages)
            : base(executor)
        {
            _sourcePages = sourcePages;
            _localPages = localPages;
        }

        public override Dictionary<int, Guid> ReadPageUniqueIds(IReadOnlyCollection<int> pageIds) =>
            pageIds.Where(_sourcePages.ContainsKey).ToDictionary(id => id, id => _sourcePages[id]);

        public override int? FindPageIdByUniqueId(Guid pageUniqueId) =>
            _localPages.TryGetValue(pageUniqueId, out var id) ? id : null;
    }

    private static SqlTableProvider CreateProvider(
        IReadOnlyList<Dictionary<string, object?>> tableRows,
        Dictionary<int, Guid> sourcePages,
        Dictionary<Guid, int> localPages,
        out List<Dictionary<string, object?>> written)
    {
        var executor = new Mock<ISqlExecutor>();
        executor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(() => ToTable(tableRows).CreateDataReader());
        executor.Setup(x => x.ExecuteNonQuery(It.IsAny<CommandBuilder>())).Returns(1);

        var metadataReader = new Mock<DataGroupMetadataReader>(executor.Object) { CallBase = false };
        metadataReader.Setup(x => x.GetTableMetadata(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<bool>())).Returns(Metadata);
        metadataReader.Setup(x => x.TableExists(It.IsAny<string>())).Returns(true);
        metadataReader.Setup(x => x.GetColumnTypes(It.IsAny<string>())).Returns(ColumnTypes);
        metadataReader.Setup(x => x.GetNotNullColumns(It.IsAny<string>()))
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EmailId" });

        var captured = new List<Dictionary<string, object?>>();
        written = captured;
        var writer = new Mock<SqlTableWriter>(executor.Object) { CallBase = false };
        writer.Setup(w => w.WriteRow(It.IsAny<Dictionary<string, object?>>(), It.IsAny<TableMetadata>(),
                It.IsAny<bool>(), It.IsAny<Action<string>?>(), It.IsAny<HashSet<string>?>()))
            .Callback<Dictionary<string, object?>, TableMetadata, bool, Action<string>?, HashSet<string>?>(
                (row, _, _, _, _) => captured.Add(new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase)))
            .Returns(WriteOutcome.Created);

        var schemaCache = new TargetSchemaCache(_ =>
            (new HashSet<string>(Metadata.AllColumns, StringComparer.OrdinalIgnoreCase),
             new Dictionary<string, string>(ColumnTypes, StringComparer.OrdinalIgnoreCase)));

        return new SqlTableProvider(metadataReader.Object,
            new FakePageReader(executor.Object, sourcePages, localPages),
            new FlatFileStore(), writer.Object, schemaCache);
    }

    private static DataTable ToTable(IEnumerable<Dictionary<string, object?>> rows)
    {
        var table = new DataTable();
        foreach (var column in Metadata.AllColumns)
            table.Columns.Add(column, typeof(object));
        foreach (var row in rows)
            table.Rows.Add(Metadata.AllColumns.Select(c => row.GetValueOrDefault(c) ?? DBNull.Value).ToArray());
        return table;
    }
}
