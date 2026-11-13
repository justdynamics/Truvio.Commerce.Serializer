using System.Linq;
using Truvio.Commerce.Serializer.Configuration;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// Phase 40 Plan 04 Task 2: regression coverage for the three example/documentation
/// configs (<c>demo-sync.json</c>, <c>ecommerce-predicates-example.json</c>,
/// <c>full-sync-example.json</c>). Per checker Warning #5 (no-backcompat is also
/// no-trap-for-the-user), these copy-paste artefacts MUST parse cleanly through
/// <see cref="ConfigLoader.Load(string, SqlIdentifierValidator?)"/>; if any predicate
/// regresses to a missing-<c>mode</c> shape, ConfigLoader will hard-reject and the
/// user copying the example produces an unloadable config.
///
/// Uses <c>identifierValidator: null</c> overload — same scope decision as
/// Swift22BaselineRoundTripTests: assertions are limited to JSON-shape parse +
/// per-predicate <c>Mode</c> resolution, not the SqlIdentifierValidator pipeline.
/// </summary>
public class ExampleConfigsLoadTests
{
    private static string ResolveConfigPath(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Truvio.Commerce.Serializer", "Configuration", fileName);
        return Path.GetFullPath(path);
    }

    [Fact]
    public void Load_DemoSync_Parses()
    {
        var path = ResolveConfigPath("demo-sync.json");
        Assert.True(File.Exists(path), $"Example file not found at: {path}");

        var config = ConfigLoader.Load(path, identifierValidator: null);

        Assert.NotEmpty(config.Predicates);
        Assert.All(config.Predicates, p => Assert.Equal(SerializerMode.Replace, p.Mode));
    }

    [Fact]
    public void Load_EcommercePredicatesExample_Parses()
    {
        var path = ResolveConfigPath("ecommerce-predicates-example.json");
        Assert.True(File.Exists(path), $"Example file not found at: {path}");

        var config = ConfigLoader.Load(path, identifierValidator: null);

        Assert.NotEmpty(config.Predicates);
        Assert.All(config.Predicates, p => Assert.Equal(SerializerMode.Replace, p.Mode));
    }

    [Fact]
    public void Load_FullSyncExample_Parses()
    {
        var path = ResolveConfigPath("full-sync-example.json");
        Assert.True(File.Exists(path), $"Example file not found at: {path}");

        var config = ConfigLoader.Load(path, identifierValidator: null);

        Assert.NotEmpty(config.Predicates);
        Assert.All(config.Predicates, p => Assert.Equal(SerializerMode.Replace, p.Mode));
    }

    [Theory]
    [InlineData("full-sync-example.json")]
    [InlineData("swift-starter.json")]
    public void ShippedExamples_ShipNoItemListRows_SoListsTravelInsideTheirDocuments(string file)
    {
        // Content ownership R7: a SqlTable predicate on ItemList / ItemListRelation would keep
        // every document in the 1.0.9-beta form.
        var config = ConfigLoader.Load(ResolveConfigPath(file), identifierValidator: null);

        Assert.True(Truvio.Commerce.Serializer.Serialization.ItemListTravelPolicy.ListsTravelInDocuments(config.Predicates));
    }
    [Fact]
    public void Load_SwiftStarter_ResolvesReplaceAndMerge_WithReplaceMergeSubfolders()
    {
        var path = ResolveConfigPath("swift-starter.json");
        Assert.True(File.Exists(path), $"Example file not found at: {path}");

        var config = ConfigLoader.Load(path, identifierValidator: null);

        Assert.Contains(config.Predicates, p => p.Mode == SerializerMode.Replace);
        Assert.Contains(config.Predicates, p => p.Mode == SerializerMode.Merge);
        Assert.Equal("replace", config.ReplaceOutputSubfolder);
        Assert.Equal("merge", config.MergeOutputSubfolder);
        Assert.Equal("replace", config.GetSubfolderForMode(SerializerMode.Replace));
        Assert.Equal("merge", config.GetSubfolderForMode(SerializerMode.Merge));
    }
}
