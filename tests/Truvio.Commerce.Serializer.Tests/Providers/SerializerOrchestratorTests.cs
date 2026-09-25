using System.Data;
using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Truvio.Commerce.Serializer.Providers.SqlTable;
using Truvio.Commerce.Serializer.Reporting;
using Dynamicweb.Data;
using Dynamicweb.CoreUI.Data;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers;

/// <summary>
/// Phase 14 + Phase 43 Layer A acceptance tests for <see cref="SerializerOrchestrator"/>.
///
/// <para>
/// Phase 44 / CONVERGE-03 + CONVERGE-04 audit (per CONTEXT D-06): 53 ProviderPredicateDefinition
/// refs in this file were classified into three dispositions:
/// <list type="bullet">
/// <item><b>RETAIN-AS-BRIDGE-TEST</b> — the three static predicate fixtures
/// (<c>ContentPred1</c>, <c>ContentPred2</c>, <c>SqlTablePred</c>) and the seven
/// <c>SerializeAll</c> tests using them. SerializeAll keeps the predicate-typed contract
/// post-phase (only the three DeserializeAll [Obsolete] overloads are deleted); these tests
/// legitimately target the surviving SerializeAll surface.</item>
/// <item><b>DELETE</b> — tests that probed the deleted [Obsolete] DeserializeAll(predicates, ...)
/// overload's predicate→entry bridge body (e.g.,
/// <c>DeserializeAll_MixedPredicates_DispatchesToCorrectProviders</c>,
/// <c>DeserializeAll_FilterAndDryRun_PassesThroughCorrectly</c>). Equivalent semantic
/// coverage lives at the Layer A SC-1/SC-2 tests via the <c>DeserializeEntries</c> seam.</item>
/// <item><b>PORT to DeserializeEntries seam</b> — tests asserting orchestrator semantics
/// that survive the pivot (providerFilter, FK ordering, cache invalidation, schema sync).
/// Each is re-expressed against the manifest-driven <c>DeserializeEntries</c> internal test
/// seam (CONTEXT D-06 / must_haves.truths #14 retains the seam for exactly this purpose).</item>
/// </list>
/// </para>
///
/// <para>The <c>StubBuildManifestEntry</c> helper + <c>#pragma warning disable CS0618</c>
/// also went with the deleted bridge — neither has any surviving caller post-pivot.</para>
/// </summary>
[Trait("Category", "Phase14")]
public class SerializerOrchestratorTests
{
    private readonly Mock<ISerializationProvider> _contentProvider;
    private readonly Mock<ISerializationProvider> _sqlTableProvider;
    private readonly ProviderRegistry _registry;
    private readonly SerializerOrchestrator _orchestrator;

    // RETAIN-AS-BRIDGE-TEST (Phase 44 / D-06): SerializeAll keeps the predicate-typed
    // contract — these fixtures drive the surviving SerializeAll(predicates, ...) surface.
    private static readonly ProviderPredicateDefinition ContentPred1 = new()
    {
        Name = "Pages",
        ProviderType = "Content",
        Path = "/",
        AreaId = 1
    };

    private static readonly ProviderPredicateDefinition ContentPred2 = new()
    {
        Name = "Blog",
        ProviderType = "Content",
        Path = "/blog",
        AreaId = 1
    };

    private static readonly ProviderPredicateDefinition SqlTablePred = new()
    {
        Name = "Order Flows",
        ProviderType = "SqlTable",
        Table = "EcomOrderFlow",
        NameColumn = "OrderFlowName"
    };

    // Phase 43 Layer A entry fixtures (per CONTEXT D-04 — Layer A retargets directly).
    private static readonly ContentEntry ContentEntry1 = new()
    {
        EntryId = "content/area-1",
        Files = new[] { "_content/area-1/page.yml" },
        AreaId = 1,
        AreaName = "Area 1",
        Path = "/",
        PageId = 0
    };

    private static readonly SqlTableEntry SqlTableEntryFx = new()
    {
        EntryId = "sql/EcomOrderFlow",
        Files = new[] { "_sql/EcomOrderFlow/row.yml" },
        Table = "EcomOrderFlow",
        NameColumn = "OrderFlowName"
    };

    public SerializerOrchestratorTests()
    {
        _contentProvider = new Mock<ISerializationProvider>();
        _contentProvider.Setup(p => p.ProviderType).Returns("Content");
        _contentProvider.Setup(p => p.Serialize(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new SerializeResult { RowsSerialized = 5, TableName = "Content" });
        _contentProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 2, Updated = 1, TableName = "Content" });

