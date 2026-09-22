using Truvio.Commerce.Serializer.Infrastructure;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Infrastructure;

/// <summary>
/// Engine issue #12: a manifest entry's <c>serviceCaches</c> never passed through the
/// config-load gate, so an unknown name was only discovered at invalidation — after the
/// entry's rows were written, where strict mode turned the warning into an entry failure.
/// </summary>
[Trait("Category", "Issue12")]
public class ManifestCacheValidationTests
{
    private static SqlTableEntry Entry(string entryId, string table, params string[] serviceCaches) => new()
    {
        EntryId = entryId,
        Files = Array.Empty<string>(),
        Table = table,
        ServiceCaches = serviceCaches
    };

    [Fact]
    public void ValidateServiceCaches_UnknownName_Throws_NamingEntryAndName()
    {
        var entries = new ManifestEntry[]
        {
            Entry("sql/EcomVariantGroups", "EcomVariantGroups", "Dynamicweb.Ecommerce.Products.VariantGroupService")
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ManifestCacheValidation.ValidateServiceCaches(entries));

        Assert.Contains("sql/EcomVariantGroups", ex.Message);
        Assert.Contains("Dynamicweb.Ecommerce.Products.VariantGroupService", ex.Message);
        // The operator needs to know what to do instead: recycle.
        Assert.Contains("recycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateServiceCaches_MultipleUnknownNames_NamesEveryOne()
    {
        var entries = new ManifestEntry[]
        {
            Entry("sql/EcomVariantGroups", "EcomVariantGroups", "Dynamicweb.Ecommerce.Products.VariantGroupService"),
            Entry("sql/EcomProducts", "EcomProducts", "Dynamicweb.Ecommerce.Products.ProductService")
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ManifestCacheValidation.ValidateServiceCaches(entries));

        Assert.Contains("sql/EcomVariantGroups", ex.Message);
        Assert.Contains("sql/EcomProducts", ex.Message);
    }

    [Theory]
    [InlineData("CountryService")]
    [InlineData("Dynamicweb.Ecommerce.International.CountryService")]
    [InlineData("countryservice")]
    public void ValidateServiceCaches_RegisteredName_ShortOrFullOrCased_DoesNotThrow(string name)
    {
        var entries = new ManifestEntry[] { Entry("sql/EcomCountry", "EcomCountry", name) };

        ManifestCacheValidation.ValidateServiceCaches(entries);
    }

    [Fact]
    public void ValidateServiceCaches_NoServiceCaches_DoesNotThrow()
    {
        var entries = new ManifestEntry[]
        {
            Entry("sql/EcomOrderFlow", "EcomOrderFlow"),
            new ContentEntry
            {
                EntryId = "content/area-1",
                Files = Array.Empty<string>(),
                AreaId = 1,
                AreaName = "Area 1",
                Path = "/",
                PageId = 0
            }
        };

        ManifestCacheValidation.ValidateServiceCaches(entries);
    }
}
