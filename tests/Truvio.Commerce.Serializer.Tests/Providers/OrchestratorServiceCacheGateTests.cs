using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Providers;
using Moq;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Providers;

/// <summary>
/// Engine issue #12: the manifest-entry <c>serviceCaches</c> gate runs before any entry is
/// dispatched, so an unknown cache name fails the call with nothing written instead of
/// failing the entry at invalidation with its rows already on the target.
/// </summary>
[Trait("Category", "Issue12")]
public class OrchestratorServiceCacheGateTests
{
    private static (SerializerOrchestrator orchestrator, Mock<ISerializationProvider> provider) Build()
    {
        var provider = new Mock<ISerializationProvider>();
        provider.Setup(p => p.ProviderType).Returns("SqlTable");
        provider.Setup(p => p.Deserialize(
                It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(),
                It.IsAny<ConflictStrategy>(),
                It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>()))
            .Returns(new ProviderDeserializeResult { Created = 1, TableName = "EcomGroups" });

        var registry = new ProviderRegistry();
        registry.Register(provider.Object);
        return (new SerializerOrchestrator(registry), provider);
    }

    private static SqlTableEntry Entry(string table, params string[] caches) => new()
    {
        EntryId = $"sql/{table}",
        Files = Array.Empty<string>(),
        Table = table,
        ServiceCaches = caches
    };

    [Fact]
    public void DeserializeEntries_UnknownServiceCache_ThrowsBeforeAnyEntryIsDispatched()
    {
        var (orchestrator, provider) = Build();
        var entries = new ManifestEntry[]
        {
            Entry("EcomGroups", "Dynamicweb.Ecommerce.Products.GroupService")
        };

        var ex = Assert.Throws<InvalidOperationException>(() => orchestrator.DeserializeEntries(
            entries, modeRoot: "unused", mode: SerializerMode.Merge,
            strategy: ConflictStrategy.DestinationWins, log: null, isDryRun: false,
            providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null));

        Assert.Contains("sql/EcomGroups", ex.Message);
        Assert.Contains("Dynamicweb.Ecommerce.Products.GroupService", ex.Message);

        provider.Verify(p => p.Deserialize(
                It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(),
                It.IsAny<ConflictStrategy>(),
                It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>()),
            Times.Never);
    }

    [Fact]
    public void DeserializeEntries_UnknownCacheOnOneEntry_StopsTheWholeRun()
    {
        // The good entry sorts first; the gate must still stop the run before it is dispatched.
        var (orchestrator, provider) = Build();
        var entries = new ManifestEntry[]
        {
            Entry("EcomOrderFlow"),
            Entry("EcomProducts", "Dynamicweb.Ecommerce.Products.ProductService")
        };

        Assert.Throws<InvalidOperationException>(() => orchestrator.DeserializeEntries(
            entries, modeRoot: "unused", mode: SerializerMode.Merge,
            strategy: ConflictStrategy.DestinationWins, log: null, isDryRun: false,
            providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null));

        provider.Verify(p => p.Deserialize(
                It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(),
                It.IsAny<ConflictStrategy>(),
                It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>()),
            Times.Never);
    }

    [Fact]
    public void DeserializeEntries_RegisteredServiceCache_Dispatches()
    {
        var (orchestrator, provider) = Build();
        var entries = new ManifestEntry[] { Entry("EcomCountry", "CountryService") };

        var result = orchestrator.DeserializeEntries(
            entries, modeRoot: "unused", mode: SerializerMode.Merge,
            strategy: ConflictStrategy.DestinationWins, log: null, isDryRun: true,
            providerFilter: null, escalator: null,
            excludeFieldsByItemType: null, excludeXmlElementsByType: null);

        Assert.Single(result.ManifestEntryOutcomes);
        provider.Verify(p => p.Deserialize(
                It.IsAny<ManifestEntry>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<bool>(),
                It.IsAny<ConflictStrategy>(),
                It.IsAny<Truvio.Commerce.Serializer.Serialization.InternalLinkResolver?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>(),
                It.IsAny<IReadOnlyDictionary<string, List<string>>?>()),
            Times.Once);
    }
}
