using Dynamicweb.Content;
using Dynamicweb.Extensibility.Dependencies;
using Microsoft.Extensions.DependencyInjection;
using EcomServices = Dynamicweb.Ecommerce.Services;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Phase 37-04 / CACHE-01: curated static map of DW service caches to direct
/// <c>ClearCache()</c> actions. Replaces the AddInManager-based <c>ICacheResolver</c>
/// path (F-10 baseline: 9 of 10 lookups silently returned "Cache type not found").
///
/// <para>New services are added by PR: append an entry to <see cref="Entries"/>,
/// commit, release. No reflection, no runtime type lookup — every action is
/// resolvable at compile time. If a DW service is renamed or removed upstream,
/// the build fails here, which is the design goal.</para>
///
/// <para>Resolution is case-insensitive and accepts BOTH the short class name
/// (e.g. <c>"CountryService"</c>) and the fully-qualified .NET type name
/// (e.g. <c>"Dynamicweb.Ecommerce.International.CountryService"</c>). The Swift
/// 2.2 baseline config uses the fully-qualified form; admin UI screens prefer
/// the short form.</para>
/// </summary>
public static class DwCacheServiceRegistry
{
    /// <summary>
    /// A single registered cache service: its short class name, its fully-qualified
    /// .NET type name, and the <see cref="Action"/> that clears it.
    /// </summary>
    public record CacheClearEntry(string ShortName, string FullTypeName, Action Invoke);

    /// <summary>
    /// Resolve a DW service cache by type through the DW dependency resolver and call
    /// its <c>ClearCache()</c> method (inherited from <c>Dynamicweb.Caching.ICacheStorage</c>).
    /// Every ecommerce service in this registry implements ICacheStorage; the DW
    /// <c>Dynamicweb.Ecommerce.Services</c> static locator uses the same DependencyResolver
    /// path internally, so this mirrors the canonical DW10 access pattern.
    /// </summary>
    private static void ClearCacheOf<T>() where T : class
    {
        var service = ((IServiceProvider)DependencyResolver.Current).GetRequiredService<T>();
        if (service is Dynamicweb.Caching.ICacheStorage cache)
            cache.ClearCache();
        else
            throw new InvalidOperationException(
                $"Service '{typeof(T).FullName}' does not implement ICacheStorage.");
    }

    /// <summary>
    /// The curated list. Each entry is a typed direct call — no reflection.
    /// Must cover every <c>serviceCaches</c> value referenced by
    /// <c>src/Truvio.Commerce.Serializer/Configuration/swift2.2-baseline.json</c>.
    ///
    /// <para><b>Ecommerce services</b> are instance types with instance <c>ClearCache()</c>
    /// methods resolved through <c>DependencyResolver.Current</c> (same path used by
    /// <c>Dynamicweb.Ecommerce.Services</c>). <b>Content.Areas</b> is a pre-instantiated
    /// static property. Both patterns are compile-time-typed.</para>
    /// </summary>
    private static readonly CacheClearEntry[] Entries = new[]
    {
        // Content / Areas — pre-instantiated static property
        new CacheClearEntry(
            "AreaService",
            "Dynamicweb.Content.AreaService",
            () => Services.Areas.ClearCache()),

        // Ecommerce International — instance services resolved via DependencyResolver
        new CacheClearEntry(
            "CountryService",
            "Dynamicweb.Ecommerce.International.CountryService",
            () => EcomServices.Countries.ClearCache()),
        new CacheClearEntry(
            "CountryRelationService",
            "Dynamicweb.Ecommerce.International.CountryRelationService",
            () => EcomServices.CountryRelations.ClearCache()),
        new CacheClearEntry(
            "CurrencyService",
            "Dynamicweb.Ecommerce.International.CurrencyService",
            () => EcomServices.Currencies.ClearCache()),
        new CacheClearEntry(
            "LanguageService",
            "Dynamicweb.Ecommerce.International.LanguageService",
            () => EcomServices.Languages.ClearCache()),
        new CacheClearEntry(
            "VatGroupService",
            "Dynamicweb.Ecommerce.International.VatGroupService",
            () => EcomServices.VatGroups.ClearCache()),
        // VatGroupCountryRelationService has no static locator accessor; resolve directly.
        new CacheClearEntry(
            "VatGroupCountryRelationService",
            "Dynamicweb.Ecommerce.International.VatGroupCountryRelationService",
            () => ClearCacheOf<Dynamicweb.Ecommerce.International.VatGroupCountryRelationService>()),

        // Ecommerce catalogue — issue #14. A Deserialize writes EcomShops / EcomGroups
        // through the SqlTable provider, behind the shop and group service caches: without
        // these, get_shops answers [] and patch_shops "not found" until an app-pool recycle.
        new CacheClearEntry(
            "ShopService",
            "Dynamicweb.Ecommerce.Shops.ShopService",
            () => ClearCacheOf<Dynamicweb.Ecommerce.Shops.ShopService>()),
        new CacheClearEntry(
            "GroupService",
            "Dynamicweb.Ecommerce.Products.GroupService",
            () => EcomServices.ProductGroups.ClearCache()),

        // Ecommerce Orders
        new CacheClearEntry(
            "PaymentService",
            "Dynamicweb.Ecommerce.Orders.PaymentService",
            () => EcomServices.Payments.ClearCache()),
        new CacheClearEntry(
            "ShippingService",
            "Dynamicweb.Ecommerce.Orders.ShippingService",
            () => EcomServices.Shippings.ClearCache()),

        // NOTE: Dynamicweb.SystemTools.TranslationLanguageService (referenced in
        // swift2.2-baseline.json for EcomLanguages) is NOT available in DW 10.23.9's
        // NuGet surface — no matching type exists across Dynamicweb.*, Dynamicweb.Core,
        // Dynamicweb.Ecommerce or Dynamicweb.Users. It likely existed in an earlier DW
        // release and was removed / merged. Entry intentionally omitted; baseline
        // configs that reference it now fail loud at config-load, which is the
        // documented CACHE-01 behavior.
    };