        _sqlTableProvider = new Mock<ISerializationProvider>();
        _sqlTableProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        _sqlTableProvider.Setup(p => p.Serialize(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new SerializeResult { RowsSerialized = 10, TableName = "EcomOrderFlow" });
        _sqlTableProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 3, Updated = 2, Skipped = 1, TableName = "EcomOrderFlow" });

        _registry = new ProviderRegistry();
        _registry.Register(_contentProvider.Object);
        _registry.Register(_sqlTableProvider.Object);

        _orchestrator = new SerializerOrchestrator(_registry);
    }

    // -------------------------------------------------------------------------
    // SerializeAll tests — RETAIN bucket (Phase 44 / D-06). SerializeAll keeps the
    // predicate-typed contract post-phase.
    // -------------------------------------------------------------------------

    [Fact]
    public void SerializeAll_TwoContentPredicates_DispatchesBothToContentProvider()
    {
        var predicates = new List<ProviderPredicateDefinition> { ContentPred1, ContentPred2 };

        var result = _orchestrator.SerializeAll(predicates, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins);

        _contentProvider.Verify(p => p.Serialize(ContentPred1, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        _contentProvider.Verify(p => p.Serialize(ContentPred2, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        Assert.Equal(2, result.SerializeResults.Count);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void SerializeAll_MixedPredicates_DispatchesToCorrectProviders()
    {
        var predicates = new List<ProviderPredicateDefinition> { ContentPred1, SqlTablePred };

        var result = _orchestrator.SerializeAll(predicates, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins);

        _contentProvider.Verify(p => p.Serialize(ContentPred1, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        _sqlTableProvider.Verify(p => p.Serialize(SqlTablePred, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        Assert.Equal(2, result.SerializeResults.Count);
    }

    [Fact]
    public void SerializeAll_FilterContent_SkipsSqlTablePredicates()
    {
        var predicates = new List<ProviderPredicateDefinition> { ContentPred1, SqlTablePred };

        var result = _orchestrator.SerializeAll(predicates, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins, providerFilter: "Content");

        _contentProvider.Verify(p => p.Serialize(ContentPred1, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        _sqlTableProvider.Verify(p => p.Serialize(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Never);
        Assert.Single(result.SerializeResults);
    }

    [Fact]
    public void SerializeAll_FilterSqlTable_SkipsContentPredicates()
    {
        var predicates = new List<ProviderPredicateDefinition> { ContentPred1, SqlTablePred };

        var result = _orchestrator.SerializeAll(predicates, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins, providerFilter: "SqlTable");

        _contentProvider.Verify(p => p.Serialize(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Never);
        _sqlTableProvider.Verify(p => p.Serialize(SqlTablePred, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        Assert.Single(result.SerializeResults);
    }

    [Fact]
    public void SerializeAll_NullFilter_DispatchesAllPredicates()
    {
        var predicates = new List<ProviderPredicateDefinition> { ContentPred1, SqlTablePred };

        var result = _orchestrator.SerializeAll(predicates, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins, providerFilter: null);

        _contentProvider.Verify(p => p.Serialize(ContentPred1, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        _sqlTableProvider.Verify(p => p.Serialize(SqlTablePred, "/output", It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()), Times.Once);
        Assert.Equal(2, result.SerializeResults.Count);
    }

    [Fact]
    public void SerializeAll_UnknownProviderType_LogsErrorAndContinues()
    {
        var unknownPred = new ProviderPredicateDefinition
        {
            Name = "Unknown",
            ProviderType = "Nonexistent"
        };
        var predicates = new List<ProviderPredicateDefinition> { unknownPred, ContentPred1 };
        var logs = new List<string>();

        var result = _orchestrator.SerializeAll(predicates, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins, log: msg => logs.Add(msg));

        // Unknown predicate should be skipped with error, Content should still be processed
        Assert.Single(result.SerializeResults);
        Assert.Single(result.Errors);
        Assert.Contains("Nonexistent", result.Errors[0]);
        Assert.Contains("WARNING", logs.First(l => l.Contains("Nonexistent")));
    }

    // -------------------------------------------------------------------------
    // DeserializeAll tests — PORT bucket (Phase 44 / D-06). The two predicate-typed
    // [Obsolete] overloads + bridge body were deleted; these tests are re-expressed
    // against the manifest-driven DeserializeEntries internal test seam.
    // -------------------------------------------------------------------------

    [Fact]
    public void DeserializeAll_UnknownProviderType_ReportsFailedOutcome()
    {
        // Phase 44 / D-06 PORT: replaces the pre-pivot
        // DeserializeAll_UnknownProviderType_LogsErrorAndContinues which used the
        // [Obsolete] DeserializeAll(predicates, ...) overload. Surface the same invariant
        // via the manifest-driven seam.
        var unknownEntry = new SqlTableEntry
        {
            EntryId = "sql/UnknownProvider",
            Files = Array.Empty<string>(),
            Table = "UnknownProvider",
        };
        // Stub a unique "Nonexistent" provider-type entry by mutating the discriminator
        // is impossible (the override is `=> "SqlTable"`), so we instead remove the
        // SqlTable registration and use an entry whose ProviderType resolves to the
        // missing provider.
        var registry = new ProviderRegistry();
        registry.Register(_contentProvider.Object);  // only Content registered
        var orchestrator = new SerializerOrchestrator(registry);

        var entries = new List<ManifestEntry> { unknownEntry, ContentEntry1 };
        var result = orchestrator.DeserializeEntries(entries, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // Content succeeds; SqlTable (no provider registered) → Failed outcome + run-level error.
        Assert.Contains(result.EntryOutcomes, o => o.EntryId == "content/area-1" && o.Status == EntryStatus.Succeeded);
        Assert.Contains(result.EntryOutcomes, o => o.EntryId == "sql/UnknownProvider" && o.Status == EntryStatus.Failed);
        Assert.True(result.HasErrors);
    }

    // -------------------------------------------------------------------------
    // Strict-mode quarantine (engine issue #5): one unresolvable link must not roll
    // back a pass that wrote otherwise-clean rows.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-point the Content provider stub so its Deserialize emits <paramref name="warning"/>
    /// through the orchestrator's wrapped log before returning a clean result — the shape of
    /// an entry that wrote its rows and logged one recoverable warning on the way.
    /// </summary>
    private void StubContentProviderWarning(string warning)
    {
        _contentProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Callback((ManifestEntry _, string _, Action<string>? log, bool _, ConflictStrategy _,
                       Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _,
                       IReadOnlyDictionary<string, List<string>>? _,
                       IReadOnlyDictionary<string, List<string>>? _) => log?.Invoke(warning))
            .Returns(new ProviderDeserializeResult { Created = 283, TableName = "Content" });
    }

    [Fact]
    public void DeserializeEntries_UnresolvableLink_NotQuarantined_FailsTheRun()
    {
        // Pre-issue-#5 behavior, retained as the default: the whole pass fails.
        StubContentProviderWarning("  WARNING: Unresolvable page ID 83 in link");
        var escalator = new StrictModeEscalator(strict: true, log: null);

        var result = _orchestrator.DeserializeEntries(
            new List<ManifestEntry> { ContentEntry1 }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: escalator, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.True(result.HasErrors);
        Assert.Empty(result.QuarantinedWarnings);
    }

    [Fact]
    public void DeserializeEntries_QuarantinedUnresolvableLink_KeepsThePassGreen()
    {
        StubContentProviderWarning("  WARNING: Unresolvable page ID 83 in link");
        var escalator = new StrictModeEscalator(
            strict: true, log: null,
            quarantinedClasses: new HashSet<string> { StrictModeWarningClass.UnresolvableLink });

        var logs = new List<string>();
        var result = _orchestrator.DeserializeEntries(
            new List<ManifestEntry> { ContentEntry1 }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: logs.Add, isDryRun: false, providerFilter: null,
            escalator: escalator, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // The 283 clean rows survive; the orphan link is reported, not fatal.
        Assert.False(result.HasErrors);
        Assert.Empty(result.Errors);
        Assert.Single(result.QuarantinedWarnings);
        Assert.Contains("Unresolvable page ID 83", result.QuarantinedWarnings[0]);
        Assert.Contains(logs, l => l.StartsWith("QUARANTINED:"));
        Assert.Contains("Quarantined: 1", result.Summary);
    }

    [Fact]
    public void DeserializeEntries_QuarantineActive_OtherWarningStillFailsTheRun()
    {
        StubContentProviderWarning("WARNING: source column [T].[C] not present on target schema — skipping");
        var escalator = new StrictModeEscalator(
            strict: true, log: null,
            quarantinedClasses: new HashSet<string> { StrictModeWarningClass.UnresolvableLink });

        var result = _orchestrator.DeserializeEntries(
            new List<ManifestEntry> { ContentEntry1 }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: escalator, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.True(result.HasErrors);
        Assert.Empty(result.QuarantinedWarnings);
    }

    // -------------------------------------------------------------------------
    // OrchestratorResult tests — unchanged (test the surviving aggregate surface)
    // -------------------------------------------------------------------------

    [Fact]
    public void OrchestratorResult_HasErrors_TrueWhenErrorsExist()
    {
        var result = new OrchestratorResult { Errors = new List<string> { "fail" } };
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void OrchestratorResult_HasErrors_TrueWhenSerializeResultHasErrors()
    {
        var result = new OrchestratorResult
        {
            SerializeResults = new List<SerializeResult>
            {
                new() { Errors = new[] { "serialize error" } }
            }
        };
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void OrchestratorResult_HasErrors_FalseWhenNoErrors()
    {
        var result = new OrchestratorResult
        {
            SerializeResults = new List<SerializeResult> { new() { RowsSerialized = 5 } }
        };
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void OrchestratorResult_Summary_AggregatesCounts()
    {
        var result = new OrchestratorResult
        {
            SerializeResults = new List<SerializeResult>
            {
                new() { RowsSerialized = 5, TableName = "Content" },
                new() { RowsSerialized = 10, TableName = "EcomOrderFlow" }
            }
        };

        Assert.Contains("15", result.Summary);
    }

    // -------------------------------------------------------------------------
    // FK Ordering + Cache Invalidation + Schema Sync tests — PORT bucket
    // (Phase 44 / D-06). Each re-expresses the invariant against DeserializeEntries.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Helper: set up ISqlExecutor mock to return FK edges for FkDependencyResolver.
    /// </summary>
    private static FkDependencyResolver CreateFkResolver(params (string Child, string Parent)[] edges)
    {
        var dataTable = new DataTable();
        dataTable.Columns.Add("ChildTable", typeof(string));
        dataTable.Columns.Add("ParentTable", typeof(string));
        foreach (var (child, parent) in edges)
            dataTable.Rows.Add(child, parent);

        var mockExecutor = new Mock<ISqlExecutor>();
        mockExecutor.Setup(x => x.ExecuteReader(It.IsAny<CommandBuilder>()))
            .Returns(() => dataTable.CreateDataReader());

        return new FkDependencyResolver(mockExecutor.Object);
    }

    private static SqlTableEntry SqlEntry(string table) =>
        new()
        {
            EntryId = $"sql/{table}",
            Files = Array.Empty<string>(),
            Table = table
        };

    [Fact]
    [Trait("Category", "Phase15")]
    public void DeserializeAll_FkOrdering_SqlTableEntriesReorderedByDependency()
    {
        // A depends on B, B depends on C => deserialization order: C, B, A
        var fkResolver = CreateFkResolver(("A", "B"), ("B", "C"));

        var callOrder = new List<string>();
        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ManifestEntry e, string _, Action<string>? _, bool _, ConflictStrategy _, Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                callOrder.Add(((SqlTableEntry)e).Table);
                return new ProviderDeserializeResult { Created = 1, TableName = ((SqlTableEntry)e).Table };
            });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        // Pass entries in wrong order: A, B, C (should be reordered to C, B, A)
        var orchestrator = new SerializerOrchestrator(registry, fkResolver);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { SqlEntry("A"), SqlEntry("B"), SqlEntry("C") },
            "/input", SerializerMode.Replace, ConflictStrategy.SourceWins,
            log: null, isDryRun: false, providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.Equal(3, callOrder.Count);
        Assert.True(callOrder.IndexOf("C") < callOrder.IndexOf("B"),
            $"Expected C before B, got: {string.Join(", ", callOrder)}");
        Assert.True(callOrder.IndexOf("B") < callOrder.IndexOf("A"),
            $"Expected B before A, got: {string.Join(", ", callOrder)}");
    }

    [Fact]
    [Trait("Category", "Phase15")]
    public void DeserializeAll_FkOrdering_ContentEntriesUnaffected()
    {
        // Mixed entries: Content + SqlTable. Content stays at front, SqlTable reordered.
        var fkResolver = CreateFkResolver(("A", "B")); // A depends on B => B before A

        var callOrder = new List<string>();

        var contentProvider = new Mock<ISerializationProvider>();
        contentProvider.Setup(p => p.ProviderType).Returns("Content");
        contentProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ManifestEntry e, string _, Action<string>? _, bool _, ConflictStrategy _, Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                callOrder.Add($"Content:{e.EntryId}");
                return new ProviderDeserializeResult { Created = 1, TableName = "Content" };
            });

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ManifestEntry e, string _, Action<string>? _, bool _, ConflictStrategy _, Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                callOrder.Add($"SqlTable:{((SqlTableEntry)e).Table}");
                return new ProviderDeserializeResult { Created = 1, TableName = ((SqlTableEntry)e).Table };
            });

        var registry = new ProviderRegistry();
        registry.Register(contentProvider.Object);
        registry.Register(sqlProvider.Object);

        // Order: sqlEntryA, content, sqlEntryB — SqlTable in FK order (B, A) first, then Content
        var orchestrator = new SerializerOrchestrator(registry, fkResolver);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { SqlEntry("A"), ContentEntry1, SqlEntry("B") },
            "/input", SerializerMode.Replace, ConflictStrategy.SourceWins,
            log: null, isDryRun: false, providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.Equal(3, callOrder.Count);
        // SqlTable B before SqlTable A (B is parent), then Content last.
        Assert.Equal("SqlTable:B", callOrder[0]);
        Assert.Equal("SqlTable:A", callOrder[1]);
        Assert.Equal("Content:content/area-1", callOrder[2]);
    }

    [Fact]
    [Trait("Category", "Phase15")]
    public void DeserializeAll_CacheInvalidation_CalledAfterEachSuccessfulDeserialize()
    {
        var entry1 = new SqlTableEntry
        {
            EntryId = "sql/EcomPayments",
            Files = Array.Empty<string>(),
            Table = "EcomPayments",
            ServiceCaches = new List<string> { "CountryService", "CurrencyService" }
        };
        var entry2 = new SqlTableEntry
        {
            EntryId = "sql/EcomShippings",
            Files = Array.Empty<string>(),
            Table = "EcomShippings",
            ServiceCaches = new List<string> { "LanguageService" }
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 1, TableName = "Test" });

        var invokeCount = 0;
        DwCacheServiceRegistry.CacheClearEntry MakeFake(string n) =>
            new(n, $"Test.{n}", () => invokeCount++);
        var cacheInvalidator = new CacheInvalidator(name => MakeFake(name));

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, cacheInvalidator: cacheInvalidator);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry1, entry2 }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // CountryService, CurrencyService from entry1, LanguageService from entry2 = 3 cache clears
        Assert.Equal(3, invokeCount);
    }

    [Fact]
    [Trait("Category", "Phase15")]
    public void DeserializeAll_DryRun_DoesNotCallCacheInvalidator()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomPayments",
            Files = Array.Empty<string>(),
            Table = "EcomPayments",
            ServiceCaches = new List<string> { "CountryService" }
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 1, TableName = "EcomPayments" });

        var invokeCount = 0;
        var cacheInvalidator = new CacheInvalidator(name =>
            new DwCacheServiceRegistry.CacheClearEntry(name, $"Test.{name}", () => invokeCount++));

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, cacheInvalidator: cacheInvalidator);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: true, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // No cache invalidation during dry-run
        Assert.Equal(0, invokeCount);
    }

    [Fact]
    [Trait("Category", "Phase15")]
    public void SerializeAll_DoesNotReorderPredicates()
    {
        // FK ordering only applies to DeserializeAll, not SerializeAll
        var predA = new ProviderPredicateDefinition { Name = "A", ProviderType = "SqlTable", Table = "A" };
        var predB = new ProviderPredicateDefinition { Name = "B", ProviderType = "SqlTable", Table = "B" };

        var fkResolver = CreateFkResolver(("A", "B")); // A depends on B

        var callOrder = new List<string>();
        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Serialize(It.IsAny<ProviderPredicateDefinition>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ProviderPredicateDefinition pred, string _, Action<string>? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                callOrder.Add(pred.Table!);
                return new SerializeResult { RowsSerialized = 1, TableName = pred.Table! };
            });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, fkResolver);
        orchestrator.SerializeAll(new List<ProviderPredicateDefinition> { predA, predB }, "/output", SerializerMode.Replace, ConflictStrategy.SourceWins);

        // Original order preserved: A, B (not reordered to B, A)
        Assert.Equal(new[] { "A", "B" }, callOrder);
    }

    [Fact]
    [Trait("Category", "Phase15")]
    public void DeserializeAll_EmptyServiceCaches_SucceedsWithoutCacheCall()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomOrderFlow",
            Files = Array.Empty<string>(),
            Table = "EcomOrderFlow",
            ServiceCaches = new List<string>() // empty
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 1, TableName = "EcomOrderFlow" });

        var resolverCalls = 0;
        var cacheInvalidator = new CacheInvalidator(_ =>
        {
            resolverCalls++;
            return null;
        });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, cacheInvalidator: cacheInvalidator);
        var result = orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.Single(result.EntryOutcomes);
        Assert.False(result.HasErrors);
        // Empty ServiceCaches → orchestrator short-circuits before calling the resolver.
        Assert.Equal(0, resolverCalls);
    }

    // === Phase 25 Tests: Schema Sync (PORT bucket) ===

    [Fact]
    [Trait("Category", "Phase25")]
    public void DeserializeAll_CallsSchemaSyncAfterEntryWithSchemaSyncConfig()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomProductGroupField",
            Files = Array.Empty<string>(),
            Table = "EcomProductGroupField",
            SchemaSync = "EcomGroupFields"
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 3, TableName = "EcomProductGroupField" });

        var mockSchemaSync = new Mock<EcomGroupFieldSchemaSync>(MockBehavior.Loose, new object[] { new Mock<ISqlExecutor>().Object });
        mockSchemaSync.Setup(s => s.SyncSchema(It.IsAny<Action<string>?>()));

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, ecomSchemaSync: mockSchemaSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSchemaSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Phase25")]
    public void DeserializeAll_DryRun_DoesNotCallSchemaSync()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomProductGroupField",
            Files = Array.Empty<string>(),
            Table = "EcomProductGroupField",
            SchemaSync = "EcomGroupFields"
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 3, TableName = "EcomProductGroupField" });

        var mockSchemaSync = new Mock<EcomGroupFieldSchemaSync>(MockBehavior.Loose, new object[] { new Mock<ISqlExecutor>().Object });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, ecomSchemaSync: mockSchemaSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: true, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSchemaSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Phase25")]
    public void DeserializeAll_NoSchemaSyncProperty_DoesNotCallSchemaSync()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomOrderFlow",
            Files = Array.Empty<string>(),
            Table = "EcomOrderFlow"
            // No SchemaSync property
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 1, TableName = "EcomOrderFlow" });

        var mockSchemaSync = new Mock<EcomGroupFieldSchemaSync>(MockBehavior.Loose, new object[] { new Mock<ISqlExecutor>().Object });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry, ecomSchemaSync: mockSchemaSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSchemaSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Never);
    }

    // === LRN-hosted-publish-01: column-backed product-field schema sync ===

    private static (Mock<ISerializationProvider> Provider, ProviderRegistry Registry) OkSqlProviderRegistry(string tableName)
    {
        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 2, TableName = tableName });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);
        return (sqlProvider, registry);
    }

    private static Mock<EcomProductFieldSchemaSync> MockProductFieldSync()
        => new(MockBehavior.Loose, new object[] { new Mock<ISqlExecutor>().Object });

    [Fact]
    public void DeserializeAll_RunsProductFieldSchemaSync_ForEcomProductFieldTable_WithoutMarker()
    {
        // The shipped config that surfaced the bug had NO schemaSync marker — the table name
        // alone must trigger the sync so those payloads are protected.
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomProductField",
            Files = Array.Empty<string>(),
            Table = "EcomProductField"
        };
        var (_, registry) = OkSqlProviderRegistry("EcomProductField");
        var mockSync = MockProductFieldSync();

        var orchestrator = new SerializerOrchestrator(registry, ecomProductFieldSchemaSync: mockSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Once);
        mockSync.Verify(s => s.WarnMissingColumns(It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public void DeserializeAll_RunsProductFieldSchemaSync_ForEcomProductFieldsMarker()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomProductField",
            Files = Array.Empty<string>(),
            Table = "EcomProductField",
            SchemaSync = "EcomProductFields"
        };
        var (_, registry) = OkSqlProviderRegistry("EcomProductField");
        var mockSync = MockProductFieldSync();

        var orchestrator = new SerializerOrchestrator(registry, ecomProductFieldSchemaSync: mockSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public void DeserializeAll_DryRun_DoesNotRunProductFieldSchemaSync()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomProductField",
            Files = Array.Empty<string>(),
            Table = "EcomProductField"
        };
        var (_, registry) = OkSqlProviderRegistry("EcomProductField");
        var mockSync = MockProductFieldSync();

        var orchestrator = new SerializerOrchestrator(registry, ecomProductFieldSchemaSync: mockSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: true, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public void DeserializeAll_UnrelatedTable_DoesNotRunProductFieldSchemaSync()
    {
        var entry = new SqlTableEntry
        {
            EntryId = "sql/EcomOrderFlow",
            Files = Array.Empty<string>(),
            Table = "EcomOrderFlow"
        };
        var (_, registry) = OkSqlProviderRegistry("EcomOrderFlow");
        var mockSync = MockProductFieldSync();

        var orchestrator = new SerializerOrchestrator(registry, ecomProductFieldSchemaSync: mockSync.Object);
        orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        mockSync.Verify(s => s.SyncSchema(It.IsAny<Action<string>?>()), Times.Never);
    }

    // === LRN-sapporo-02: clean fragment-only merge must report status ok ===

    [Fact]
    public void DeserializeEntries_CleanFragmentMerge_ContentAndSql_ZeroFailed_EmptyErrors_IsNotError()
    {
        // Reproduces the LRN-sapporo-02 shape observed on 0.8.0-beta: a fragment-only merge
        // with content + sql entries, 0 failed, empty Errors — which returned status="error"
        // with an empty Errors list. Status must derive from failed>0 || Errors.Any().
        //
        // The root-cause hypothesis named a content entry reporting all-zero counts as a
        // non-success sentinel, so the content entry here returns 0/0/0/0 with no errors.
        var contentEntry = new ContentEntry
        {
            EntryId = "content/area-1",
            Files = Array.Empty<string>(),
            AreaId = 1,
            AreaName = "Fragment",
            Path = "/",
            PageId = 0
        };
        var sqlEntry = new SqlTableEntry
        {
            EntryId = "sql/EcomProductConfiguratorGroups",
            Files = Array.Empty<string>(),
            Table = "EcomProductConfiguratorGroups"
        };

        var contentProvider = new Mock<ISerializationProvider>();
        contentProvider.Setup(p => p.ProviderType).Returns("Content");
        contentProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 0, Updated = 0, Skipped = 0, Failed = 0, TableName = "Content" });

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 10, Updated = 0, Skipped = 0, Failed = 0, TableName = "EcomProductConfiguratorGroups" });

        var registry = new ProviderRegistry();
        registry.Register(contentProvider.Object);
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry);
        var result = orchestrator.DeserializeEntries(
            new List<ManifestEntry> { contentEntry, sqlEntry }, "/input", SerializerMode.Merge,
            ConflictStrategy.DestinationWins, log: null, isDryRun: false, providerFilter: null,
            escalator: null, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // Aggregation invariants: 0 failed, no run-level errors, so HasErrors is false.
        Assert.Equal(0, result.EntryOutcomes.Sum(o => o.Counts.Failed));
        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.EntryOutcomes, o => o.Status == EntryStatus.Failed);

        // The clean run's summary must NOT carry a trailing "Errors:" fragment (the 0.8.0 shape).
        Assert.DoesNotContain("Errors:", result.Summary);

        // The HTTP status mapped from this result must be Ok, not Error.
        var mapped = DeserializeCommand.InvokeMapStatusForTest(result);
        Assert.Equal(CommandResult.ResultType.Ok, mapped.Status);
    }

    [Fact]
    [Trait("Category", "Phase15")]
    public void DeserializeAll_CacheInvalidationFailure_LoggedButDoesNotBlockOtherEntries()
    {
        var entry1 = new SqlTableEntry
        {
            EntryId = "sql/EcomPayments",
            Files = Array.Empty<string>(),
            Table = "EcomPayments",
            ServiceCaches = new List<string> { "PaymentService" }
        };
        var entry2 = new SqlTableEntry
        {
            EntryId = "sql/EcomShippings",
            Files = Array.Empty<string>(),
            Table = "EcomShippings",
            ServiceCaches = new List<string> { "ShippingService" }
        };

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 1, TableName = "Test" });

        var goodInvoked = 0;
        var cacheInvalidator = new CacheInvalidator(name =>
            name.Equals("ShippingService", StringComparison.OrdinalIgnoreCase)
                ? new DwCacheServiceRegistry.CacheClearEntry("ShippingService", "Test.ShippingService", () => goodInvoked++)
                : null);

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);

        var logs = new List<string>();
        var orchestrator = new SerializerOrchestrator(registry, cacheInvalidator: cacheInvalidator);
        var result = orchestrator.DeserializeEntries(
            new List<ManifestEntry> { entry1, entry2 }, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: msg => logs.Add(msg), isDryRun: false,
            providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // Both entries should have been processed
        Assert.Equal(2, result.EntryOutcomes.Count);
        // Cache failure was logged
        Assert.Contains(logs, l => l.Contains("WARNING") && l.Contains("Cache invalidation failed"));
        // Good cache was still cleared
        Assert.Equal(1, goodInvoked);
    }

    // =========================================================================
    // Phase 43 Layer A acceptance tests — SC-1, SC-2, SC-3, SC-6
    // These exercise the new manifest-driven DeserializeAll path via the
    // internal DeserializeEntries test seam (avoids on-disk manifest setup).
    // =========================================================================

    /// <summary>
    /// SC-1: orchestrator dispatches every entry from a manifest, populating EntryOutcomes
    /// with one outcome per dispatched entry.
    /// </summary>
    [Fact]
    [Trait("Category", "Phase43")]
    public void DeserializeAll_ManifestDriven_DispatchesEachEntry_SC1()
    {
        var entries = new List<ManifestEntry> { ContentEntry1, SqlTableEntryFx };

        var result = _orchestrator.DeserializeEntries(
            entries,
            modeRoot: "/tmp/modeRoot",
            mode: SerializerMode.Replace,
            strategy: ConflictStrategy.SourceWins,
            log: null,
            isDryRun: false,
            providerFilter: null,
            escalator: null,
            excludeFieldsByItemType: null,
            excludeXmlElementsByType: null);

        Assert.Equal(2, result.EntryOutcomes.Count);
        Assert.Contains(result.EntryOutcomes, o => o.EntryId == "content/area-1" && o.Status == EntryStatus.Succeeded);
        Assert.Contains(result.EntryOutcomes, o => o.EntryId == "sql/EcomOrderFlow" && o.Status == EntryStatus.Succeeded);
        Assert.False(result.HasErrors);
    }

    /// <summary>
    /// SC-2: providerFilter exclusion produces an EntryStatus.Skipped outcome (today's
    /// silent-skip class becomes observable per REPORT-01 / D-02).
    /// </summary>
    [Fact]
    [Trait("Category", "Phase43")]
    public void DeserializeAll_ProviderFilterExclusion_ReportsSkipped_SC2()
    {
        var entries = new List<ManifestEntry> { ContentEntry1, SqlTableEntryFx };

        var result = _orchestrator.DeserializeEntries(
            entries,
            modeRoot: "/tmp/modeRoot",
            mode: SerializerMode.Replace,
            strategy: ConflictStrategy.SourceWins,
            log: null,
            isDryRun: false,
            providerFilter: "Content",
            escalator: null,
            excludeFieldsByItemType: null,
            excludeXmlElementsByType: null);

        Assert.Contains(result.EntryOutcomes, o => o.EntryId == "sql/EcomOrderFlow" && o.Status == EntryStatus.Skipped);
        Assert.Contains(result.EntryOutcomes, o => o.EntryId == "content/area-1" && o.Status == EntryStatus.Succeeded);
        // Skipped is not a failure → HasErrors stays false.
        Assert.False(result.HasErrors);
    }

    /// <summary>
    /// SC-3a: HasErrors is true when at least one EntryOutcome has Status == Failed.
    /// Direct test of the aggregation invariant in OrchestratorResult.
    /// </summary>
    [Fact]
    [Trait("Category", "Phase43")]
    public void OrchestratorResult_HasErrors_TrueWhenAnyEntryFailed_SC3()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                EntryOutcome.Skipped(ContentEntry1, "filtered"),
                EntryOutcome.Failed(SqlTableEntryFx, "FK violation")
            }
        };
        Assert.True(result.HasErrors);
    }

    /// <summary>
    /// SC-3b: HasErrors is false when no entry outcome failed, even with mixed Skipped
    /// + Succeeded outcomes.
    /// </summary>
    [Fact]
    [Trait("Category", "Phase43")]
    public void OrchestratorResult_HasErrors_FalseWhenAllSucceededOrSkipped_SC3()
    {
        var result = new OrchestratorResult
        {
            EntryOutcomes = new List<EntryOutcome>
            {
                EntryOutcome.Skipped(ContentEntry1, "filtered"),
                EntryOutcome.From(SqlTableEntryFx,
                    new ProviderDeserializeResult { Created = 5, TableName = "EcomOrderFlow" },
                    TimeSpan.FromMilliseconds(50))
            }
        };
        Assert.False(result.HasErrors);
    }

    /// <summary>
    /// SC-6: FK ordering operates on entries[] live-recomputed regardless of input order.
    /// Two different input orderings produce identical dispatch order.
    /// </summary>
    [Fact]
    [Trait("Category", "Phase43")]
    public void DeserializeAll_ShuffledManifestEntries_ProducesSameDispatchOrder_SC6()
    {
        // 3 SqlTableEntries A, B, C where FK requires C before B before A.
        var entryA = new SqlTableEntry { EntryId = "sql/A", Files = Array.Empty<string>(), Table = "A" };
        var entryB = new SqlTableEntry { EntryId = "sql/B", Files = Array.Empty<string>(), Table = "B" };
        var entryC = new SqlTableEntry { EntryId = "sql/C", Files = Array.Empty<string>(), Table = "C" };

        var fkResolver = CreateFkResolver(("A", "B"), ("B", "C"));

        var dispatchedOrder = new List<string>();
        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(),
                It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(),
                It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ManifestEntry e, string _, Action<string>? _, bool _, ConflictStrategy _, Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                dispatchedOrder.Add(((SqlTableEntry)e).Table);
                return new ProviderDeserializeResult { Created = 1, TableName = ((SqlTableEntry)e).Table };
            });

        var registry = new ProviderRegistry();
        registry.Register(sqlProvider.Object);
        var orchestrator = new SerializerOrchestrator(registry, fkResolver);

        // Run with ABC ordering.
        var unshuffled = new List<ManifestEntry> { entryA, entryB, entryC };
        orchestrator.DeserializeEntries(unshuffled, "/tmp", SerializerMode.Replace, ConflictStrategy.SourceWins,
            log: null, isDryRun: false, providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null);
        var orderUnshuffled = dispatchedOrder.ToList();
        dispatchedOrder.Clear();

        // Run with shuffled ordering — FK reorder must produce the same dispatch sequence.
        var shuffled = new List<ManifestEntry> { entryC, entryA, entryB };
        orchestrator.DeserializeEntries(shuffled, "/tmp", SerializerMode.Replace, ConflictStrategy.SourceWins,
            log: null, isDryRun: false, providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null);
        var orderShuffled = dispatchedOrder.ToList();

        Assert.Equal(orderUnshuffled, orderShuffled);
        Assert.Equal(new[] { "C", "B", "A" }, orderUnshuffled);
    }

    // -------------------------------------------------------------------------
    // Engine issue #35: LINK-02 runs Content entries before SqlTable entries, so an area's
    // ecom language must be validated against the target PLUS what the same run delivers.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a run where a SqlTable entry opts into link resolution (so LINK-02 moves the
    /// Content entry first), the Content entry reports its ecom language ENU as missing at
    /// write time, and the <c>sql/EcomLanguages</c> entry either delivers ENU or does not.
    /// </summary>
    private static (SerializerOrchestrator Orchestrator, List<ManifestEntry> Entries, List<string> CallOrder)
        BuildEcomLanguageRun(bool packageDeliversLanguage)
    {
        var callOrder = new List<string>();
        var languagesOnTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var contentProvider = new Mock<ISerializationProvider>();
        contentProvider.Setup(p => p.ProviderType).Returns("Content");
        contentProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ManifestEntry e, string _, Action<string>? _, bool _, ConflictStrategy _, Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                callOrder.Add(e.EntryId);
                // What ContentDeserializer records when ENU is not on target at write time.
                var pending = languagesOnTarget.Contains("ENU")
                    ? Array.Empty<Truvio.Commerce.Serializer.Serialization.PendingEcomLanguageCheck>()
                    : new[] { new Truvio.Commerce.Serializer.Serialization.PendingEcomLanguageCheck(1, "ENU") };
                return new ProviderDeserializeResult
                {
                    Created = 1,
                    TableName = "Content",
                    SourceToTargetPageMap = new Dictionary<int, int> { [10] = 110 },
                    PendingEcomLanguageChecks = pending
                };
            });

        var sqlProvider = new Mock<ISerializationProvider>();
        sqlProvider.Setup(p => p.ProviderType).Returns("SqlTable");
        sqlProvider.Setup(p => p.Deserialize(It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(), It.IsAny<ConflictStrategy>(), It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>(), It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns((ManifestEntry e, string _, Action<string>? _, bool _, ConflictStrategy _, Truvio.Commerce.Serializer.Serialization.InternalLinkResolver? _, IReadOnlyDictionary<string, List<string>>? _, IReadOnlyDictionary<string, List<string>>? _) =>
            {
                callOrder.Add(e.EntryId);
                var table = ((SqlTableEntry)e).Table;
                if (packageDeliversLanguage && table == "EcomLanguages")
                    languagesOnTarget.Add("ENU");
                return new ProviderDeserializeResult { Created = 1, TableName = table };
            });

        var registry = new ProviderRegistry();
        registry.Register(contentProvider.Object);
        registry.Register(sqlProvider.Object);

        var orchestrator = new SerializerOrchestrator(registry,
            ecomLanguageExists: id => languagesOnTarget.Contains(id));

        // Manifest order puts the language first; LINK-02 still runs Content first.
        var entries = new List<ManifestEntry>
        {
            SqlEntry("EcomLanguages"),
            new SqlTableEntry
            {
                EntryId = "sql/UrlPath",
                Files = Array.Empty<string>(),
                Table = "UrlPath",
                ResolveLinksInColumns = new[] { "UrlPathRedirect" }
            },
            ContentEntry1
        };
        return (orchestrator, entries, callOrder);
    }

    [Fact]
    public void DeserializeEntries_ContentRunsFirst_LanguageDeliveredBySamePackage_NoWarning()
    {
        var (orchestrator, entries, callOrder) = BuildEcomLanguageRun(packageDeliversLanguage: true);
        var escalator = new StrictModeEscalator(strict: true, log: null);
        var logs = new List<string>();

        var result = orchestrator.DeserializeEntries(entries, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: logs.Add, isDryRun: false, providerFilter: null,
            escalator: escalator, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        // LINK-02 ordering is unchanged: the Content entry ran before sql/EcomLanguages.
        Assert.Equal("content/area-1", callOrder[0]);
        Assert.True(callOrder.IndexOf("content/area-1") < callOrder.IndexOf("sql/EcomLanguages"));
        Assert.False(result.HasErrors);
        Assert.Empty(result.Errors);
        Assert.DoesNotContain(logs, l => l.Contains("WARNING") && l.Contains("ecom language"));
    }

    [Fact]
    public void DeserializeEntries_LanguageMissingEverywhere_StillFailsStrict()
    {
        var (orchestrator, entries, callOrder) = BuildEcomLanguageRun(packageDeliversLanguage: false);
        var escalator = new StrictModeEscalator(strict: true, log: null);
        var logs = new List<string>();

        var result = orchestrator.DeserializeEntries(entries, "/input", SerializerMode.Replace,
            ConflictStrategy.SourceWins, log: logs.Add, isDryRun: false, providerFilter: null,
            escalator: escalator, excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.Equal("content/area-1", callOrder[0]);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Contains("Area 1 references ecom language 'ENU' which does not exist on target"));
        Assert.Contains(logs, l => l.StartsWith("WARNING: Area 1 references ecom language 'ENU'"));
    }
}
