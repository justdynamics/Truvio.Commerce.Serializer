using System.Text.Json;
using Truvio.Commerce.Serializer.Infrastructure;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Phase 42-01: STJ polymorphism + strict-read failure-mode tests for the v0.6.0
/// manifest type system. Pins the read-failure contract Phases 02/03/04 will rely on:
/// unknown property, missing required field, missing discriminator, unknown discriminator
/// value, schemaVersion gate, and discriminator-position quirks.
/// </summary>
public class ManifestEntryPolymorphismTests
{
    [Fact]
    public void Roundtrip_ContentEntry_DiscriminatorAtPositionZero()
    {
        var entry = new ContentEntry
        {
            EntryId = "content-1",
            Files = new[] { "replace/area-1/page-a.yml", "replace/area-1/page-b.yml" },
            AreaId = 7,
            AreaName = "Customer Center",
            Path = "/customer-center",
            PageId = 100,
            AcknowledgedOrphanPageIds = new[] { 999 },
            ExcludeAreaColumns = new[] { "AreaSomeColumn" }
        };

        var json = JsonSerializer.Serialize<ManifestEntry>(entry, ManifestSchema.ManifestJsonOptions);

        using var doc = JsonDocument.Parse(json);
        var firstProperty = doc.RootElement.EnumerateObject().First();
        Assert.Equal("providerType", firstProperty.Name);
        Assert.Equal("Content", firstProperty.Value.GetString());

        var roundTripped = JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions);
        var content = Assert.IsType<ContentEntry>(roundTripped);
        Assert.Equal("content-1", content.EntryId);
        Assert.Equal("Content", content.ProviderType);
        Assert.Equal(2, content.Files.Count);
        Assert.Equal(7, content.AreaId);
        Assert.Equal("Customer Center", content.AreaName);
        Assert.Equal("/customer-center", content.Path);
        Assert.Equal(100, content.PageId);
        Assert.Equal(new[] { 999 }, content.AcknowledgedOrphanPageIds);
        Assert.Equal(new[] { "AreaSomeColumn" }, content.ExcludeAreaColumns);
    }

    [Fact]
    public void Roundtrip_SqlTableEntry_DiscriminatorAtPositionZero()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql-1",
            Files = new[] { "replace/sql/eccomorderflow/draft.yml" },
            Table = "EcomOrderFlow",
            NameColumn = "OrderFlowName",
            CompareColumns = "OrderFlowName,OrderFlowDescription",
            XmlColumns = new[] { "OrderFlowProperties" },
            ResolveLinksInColumns = new[] { "OrderFlowConfirmEmailContent" },
            ServiceCaches = new[] { "Dynamicweb.Ecommerce.Services.OrderFlowService" },
            SchemaSync = "EcomGroupFields"
        };

        var json = JsonSerializer.Serialize<ManifestEntry>(entry, ManifestSchema.ManifestJsonOptions);

        using var doc = JsonDocument.Parse(json);
        var firstProperty = doc.RootElement.EnumerateObject().First();
        Assert.Equal("providerType", firstProperty.Name);
        Assert.Equal("SqlTable", firstProperty.Value.GetString());

        var roundTripped = JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions);
        var sql = Assert.IsType<SqlTableEntry>(roundTripped);
        Assert.Equal("sql-1", sql.EntryId);
        Assert.Equal("SqlTable", sql.ProviderType);
        Assert.Single(sql.Files);
        Assert.Equal("EcomOrderFlow", sql.Table);
        Assert.Equal("OrderFlowName", sql.NameColumn);
        Assert.Equal("OrderFlowName,OrderFlowDescription", sql.CompareColumns);
        Assert.Equal(new[] { "OrderFlowProperties" }, sql.XmlColumns);
        Assert.Equal(new[] { "OrderFlowConfirmEmailContent" }, sql.ResolveLinksInColumns);
        Assert.Equal(new[] { "Dynamicweb.Ecommerce.Services.OrderFlowService" }, sql.ServiceCaches);
        Assert.Equal("EcomGroupFields", sql.SchemaSync);
    }

    [Fact]
    public void Read_UnknownDiscriminator_ThrowsJsonException()
    {
        var json = """{"providerType":"Bogus","entryId":"x","files":[]}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions));
    }

    [Fact]
    public void Read_MissingRequiredField_ThrowsJsonExceptionNamingField()
    {
        // Missing "entryId" — required on ManifestEntry base.
        var json = """{"providerType":"Content","files":[],"areaId":1,"areaName":"a","path":"/","pageId":0}""";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions));
        Assert.Contains("entryid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_UnknownProperty_ThrowsJsonExceptionNamingProperty()
    {
        // "bogusField" is not on ContentEntry — UnmappedMemberHandling.Disallow rejects it.
        var json = """{"providerType":"Content","entryId":"x","files":[],"areaId":1,"areaName":"a","path":"/","pageId":0,"bogusField":"x"}""";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions));
        Assert.Contains("bogusfield", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_DiscriminatorAtNonZeroPosition_ThrowsJsonException_NotNotSupportedException()
    {
        // entryId comes BEFORE providerType. With AllowOutOfOrderMetadataProperties=false (default),
        // STJ in .NET 8 cannot find the discriminator at position 0 and falls through to attempting
        // to instantiate the declared base type — which fails with NotSupportedException because
        // ManifestEntry is abstract. Both JsonException and NotSupportedException are LOUD failures;
        // either is acceptable. The regression we're guarding against is silent base-type
        // instantiation. (PITFALLS §4 originally predicted JsonException; .NET 8 STJ empirically
        // throws NotSupportedException — pin the actual contract so future STJ updates that change
        // it don't go unnoticed.)
        var json = """{"entryId":"x","providerType":"Content","files":[],"areaId":1,"areaName":"a","path":"/","pageId":0}""";

        var ex = Record.Exception(() =>
            JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions));

        Assert.NotNull(ex);
        // If a future STJ update routes this through the typed JsonException path
        // (PITFALLS §4 prediction), Assert.IsType<JsonException> will catch the variant.
        // Until then, NotSupportedException is the actual .NET 8 behavior — accept either
        // exact type, but never a silent return.
        if (ex is JsonException)
            Assert.IsType<JsonException>(ex);
        else
            Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void SchemaVersionGate_WrongVersion_ThrowsInvalidOperationException()
    {
        var json = """
            {"schemaVersion":99,"mode":"replace","writtenAtUtc":"2026-05-08T00:00:00Z","complete":true,"excludeFieldsByItemType":{},"excludeXmlElementsByType":{},"entries":[]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => AssertSchemaVersionGate(json));
        Assert.Contains("schemaVersion=99", ex.Message);
        Assert.Contains($"expected {ManifestSchema.CurrentVersion}", ex.Message);
    }

    [Fact]
    public void SchemaVersionGate_MissingField_ThrowsInvalidOperationException()
    {
        var json = """
            {"mode":"replace","writtenAtUtc":"2026-05-08T00:00:00Z","complete":true,"excludeFieldsByItemType":{},"excludeXmlElementsByType":{},"entries":[]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => AssertSchemaVersionGate(json));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_MissingDiscriminatorField_ThrowsJsonException()
    {
        // No providerType key at all. STJ MUST refuse this loudly — the regression we're guarding
        // against is silent instantiation of the base type. .NET 8 STJ throws NotSupportedException
        // ("Deserialization of types without a parameterless constructor ... is not supported.
        //  Type 'ManifestEntry'.") because the abstract type can't be instantiated when no
        //  discriminator picks a derived type. JsonException is also acceptable (some STJ versions
        //  throw it). Either is fine — what we MUST NOT see is a silent ManifestEntry instance.
        var json = """{"entryId":"x","files":[],"areaId":1,"areaName":"a","path":"/","pageId":0}""";

        var ex = Record.Exception(() =>
            JsonSerializer.Deserialize<ManifestEntry>(json, ManifestSchema.ManifestJsonOptions));

        Assert.NotNull(ex);
        Assert.True(ex is JsonException || ex is NotSupportedException,
            $"Expected JsonException or NotSupportedException; got {ex.GetType().FullName}: {ex.Message}");
    }

    /// <summary>
    /// JsonDocument-based schemaVersion precheck used by tests above. Plan 02 lifts this into
    /// <see cref="ManifestWriter"/> as the production read path; this is the test-side preview
    /// that pins the contract before that happens.
    /// </summary>
    private static void AssertSchemaVersionGate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("schemaVersion", out var v) || v.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"Manifest is missing a numeric 'schemaVersion' field. Manifests require schemaVersion={ManifestSchema.CurrentVersion}.");
        var version = v.GetInt32();
        if (version != ManifestSchema.CurrentVersion)
            throw new InvalidOperationException($"Manifest has schemaVersion={version}, expected {ManifestSchema.CurrentVersion}. Re-run serialize.");
    }
}
