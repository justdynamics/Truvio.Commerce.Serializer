using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.Configuration;

/// <summary>
/// 1.0.0-beta inline scope: the configuration is the boundary. A scope must fall inside a
/// configured predicate of the same mode, can only narrow it, and passes the same identifier gate.
/// </summary>
public class InlineScopeResolverTests
{
    private static SerializerConfiguration Config() => new()
    {
        OutputDirectory = "Serializer",
        Predicates = new List<ProviderPredicateDefinition>
        {
            new()
            {
                Name = "Site", ProviderType = "Content", Mode = SerializerMode.Replace, AreaId = 3, Path = "/",
                Excludes = new() { "/Posts", "/Customer Center/Drafts" },
                ExcludeFields = new() { "AreaDomain" },
                AcknowledgedOrphanPageIds = new() { 42 }
            },
            new() { Name = "Posts", ProviderType = "Content", Mode = SerializerMode.Merge, AreaId = 3, Path = "/Posts" },
            new()
            {
                Name = "EcomCountries", ProviderType = "SqlTable", Mode = SerializerMode.Replace, Table = "EcomCountries",
                NameColumn = "CountryCode", Where = "CountryRegion = 'EU'", ServiceCaches = new() { "CountryService" },
                IncludeFields = new() { "CountryIndex" }
            },
            new() { Name = "EcomProducts", ProviderType = "SqlTable", Mode = SerializerMode.Merge, Table = "EcomProducts" }
        }
    };

    private static SqlIdentifierValidator Validator() => new(
        () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EcomCountries", "EcomProducts" },
        table => table.Equals("EcomCountries", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CountryCode", "CountryRegion", "CountryName", "CountryIndex" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ProductId", "ProductName" });

    private static InlineScopeResolution Resolve(InlineScope scope, SerializerMode mode = SerializerMode.Replace,
        bool forDeserialize = false, Func<int, (int, string)?>? resolvePage = null) =>
        InlineScopeResolver.Resolve(scope, mode, Config(), Validator(), forDeserialize, resolvePage);

    [Fact]
    public void Content_InsideFence_NarrowsAndInheritsDefaults()
    {
        var r = Resolve(new InlineScope { AreaId = 3, Path = "/Customer Center/", ExcludeFields = new() { "Secret" } });

        Assert.True(r.IsValid, string.Join(" ", r.Errors));
        Assert.Equal("Site", r.Fence!.Name);
        var p = r.Predicate!;
        Assert.Equal("/Customer Center", p.Path);
        Assert.Equal(SerializerMode.Replace, p.Mode);
        Assert.True(p.IsInlineScope);
        Assert.Equal(new[] { "AreaDomain", "Secret" }, p.ExcludeFields);
        Assert.Equal(new[] { "/Customer Center/Drafts" }, p.Excludes);
        Assert.Equal(new[] { 42 }, p.AcknowledgedOrphanPageIds);
    }

    [Fact]
    public void Content_UnderFenceExclude_OwnedByOtherMode_IsRejectedWithModeHint()
    {
        var r = Resolve(new InlineScope { AreaId = 3, Path = "/Posts/2024" });

        Assert.False(r.IsValid);
        var message = string.Join(" ", r.Errors);
        Assert.Contains("outside what Serializer.config.json allows", message);
        Assert.Contains("'Posts'", message);
        Assert.Contains("mode=merge", message);
    }

    [Fact]
    public void Content_SameScopeInOwningMode_IsAccepted()
    {
        var r = Resolve(new InlineScope { AreaId = 3, Path = "/Posts/2024" }, SerializerMode.Merge);

        Assert.True(r.IsValid, string.Join(" ", r.Errors));
        Assert.Equal("Posts", r.Fence!.Name);
        Assert.Equal(SerializerMode.Merge, r.Predicate!.Mode);
    }

    [Fact]
    public void Content_UnconfiguredArea_IsRejected()
    {
        var r = Resolve(new InlineScope { AreaId = 9, Path = "/" });
        Assert.False(r.IsValid);
        Assert.Contains("area 9", string.Join(" ", r.Errors));
    }

    [Fact]
    public void Content_ExcludeNotBelowPath_IsRejected()
    {
        var r = Resolve(new InlineScope { AreaId = 3, Path = "/Customer Center", Excludes = new() { "/About" } });
        Assert.False(r.IsValid);
        Assert.Contains("scope.excludes '/About' is not below scope.path", string.Join(" ", r.Errors));
    }

    [Fact]
    public void Content_SqlTableField_IsRejected()
    {
        var r = Resolve(new InlineScope { ProviderType = "Content", AreaId = 3, Path = "/", Where = "X = 1" });
        Assert.False(r.IsValid);
        Assert.Contains("scope.where is not valid for a Content scope", string.Join(" ", r.Errors));
    }

