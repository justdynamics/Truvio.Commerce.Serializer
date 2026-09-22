# Caches a Deserialize invalidates

Dynamicweb serves most of its configuration from in-process service caches. The
Serializer writes to the database underneath them, so a write is only visible to
the admin, the API and the storefront once the owning cache is cleared. This page
says which caches a Deserialize clears, which it cannot, and what that leaves for
an app-pool recycle.

## Two ways a cache gets cleared

**Declared** — a SqlTable predicate lists cache services in `serviceCaches`.
Every name is validated at config-load against `DwCacheServiceRegistry`; an
unknown name fails the load rather than surfacing mid-run.

```json
{
  "table": "EcomCurrencies",
  "serviceCaches": ["Dynamicweb.Ecommerce.International.CurrencyService"]
}
```

**Implied** — some tables are behind a cache whether or not the predicate says
so. `DwCacheServiceRegistry.ImpliedCachesForTable` maps those tables to their
services, and `SerializerOrchestrator` clears the union of declared and implied
after the entry's write. This is issue #14: after a DB reset plus Deserialize,
`SHOP1` existed in `EcomShops` but `get_shops` returned `[]` and `patch_shops`
answered "not found" until a recycle, because no baseline config listed the shop
service.

| Table | Implied caches |
|---|---|
| `EcomShops` | `ShopService`, `GroupService` |
| `EcomShopGroupRelation` | `ShopService`, `GroupService` |
| `EcomGroups` | `GroupService` |
| `EcomGroupRelations` | `GroupService` |

Implied clearing does not replace `serviceCaches`: that list stays the way a
predicate declares caches the engine cannot infer from the table name.

## Registered services

`DwCacheServiceRegistry.Entries` — a curated list of typed `ClearCache()` calls,
no reflection. Resolution accepts the short class name or the fully-qualified
type name, case-insensitively.

| Short name | Type |
|---|---|
| `AreaService` | `Dynamicweb.Content.AreaService` |
| `CountryService` | `Dynamicweb.Ecommerce.International.CountryService` |
| `CountryRelationService` | `Dynamicweb.Ecommerce.International.CountryRelationService` |
| `CurrencyService` | `Dynamicweb.Ecommerce.International.CurrencyService` |
| `LanguageService` | `Dynamicweb.Ecommerce.International.LanguageService` |
| `VatGroupService` | `Dynamicweb.Ecommerce.International.VatGroupService` |
| `VatGroupCountryRelationService` | `Dynamicweb.Ecommerce.International.VatGroupCountryRelationService` |
| `ShopService` | `Dynamicweb.Ecommerce.Shops.ShopService` |
| `GroupService` | `Dynamicweb.Ecommerce.Products.GroupService` |
| `PaymentService` | `Dynamicweb.Ecommerce.Orders.PaymentService` |
| `ShippingService` | `Dynamicweb.Ecommerce.Orders.ShippingService` |

New services are added by PR: append an entry, commit, release. If DW renames or
removes one upstream, the build fails at the registry, which is the design goal.

## What still needs a recycle

Clearing a service cache is not a substitute for a restart. After a Deserialize,
an app-pool recycle is still required for:

- **Item types** — an item-type XML written in this run defines a table and a
  metadata graph that `ItemManager` loads at startup; new or changed types are
  only fully live after a recycle.
- **Add-ins and assemblies** — anything that changes what is loaded into the
  process (never install a second version of an add-in that is already
  installed: `AddInManager` throws and the site 500s on every path).
- **Templates and layouts** that DW compiles and caches per process.
- **Ecommerce data not covered by a registered service** — any `Ecom*` table
  whose service is not in the registry and not implied by the table name. If a
  write is invisible after a Deserialize and the table is not in the tables above,
  that is the case to suspect: recycle, then add the service to the registry by PR
  so the next run does not need one.
- **Product index data** — index builds are drained by the scheduled
  `Repository task handler`, not by a cache clear.

Dry runs clear nothing: cache invalidation is skipped when `isDryRun` is set, and
is skipped for an entry that ended with errors.

## See also

- [SQL tables](sql-tables.md) — `serviceCaches` on a predicate
- [`DwCacheServiceRegistry.cs`](../src/Truvio.Commerce.Serializer/Infrastructure/DwCacheServiceRegistry.cs)
- [`CacheInvalidator.cs`](../src/Truvio.Commerce.Serializer/Providers/CacheInvalidator.cs)
