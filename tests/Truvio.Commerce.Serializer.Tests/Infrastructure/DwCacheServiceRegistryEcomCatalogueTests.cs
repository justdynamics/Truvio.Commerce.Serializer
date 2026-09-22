using Truvio.Commerce.Serializer.Infrastructure;
using Truvio.Commerce.Serializer.Providers;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Issue #14: after a DB reset plus Deserialize, a shop written into EcomShops stayed
/// invisible (get_shops [], patch_shops "not found") until an app-pool recycle, because the
/// shop and group service caches were neither registered nor cleared.
/// </summary>
public class DwCacheServiceRegistryEcomCatalogueTests
{
    [Theory]
    [InlineData("ShopService")]
    [InlineData("Dynamicweb.Ecommerce.Shops.ShopService")]
    [InlineData("GroupService")]
    [InlineData("Dynamicweb.Ecommerce.Products.GroupService")]
    public void Resolve_FindsTheCatalogueServices_ByShortAndFullName(string name)
    {
        Assert.NotNull(DwCacheServiceRegistry.Resolve(name));
    }

    [Fact]
    public void AllSupportedNames_ListsTheCatalogueServices()
    {
        Assert.Contains("ShopService", DwCacheServiceRegistry.AllSupportedNames);
        Assert.Contains("GroupService", DwCacheServiceRegistry.AllSupportedNames);
    }

    [Theory]
    [InlineData("EcomShops", new[] { "ShopService", "GroupService" })]
    [InlineData("ecomshops", new[] { "ShopService", "GroupService" })]
    [InlineData("EcomShopGroupRelation", new[] { "ShopService", "GroupService" })]
    [InlineData("EcomGroups", new[] { "GroupService" })]
    [InlineData("EcomGroupRelations", new[] { "GroupService" })]
    public void ImpliedCachesForTable_CoversTheTablesADeserializeWrites(string table, string[] expected)
    {
        Assert.Equal(expected, DwCacheServiceRegistry.ImpliedCachesForTable(table));
    }

    [Theory]
    [InlineData("EcomOrders")]
    [InlineData("Page")]
    [InlineData("")]
    [InlineData(null)]
    public void ImpliedCachesForTable_IsEmptyForEverythingElse(string? table)
    {
        Assert.Empty(DwCacheServiceRegistry.ImpliedCachesForTable(table));
    }

    [Fact]
    public void EveryImpliedName_ResolvesInTheRegistry()
    {
        // An implied name that is not registered would throw at InvalidateCaches mid-run.
        foreach (var table in new[] { "EcomShops", "EcomShopGroupRelation", "EcomGroups", "EcomGroupRelations" })
            foreach (var name in DwCacheServiceRegistry.ImpliedCachesForTable(table))
                Assert.NotNull(DwCacheServiceRegistry.Resolve(name));
    }

    [Fact]
    public void InvalidateCaches_ClearsTheImpliedShopCaches()
    {
        // Fake resolver: proves invocation without touching real DW service singletons.
        var cleared = new List<string>();
        var invalidator = new CacheInvalidator(name =>
            new DwCacheServiceRegistry.CacheClearEntry(name, "Fake." + name, () => cleared.Add(name)));

        invalidator.InvalidateCaches(DwCacheServiceRegistry.ImpliedCachesForTable("EcomShops"));

        Assert.Equal(new[] { "ShopService", "GroupService" }, cleared);
    }
}
