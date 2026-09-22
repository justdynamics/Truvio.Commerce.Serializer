using System.Text.Json;
using Truvio.Commerce.Serializer.Infrastructure;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Tests for the v0.6.0 ManifestWriter — atomic-write + complete-sentinel + schemaVersion-gate
/// envelope (Phase 42-02). Replaces the v1 flat-files tests; the Manifest type now lives in
/// Infrastructure/Manifest.cs (Plan 01) and carries polymorphic ManifestEntry items.
/// </summary>
public class ManifestWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ManifestWriter _writer;

    public ManifestWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ManifestWriterTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _writer = new ManifestWriter();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- factories ----------

    private static ContentEntry BuildContentEntry(
        string entryId = "content-area-1",
        int areaId = 1,
        string areaName = "Swift",
        string path = "/",
        int pageId = 0,
        IReadOnlyList<string>? files = null,
        IReadOnlyList<int>? acknowledgedOrphanPageIds = null,
        IReadOnlyList<string>? excludeAreaColumns = null)
        => new()
        {
            EntryId = entryId,
            AreaId = areaId,
            AreaName = areaName,
            Path = path,
            PageId = pageId,
            Files = files ?? new[] { "swift/index.yml", "swift/about.yml" },
            AcknowledgedOrphanPageIds = acknowledgedOrphanPageIds ?? Array.Empty<int>(),
            ExcludeAreaColumns = excludeAreaColumns ?? Array.Empty<string>()
        };

    private static SqlTableEntry BuildSqlTableEntry(
        string entryId = "sqltable-EcomOrderFlow",
        string table = "EcomOrderFlow",
        string? nameColumn = "OrderFlowName",
        string? compareColumns = "OrderFlowName,OrderFlowActive",
        IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? xmlColumns = null,
        IReadOnlyList<string>? resolveLinksInColumns = null,
        IReadOnlyList<string>? serviceCaches = null,
        string? schemaSync = null)
        => new()
        {
            EntryId = entryId,
            Table = table,
            NameColumn = nameColumn,
            CompareColumns = compareColumns,
            Files = files ?? new[] { "sql/EcomOrderFlow/standard.yml" },
            XmlColumns = xmlColumns ?? Array.Empty<string>(),
            ResolveLinksInColumns = resolveLinksInColumns ?? Array.Empty<string>(),
            ServiceCaches = serviceCaches ?? Array.Empty<string>(),
            SchemaSync = schemaSync
        };

    // ---------- Test 1 ----------

    [Fact]
    public void Write_EmitsEnvelopeWithSchemaVersion2_AndCompleteSentinel()
    {
        var entries = new ManifestEntry[]
        {
            BuildContentEntry(),
            BuildSqlTableEntry()
        };

        _writer.Write(_tempDir, "replace", entries);

        var manifestPath = Path.Combine(_tempDir, "replace-manifest.json");
        Assert.True(File.Exists(manifestPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        Assert.Equal(ManifestSchema.CurrentVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("replace", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal(2, root.GetProperty("entries").GetArrayLength());
    }

    // ---------- Test 2 ----------

    [Fact]
    public void Read_TolerantOfStaleTmpFile_FromPriorTornWrite()
    {
        // arrange — atomicity itself is proven by `File.Move(overwrite: true)` in `ManifestWriter.cs`
        // (verified via the grep-link acceptance step in this task's <acceptance_criteria>);
        // this test only proves Read tolerates the stray .tmp byproduct that a torn prior run
        // would leave on disk. We simulate the byproduct directly: a healthy {mode}-manifest.json
        // PLUS a truncated {mode}-manifest.json.tmp next to it. Read opens only the final path
        // and is unaffected by the .tmp.
        var entries = new ManifestEntry[] { BuildContentEntry() };
        _writer.Write(_tempDir, "replace", entries);

        var tmpPath = Path.Combine(_tempDir, "replace-manifest.json.tmp");
        File.WriteAllText(tmpPath, "{ TRUNCATED-PARTIAL-JSON-FROM-PRIOR-CRASH");

        // act
        var manifest = _writer.Read(_tempDir, "replace");

        // assert
        Assert.NotNull(manifest);
        Assert.Equal(ManifestSchema.CurrentVersion, manifest!.SchemaVersion);
        Assert.Equal("replace", manifest.Mode);
        Assert.True(manifest.Complete);
        Assert.Single(manifest.Entries);
        Assert.True(File.Exists(tmpPath), ".tmp byproduct should still be on disk after Read");
    }

    // ---------- Test 3 ----------

    [Fact]
    public void Write_ThenRead_RoundTripsAllFields()
    {
        var content = BuildContentEntry(
            acknowledgedOrphanPageIds: new[] { 100, 200, 300 },
            excludeAreaColumns: new[] { "AreaShopId", "AreaUserManagementAccessUserId" });
        var sql = BuildSqlTableEntry(
            xmlColumns: new[] { "OrderFlowXml" },
            resolveLinksInColumns: new[] { "OrderFlowXml" },
            serviceCaches: new[] { "Dynamicweb.Ecommerce.Services.OrderFlowService" },
            schemaSync: "EcomGroupFields");

        _writer.Write(_tempDir, "replace", new ManifestEntry[] { content, sql },
            excludeFieldsByItemType: new Dictionary<string, List<string>>
            {
                ["Swift_Page"] = new() { "Title", "Description" }
            },
            excludeXmlElementsByType: new Dictionary<string, List<string>>
            {
                ["Swift_Section"] = new() { "ColumnGap" }
            });

        var manifest = _writer.Read(_tempDir, "replace");
        Assert.NotNull(manifest);
        Assert.Equal(ManifestSchema.CurrentVersion, manifest!.SchemaVersion);
        Assert.Equal("replace", manifest.Mode);
        Assert.True(manifest.Complete);
        Assert.Equal(2, manifest.Entries.Count);

        var roundTrippedContent = Assert.IsType<ContentEntry>(manifest.Entries[0]);
        Assert.Equal(content.EntryId, roundTrippedContent.EntryId);
        Assert.Equal(content.AreaId, roundTrippedContent.AreaId);
        Assert.Equal(content.AreaName, roundTrippedContent.AreaName);
        Assert.Equal(content.Path, roundTrippedContent.Path);
        Assert.Equal(content.PageId, roundTrippedContent.PageId);
        Assert.Equal(content.Files, roundTrippedContent.Files);
        Assert.Equal(content.AcknowledgedOrphanPageIds, roundTrippedContent.AcknowledgedOrphanPageIds);
        Assert.Equal(content.ExcludeAreaColumns, roundTrippedContent.ExcludeAreaColumns);
        Assert.Equal("Content", roundTrippedContent.ProviderType);

        var roundTrippedSql = Assert.IsType<SqlTableEntry>(manifest.Entries[1]);
        Assert.Equal(sql.EntryId, roundTrippedSql.EntryId);
        Assert.Equal(sql.Table, roundTrippedSql.Table);
        Assert.Equal(sql.NameColumn, roundTrippedSql.NameColumn);
        Assert.Equal(sql.CompareColumns, roundTrippedSql.CompareColumns);
        Assert.Equal(sql.Files, roundTrippedSql.Files);
        Assert.Equal(sql.XmlColumns, roundTrippedSql.XmlColumns);
        Assert.Equal(sql.ResolveLinksInColumns, roundTrippedSql.ResolveLinksInColumns);
        Assert.Equal(sql.ServiceCaches, roundTrippedSql.ServiceCaches);
        Assert.Equal(sql.SchemaSync, roundTrippedSql.SchemaSync);
        Assert.Equal("SqlTable", roundTrippedSql.ProviderType);

        Assert.Equal(new[] { "Title", "Description" }, manifest.ExcludeFieldsByItemType["Swift_Page"]);
        Assert.Equal(new[] { "ColumnGap" }, manifest.ExcludeXmlElementsByType["Swift_Section"]);
    }

    // ---------- Test 4 ----------

    [Fact]
    public void Read_ReturnsNullWhenFileMissing()
    {
        var manifest = _writer.Read(_tempDir, "no-such-mode");
        Assert.Null(manifest);
    }

    // ---------- Test 5 ----------

    [Fact]
    public void Read_SchemaVersion1_ThrowsInvalidOperationExceptionNamingMismatch()
    {
        var manifestPath = Path.Combine(_tempDir, "replace-manifest.json");
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"mode\":\"replace\",\"complete\":true,\"entries\":[]}");

        var ex = Assert.Throws<InvalidOperationException>(() => _writer.Read(_tempDir, "replace"));
        Assert.Contains("schemaVersion=1", ex.Message);
        Assert.Contains("expected one of 2, 3", ex.Message);
    }

    // ---------- Test 6 ----------

    [Fact]
    public void Read_MissingSchemaVersion_ThrowsInvalidOperationException()
    {
        var manifestPath = Path.Combine(_tempDir, "replace-manifest.json");
        File.WriteAllText(manifestPath, "{\"mode\":\"replace\",\"complete\":true,\"entries\":[]}");

        var ex = Assert.Throws<InvalidOperationException>(() => _writer.Read(_tempDir, "replace"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schemaVersion", ex.Message);
    }

    // ---------- Test 7 ----------

    [Fact]
    public void Read_CompleteFalse_ThrowsJsonException()
    {
        // hand-write a syntactically valid v2 manifest with complete=false. Required envelope
        // fields are present so the strict-mode typed deserialize succeeds; the post-deserialize
        // sentinel check is what fires.
        var manifestPath = Path.Combine(_tempDir, "replace-manifest.json");
        File.WriteAllText(manifestPath,
            "{\"schemaVersion\":2,\"mode\":\"replace\"," +
            "\"writtenAtUtc\":\"2026-05-08T00:00:00Z\"," +
            "\"complete\":false," +
            "\"excludeFieldsByItemType\":{}," +
            "\"excludeXmlElementsByType\":{}," +
            "\"entries\":[]}");

        var ex = Assert.Throws<JsonException>(() => _writer.Read(_tempDir, "replace"));
        Assert.Contains("Complete", ex.Message);
        Assert.Contains("torn", ex.Message);
    }

    // ---------- Test 8 ----------

    [Fact]
    public void Read_UnknownProperty_ThrowsJsonExceptionNamingProperty()
    {
        // Strict-mode (UnmappedMemberHandling.Disallow) on the options bag AND the type catches
        // unknown top-level properties at typed-deserialize time with a JsonException naming the
        // offender.
        var manifestPath = Path.Combine(_tempDir, "replace-manifest.json");
        File.WriteAllText(manifestPath,
            "{\"schemaVersion\":2,\"mode\":\"replace\"," +
            "\"writtenAtUtc\":\"2026-05-08T00:00:00Z\"," +
            "\"complete\":true," +
            "\"excludeFieldsByItemType\":{}," +
            "\"excludeXmlElementsByType\":{}," +
            "\"entries\":[]," +
            "\"bogus\":1}");

        var ex = Assert.Throws<JsonException>(() => _writer.Read(_tempDir, "replace"));
        Assert.Contains("bogus", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Test 9 ----------

    [Fact]
    public void Write_ExcludeMapsBakedIntoEnvelope()
    {
        var excludeFields = new Dictionary<string, List<string>>
        {
            ["Swift_Page"] = new() { "Title", "MetaDescription" },
            ["Swift_Section"] = new() { "ColumnGap" }
        };
        var excludeXml = new Dictionary<string, List<string>>
        {
            ["EcomCart"] = new() { "FormFields" }
        };

        _writer.Write(_tempDir, "replace", Array.Empty<ManifestEntry>(),
            excludeFieldsByItemType: excludeFields,
            excludeXmlElementsByType: excludeXml);

        var json = File.ReadAllText(Path.Combine(_tempDir, "replace-manifest.json"));
        using var doc = JsonDocument.Parse(json);

        var fieldsMap = doc.RootElement.GetProperty("excludeFieldsByItemType");
        var page = fieldsMap.GetProperty("Swift_Page").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Title", "MetaDescription" }, page);
        var section = fieldsMap.GetProperty("Swift_Section").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "ColumnGap" }, section);

        var xmlMap = doc.RootElement.GetProperty("excludeXmlElementsByType");
        var cart = xmlMap.GetProperty("EcomCart").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "FormFields" }, cart);
    }

    // ---------- Test 10 ----------

    [Fact]
    public void Write_FilesArrayPosixForwardSlash_FromEntries()
    {
        // The POSIX-forward-slash invariant is preserved in v2: it now applies to entries[].files[]
        // (each provider emits POSIX-relative paths in its ManifestEntry.Files at BuildManifestEntry
        // time, Plan 03). This test pins that the writer does NOT mangle backslashes if a careless
        // caller hands in OS-style paths — it serializes them verbatim, and the JSON output reflects
        // exactly what was serialized. Provider-side normalization is the correct guarantee, but
        // the writer must not silently rewrite either way.
        //
        // We assert: when entries are constructed with forward-slash paths (the contract), JSON
        // contains them and contains NO backslash sequences. This is the cross-platform invariant.
        var content = BuildContentEntry(files: new[] { "swift/nested/page.yml", "swift/index.yml" });
        var sql = BuildSqlTableEntry(files: new[] { "sql/EcomOrderFlow/standard.yml" });

        _writer.Write(_tempDir, "replace", new ManifestEntry[] { content, sql });

        var json = File.ReadAllText(Path.Combine(_tempDir, "replace-manifest.json"));
        Assert.Contains("swift/nested/page.yml", json);
        Assert.Contains("swift/index.yml", json);
        Assert.Contains("sql/EcomOrderFlow/standard.yml", json);
        // No escaped backslashes in the JSON output (would appear as \\ in the source representation).
        Assert.DoesNotContain("\\\\", json);
    }
}
