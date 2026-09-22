using System.Text.Json;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Providers.Content;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Phase 42-04 / PROVIDER-05: 20-case mechanical round-trip property test (10 predicate
/// fields × 2 providers). For each (field, provider) pair, build a populated predicate,
/// call <see cref="ISerializationProvider.BuildManifestEntry"/>, write the resulting
/// <see cref="Manifest"/> envelope through <see cref="ManifestWriter"/>, read it back, and
/// assert the field survives end-to-end.
///
/// Pitfall #2 defense (silent loss of post-processing metadata): every PROVIDER-05 field
/// is mechanically asserted on its destination provider; for fields that have no
/// destination on a given provider the test asserts the field's JSON name is ABSENT from
/// the entry's serialized output (proves no field is silently smuggled into the wrong
/// entry type).
///
/// Field landing locations (per Plan 42-03 SUMMARY):
///   ContentEntry           : ExcludeAreaColumns, AcknowledgedOrphanPageIds
///   SqlTableEntry          : ServiceCaches, SchemaSync, XmlColumns, ResolveLinksInColumns,
///                            KeyColumns, ReplaceStrategy (the heap key-resolution fix)
///   Manifest envelope maps : ExcludeFields (-> ExcludeFieldsByItemType),
///                            ExcludeXmlElements (-> ExcludeXmlElementsByType)
/// </summary>
public class ManifestRoundTripTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ManifestWriter _writer;

    public ManifestRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ManifestRoundTripTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _writer = new ManifestWriter();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- 20-case theory matrix ----------

    public static IEnumerable<object[]> RoundTripCases() => new[]
    {
        new object[] { "ServiceCaches",            "Content"  },
        new object[] { "ServiceCaches",            "SqlTable" },
        new object[] { "SchemaSync",               "Content"  },
        new object[] { "SchemaSync",               "SqlTable" },
        new object[] { "XmlColumns",               "Content"  },
        new object[] { "XmlColumns",               "SqlTable" },
        new object[] { "ExcludeFields",            "Content"  },
        new object[] { "ExcludeFields",            "SqlTable" },
        new object[] { "ExcludeXmlElements",       "Content"  },
        new object[] { "ExcludeXmlElements",       "SqlTable" },
        new object[] { "ExcludeAreaColumns",       "Content"  },
        new object[] { "ExcludeAreaColumns",       "SqlTable" },
        new object[] { "ResolveLinksInColumns",    "Content"  },
        new object[] { "ResolveLinksInColumns",    "SqlTable" },
        new object[] { "AcknowledgedOrphanPageIds","Content"  },
        new object[] { "AcknowledgedOrphanPageIds","SqlTable" },
        new object[] { "KeyColumns",               "Content"  },
        new object[] { "KeyColumns",               "SqlTable" },
        new object[] { "ReplaceStrategy",          "Content"  },
        new object[] { "ReplaceStrategy",          "SqlTable" },
    };

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Field_RoundTrips_PredicateThroughManifestThroughEntry(string fieldName, string providerType)
    {
        // 1. Build predicate populated with a non-default value for the field under test.
        var predicate = BuildPredicate(fieldName, providerType);
        var expected = ExpectedValue(fieldName);

        // 2. Construct provider directly — no test subclasses, no mocks. Both provider ctors
        //    are pure field assignment; BuildManifestEntry never dereferences any of the
        //    SqlTableProvider dependencies (verified by reading SqlTableProvider.cs lines 31-43
        //    and ContentProvider.cs lines 27-30 + the BuildManifestEntry bodies).
        ISerializationProvider provider = providerType == "Content"
            ? new ContentProvider(filesRoot: null)
            : new SqlTableProvider(null!, null!, null!, null!, null);

        var writtenFiles = new List<string>();
        var entry = provider.BuildManifestEntry(predicate, _tempDir, writtenFiles);

        // 3. Build envelope-level by-ItemType maps for the envelope-baking cases.
        var excludeFieldsByItemType = fieldName == "ExcludeFields"
            ? new Dictionary<string, List<string>> { ["Page"] = predicate.ExcludeFields.ToList() }
            : null;
        var excludeXmlElementsByType = fieldName == "ExcludeXmlElements"
            ? new Dictionary<string, List<string>> { ["Page"] = predicate.ExcludeXmlElements.ToList() }
            : null;

        // 4. Atomic-write the manifest.
        _writer.Write(_tempDir, "replace", new[] { entry }, excludeFieldsByItemType, excludeXmlElementsByType);

        // 5. Read it back.
        var manifest = _writer.Read(_tempDir, "replace");
        Assert.NotNull(manifest);
        Assert.Single(manifest!.Entries);
        var roundTrippedEntry = manifest.Entries[0];

        // 6. Read back via the switch helper.
        var roundTripped = ReadBack(fieldName, roundTrippedEntry, manifest);

        // 7. Assert per the <behavior> rules.
        if (roundTripped is null)
        {
            // No-op shape: the field has no destination on this provider's entry type.
            // Prove the field's JSON name is NOT present on the entry's serialized output —
            // i.e. no field smuggling into the wrong entry type.
            var entryJson = JsonSerializer.Serialize<ManifestEntry>(roundTrippedEntry, ManifestSchema.ManifestJsonOptions);
            var camelCaseFieldName = JsonNamingPolicy.CamelCase.ConvertName(fieldName);
            Assert.DoesNotContain(
                $"\"{camelCaseFieldName}\"",
                entryJson);
        }
        else
        {
            AssertFieldEquals(fieldName, expected, roundTripped);
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// Build a populated <see cref="ProviderPredicateDefinition"/> for the test case. The
    /// field under test gets a non-default value; other required fields get minimal valid
    /// values so <see cref="ContentProvider.ValidatePredicate"/> /
    /// <see cref="SqlTableProvider.ValidatePredicate"/> would accept the predicate (we don't
    /// actually call Validate — BuildManifestEntry doesn't — but the predicate shape stays
    /// realistic).
    /// </summary>
    private static ProviderPredicateDefinition BuildPredicate(string fieldName, string providerType)
    {
        // Base shapes for each provider.
        ProviderPredicateDefinition predicate = providerType == "Content"
            ? new ProviderPredicateDefinition
            {
                Name = $"test-content-{fieldName}",
                ProviderType = "Content",
                AreaId = 42,
                Path = "/test",
                PageId = 1
            }
            : new ProviderPredicateDefinition
            {
                Name = $"test-sql-{fieldName}",
                ProviderType = "SqlTable",
                Table = "EcomTestTable",
                NameColumn = "TestName"
            };

        // Layer the field-under-test value on top via record `with`.
        return fieldName switch
        {
            "ServiceCaches"             => predicate with { ServiceCaches = new List<string> { "Cache.A", "Cache.B" } },
            "SchemaSync"                => predicate with { SchemaSync = "EcomGroupFields" },
            "XmlColumns"                => predicate with { XmlColumns = new List<string> { "OrderFlowXml", "FormFieldsXml" } },
            "ExcludeFields"             => predicate with { ExcludeFields = new List<string> { "Title", "Description" } },
            "ExcludeXmlElements"        => predicate with { ExcludeXmlElements = new List<string> { "ColumnGap", "Spacing" } },
            "ExcludeAreaColumns"        => predicate with { ExcludeAreaColumns = new List<string> { "AreaShopId", "AreaUserManagementAccessUserId" } },
            "ResolveLinksInColumns"     => predicate with { ResolveLinksInColumns = new List<string> { "OrderFlowConfirmEmailContent", "UrlPathRedirect" } },
            "AcknowledgedOrphanPageIds" => predicate with { AcknowledgedOrphanPageIds = new List<int> { 100, 200, 300 } },
            "KeyColumns"                => predicate with { KeyColumns = new List<string> { "DynamicStructureUniqueId" } },
            "ReplaceStrategy"           => predicate with { ReplaceStrategy = "truncate" },
            _ => throw new ArgumentException($"Unknown fieldName: {fieldName}")
        };
    }

    /// <summary>
    /// The expected populated value for the field, mirroring <see cref="BuildPredicate"/>.
    /// Kept independent so a typo in either side is caught by the assert.
    /// </summary>
    private static object ExpectedValue(string fieldName) => fieldName switch
    {
        "ServiceCaches"             => new List<string> { "Cache.A", "Cache.B" },
        "SchemaSync"                => "EcomGroupFields",
        "XmlColumns"                => new List<string> { "OrderFlowXml", "FormFieldsXml" },
        "ExcludeFields"             => new List<string> { "Title", "Description" },
        "ExcludeXmlElements"        => new List<string> { "ColumnGap", "Spacing" },
        "ExcludeAreaColumns"        => new List<string> { "AreaShopId", "AreaUserManagementAccessUserId" },
        "ResolveLinksInColumns"     => new List<string> { "OrderFlowConfirmEmailContent", "UrlPathRedirect" },
        "AcknowledgedOrphanPageIds" => new List<int> { 100, 200, 300 },
        "KeyColumns"                => new List<string> { "DynamicStructureUniqueId" },
        "ReplaceStrategy"           => "truncate",
        _ => throw new ArgumentException($"Unknown fieldName: {fieldName}")
    };

    /// <summary>
    /// Extract the round-tripped value from the round-tripped <see cref="ManifestEntry"/> /
    /// <see cref="Manifest"/>. Returns <c>null</c> when the field has no destination on
    /// the given provider's entry type — that's the no-op signal the test interprets as
    /// "field-name MUST be absent from JSON".
    /// </summary>
    private static object? ReadBack(string fieldName, ManifestEntry entry, Manifest manifest) => fieldName switch
    {
        "ServiceCaches"             => (entry as SqlTableEntry)?.ServiceCaches,
        "SchemaSync"                => (entry as SqlTableEntry)?.SchemaSync,
        "XmlColumns"                => (entry as SqlTableEntry)?.XmlColumns,
        "ExcludeFields"             => manifest.ExcludeFieldsByItemType.TryGetValue("Page", out var v1) ? (object)v1 : null,
        "ExcludeXmlElements"        => manifest.ExcludeXmlElementsByType.TryGetValue("Page", out var v2) ? (object)v2 : null,
        "ExcludeAreaColumns"        => (entry as ContentEntry)?.ExcludeAreaColumns,
        "ResolveLinksInColumns"     => (entry as SqlTableEntry)?.ResolveLinksInColumns,
        "AcknowledgedOrphanPageIds" => (entry as ContentEntry)?.AcknowledgedOrphanPageIds,
        "KeyColumns"                => (entry as SqlTableEntry)?.KeyColumns,
        "ReplaceStrategy"           => (entry as SqlTableEntry)?.ReplaceStrategy,
        _ => throw new ArgumentException($"Unknown fieldName: {fieldName}")
    };

    /// <summary>
    /// Type-aware equality assertion. xUnit's Assert.Equal does element-wise comparison for
    /// IEnumerable, but we cast to the right concrete shape so the message is meaningful
    /// when a specific case fails.
    /// </summary>
    private static void AssertFieldEquals(string fieldName, object expected, object actual)
    {
        switch (expected)
        {
            case string s:
                Assert.Equal(s, (string?)actual);
                break;
            case List<int> intList:
                Assert.Equal(intList, (IEnumerable<int>)actual);
                break;
            case List<string> stringList:
                Assert.Equal(stringList, (IEnumerable<string>)actual);
                break;
            default:
                throw new ArgumentException($"Unhandled expected type {expected.GetType()} for field {fieldName}");
        }
    }
}