    [Fact]
    public void Content_PageId_ResolvesToPath()
    {
        var r = Resolve(new InlineScope { PageId = 17 }, resolvePage: id => id == 17 ? (3, "/Customer Center/Orders") : null);

        Assert.True(r.IsValid, string.Join(" ", r.Errors));
        Assert.Equal(3, r.Predicate!.AreaId);
        Assert.Equal("/Customer Center/Orders", r.Predicate.Path);
        Assert.Equal(17, r.Predicate.PageId);
    }

    [Fact]
    public void Content_SwitchingOnLanguageLayers_IsRejected()
    {
        var r = Resolve(new InlineScope { AreaId = 3, Path = "/Customer Center", IncludeLanguageLayers = true });
        Assert.False(r.IsValid);
        Assert.Contains("includeLanguageLayers", string.Join(" ", r.Errors));
    }

    [Fact]
    public void Content_FenceOwnedAcknowledgedOrphans_MustMatch()
    {
        var r = Resolve(new InlineScope { AreaId = 3, Path = "/Customer Center", AcknowledgedOrphanPageIds = new() { 1 } });
        Assert.False(r.IsValid);
        Assert.Contains("owned by the configuration", string.Join(" ", r.Errors));
    }

    [Fact]
    public void SqlTable_WhereIsAndedWithFence()
    {
        var r = Resolve(new InlineScope { Table = "EcomCountries", Where = "CountryCode = 'DK'" });

        Assert.True(r.IsValid, string.Join(" ", r.Errors));
        Assert.Equal("(CountryRegion = 'EU') AND (CountryCode = 'DK')", r.Predicate!.Where);
        Assert.Equal("CountryCode", r.Predicate.NameColumn);
        Assert.Equal(new[] { "CountryService" }, r.Predicate.ServiceCaches);
    }

    [Fact]
    public void SqlTable_FenceOwnedField_DifferentValue_IsRejected_SameValue_IsAccepted()
    {
        Assert.False(Resolve(new InlineScope { Table = "EcomCountries", NameColumn = "CountryName" }).IsValid);
        Assert.True(Resolve(new InlineScope { Table = "EcomCountries", NameColumn = "countrycode" }).IsValid);
    }

    [Fact]
    public void SqlTable_IncludeFieldsWidening_IsRejected()
    {
        var r = Resolve(new InlineScope { Table = "EcomCountries", IncludeFields = new() { "CountryName" } });
        Assert.False(r.IsValid);
        Assert.Contains("widen", string.Join(" ", r.Errors));
    }

    [Fact]
    public void SqlTable_UnknownColumn_FailsIdentifierGate()
    {
        var r = Resolve(new InlineScope { Table = "EcomCountries", ExcludeFields = new() { "NoSuchColumn" } });
        Assert.False(r.IsValid);
        Assert.Contains("NoSuchColumn", string.Join(" ", r.Errors));
    }

    [Fact]
    public void SqlTable_WhereWithUnknownColumn_FailsWhereGate()
    {
        var r = Resolve(new InlineScope { Table = "EcomCountries", Where = "Bogus = 'x'" });
        Assert.False(r.IsValid);
    }

    [Fact]
    public void SqlTable_OtherModeTable_IsRejectedWithModeHint()
    {
        var r = Resolve(new InlineScope { Table = "EcomProducts" });
        Assert.False(r.IsValid);
        Assert.Contains("mode=merge", string.Join(" ", r.Errors));
    }

    [Fact]
    public void SqlTable_OnDeserialize_RowFilters_AreRejected()
    {
        var r = Resolve(new InlineScope { Table = "EcomCountries", Where = "CountryCode = 'DK'" }, forDeserialize: true);
        Assert.False(r.IsValid);
        Assert.Contains("not valid on Deserialize", string.Join(" ", r.Errors));

        Assert.True(Resolve(new InlineScope { Table = "EcomCountries" }, forDeserialize: true).IsValid);
    }

    [Fact]
    public void UnknownProviderType_IsRejected()
    {
        Assert.False(Resolve(new InlineScope { ProviderType = "Files" }).IsValid);
    }

    [Fact]
    public void InlineScope_ParsesPredicateShapedJson()
    {
        var scope = InlineScope.Parse("{\"areaId\":3,\"path\":\"/Customer Center\",\"excludes\":[\"/Customer Center/Drafts\"]}");
        Assert.NotNull(scope);
        Assert.Equal(3, scope!.AreaId);
        Assert.Single(scope.Excludes!);
    }
}