    private static readonly Dictionary<string, CacheClearEntry> ByName =
        Entries
            .SelectMany(e => new[] { (key: e.ShortName, entry: e), (key: e.FullTypeName, entry: e) })
            .ToDictionary(t => t.key, t => t.entry, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<string> _allNames =
        Entries
            .SelectMany(e => new[] { e.ShortName, e.FullTypeName })
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Resolve by short name OR fully-qualified type name. Case-insensitive.
    /// Returns <c>null</c> for unknown / null / whitespace names.
    /// </summary>
    public static CacheClearEntry? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return ByName.TryGetValue(name, out var entry) ? entry : null;
    }

    /// <summary>
    /// Issue #14: caches a write to a given table invalidates WHETHER OR NOT the predicate
    /// declares them. A predicate's <c>serviceCaches</c> list is a config choice, and the
    /// Swift baselines did not list the catalogue services — so a Deserialize that created
    /// SHOP1 left the shop service serving its pre-write (empty) snapshot until a recycle.
    /// These are the tables whose DW service cache is unconditionally stale after a write.
    ///
    /// <para>NOT a substitute for <c>serviceCaches</c>: that list stays the way a predicate
    /// declares caches the engine cannot infer. The two are unioned at the call site.</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> ImpliedByTable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // EcomShops rows are served by ShopService; shop→group relations by GroupService,
            // so a shop write invalidates both.
            ["EcomShops"] = new[] { "ShopService", "GroupService" },
            ["EcomShopGroupRelation"] = new[] { "ShopService", "GroupService" },
            ["EcomGroups"] = new[] { "GroupService" },
            ["EcomGroupRelations"] = new[] { "GroupService" },
        };

    /// <summary>
    /// Issue #14: the registered cache names a write to <paramref name="tableName"/> must
    /// clear even when the predicate declares none. Empty for every other table.
    /// </summary>
    public static IReadOnlyList<string> ImpliedCachesForTable(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return Array.Empty<string>();
        return ImpliedByTable.TryGetValue(tableName.Trim(), out var names)
            ? names
            : Array.Empty<string>();
    }

    /// <summary>
    /// The sorted union of every registered short name and full type name.
    /// Used by ConfigLoader to build helpful "supported: ..." error messages
    /// when an unknown cache name is encountered.
    /// </summary>
    public static IReadOnlyList<string> AllSupportedNames => _allNames;
}
