# SQL tables

`SqlTable` predicates capture arbitrary SQL tables as YAML, one file per row.
Use them for reference data, ecommerce configuration, URL redirects,
user groups, or any table whose rows map naturally to replace or merge
content. This page covers when to reach for them, the full field surface,
and the validation guarantees.

## Table of contents

- [When to use SqlTable vs Content](#when-to-use-sqltable-vs-content)
- [Minimal predicate](#minimal-predicate)
- [Row identity: nameColumn vs composite key](#row-identity-namecolumn-vs-composite-key)
- [Tables without a primary key](#tables-without-a-primary-key)
- [WHERE clauses](#where-clauses)
- [includeFields / excludeFields](#includefields--excludefields)
- [xmlColumns and excludeXmlElements](#xmlcolumns-and-excludexmlelements)
- [serviceCaches](#servicecaches)
- [compareColumns for change detection](#comparecolumns-for-change-detection)
- [resolveLinksInColumns](#resolvelinksincolumns)
- [Schema sync](#schema-sync)
- [The directory-read contract](#the-directory-read-contract)

## When to use SqlTable vs Content

Use **Content** predicates when:

- You're syncing pages, grids, paragraphs, areas, or item-type-tagged
  content the end user edits through DW admin's content tree.
- Permission mapping, GUID identity, and ItemType field rewriting
  matter. Content predicates route through the full DW content API; a
  SqlTable predicate pointed at `[Page]` and `[Paragraph]` would bypass
  every DW side-effect that makes content actually work.

Use **SqlTable** predicates when:

- The data is structured table data — reference lists (countries,
  currencies), ecommerce config (payment methods, shipping methods,
  VAT rules, order flows), redirects (`UrlPath`), users / groups.
- There is no content-tree hierarchy; rows are a flat set with a
  natural key.
- You need `WHERE`-clause filtering to include a subset of rows from a
  mixed-purpose table.

**Never** use SqlTable to sync `[Page]`, `[Paragraph]`, `[GridRow]`, or
`[Area]` — these are Content territory. Bypassing DW's content APIs
skips caching, permission inheritance, navigation refresh, and property
normalization. Use the Content predicate.

## Minimal predicate

```json
{
  "name": "EcomOrderFlow",
  "providerType": "SqlTable",
  "table": "EcomOrderFlow",
  "nameColumn": "OrderFlowName"
}
```

This captures every row in `EcomOrderFlow`, writes one YAML file per row
named by the `OrderFlowName` value, and will deserialize back into the
target matching rows by `OrderFlowName` (source-wins overwrite in Replace
mode).

## Row identity: nameColumn vs composite key

`nameColumn` is optional but usually preferred.

**With `nameColumn`:** One file per row, named after the `nameColumn`
value. The file name becomes the natural key. `Default.yml` / `Quote.yml`
/ `Subscription.yml` is scannable in Git and reads well in diffs. On
deserialize, the writer matches rows by `nameColumn`; existing rows get
updated, new rows get inserted.

**Without `nameColumn`:** The writer falls back to the table's composite
primary key. File names derive from the key values — for
`EcomShopGroupRelation` (a junction table with composite PK
`ShopGroupShopId, ShopGroupGroupId`), files look like
`SHOP1$$GROUP253.yml`. Less readable but works for tables without a
natural single-column key.

`nameColumn` must exist on the table and be validated against
`INFORMATION_SCHEMA.COLUMNS` at config-load. A misspelled column fails
loud:

```
Column identifier not in INFORMATION_SCHEMA: '[EcomVatGroups].[VatName]'.
Check exclude/include/where fields in your predicate config.
```

A typo like `VatName` instead of `VatGroupName` is exactly this bug;
`SqlIdentifierValidator` catches it at config-load.

## Tables without a primary key

Some platform tables are heaps: they carry an identity column and no
primary-key index. On DW 10.28 that list is `DynamicStructures`,
`Languages`, `ScreenLayout`, `ScreenLayoutEditor`, `ScreenLayoutGroup` and
`ScreenLayoutTab`. A customer table can be a heap too.

Such a table used to be written with a truncate and re-insert, in Replace and
in Merge alike, so a Merge of one row deleted every target row the payload did
not carry and the run still reported `N created, 0 failed`
(justdynamics/Truvio.Commerce.Foundry#1305). That is gone. The engine now
resolves a match key and upserts, and Merge never deletes.

### Resolution order

The first step that resolves wins:

1. **The declared PRIMARY KEY.** Unchanged behaviour, and silent.
2. **The entry's `keyColumns`.** Explicit beats inference.
3. **A UNIQUE index or UNIQUE constraint.** Read from `sys.indexes` /
   `sys.index_columns`. A filtered index, a disabled index, an index with a
   nullable column and an index over nothing but identity columns are all
   rejected: none of them can match a row reliably. The narrowest remaining
   candidate is used.
4. **The full column tuple.** Every column the payload carries, identity
   columns excluded. An exact-row match: an identical row is skipped, a
   different row is inserted.

An identity column is never a match key on its own. Auto-ids are
environment-local, so matching on one binds the write to an unrelated target
row. For the same reason a heap written by an inferred key is inserted without
`SET IDENTITY_INSERT`: the target assigns its own id.

### `keyColumns`

Optional, SqlTable only. The columns to match target rows on when the table
declares no primary key. Ignored on a table that has one.

```json
{
  "name": "Dynamic workspaces",
  "providerType": "SqlTable",
  "table": "DynamicStructures",
  "keyColumns": ["DynamicStructureUniqueId"]
}
```

A column named here that does not exist on the target table fails the entry
with a message naming the column. Prefer `keyColumns` over the all-columns
fallback wherever the table has a real natural key: an all-columns match
treats any edited row as a new row.

### `replaceStrategy`

Optional, SqlTable only, default absent. The one setting that still deletes.

```json
"replaceStrategy": "truncate"
```

Under Replace, `truncate` deletes every row of the target table before the
payload is written, and the deleted rows are counted in the run report. Absent,
Replace upserts on the resolved key and leaves target rows the payload does not
carry alone.

Under Merge the field is ignored and the run logs
`WARNING: [T] declares replaceStrategy: truncate, which is ignored under Merge`.
Merge never deletes, whatever the entry says. Any value other than `truncate`
fails the entry.

Reach for `truncate` only when the layer owns the whole table and a stale
target row is a defect, for instance a fully-generated lookup table. On a table
a host also writes to, it destroys the host's rows.

### The WARNING and the Deleted count

Every inferred key logs one line per entry:

```
WARNING: [DynamicStructures] has no primary key; rows matched by keyColumns; target rows not in the payload are preserved.
```

The resolution reads `keyColumns`, `unique index (<name>)` or `all columns`.
The `WARNING` prefix rides the strict-mode escalator, so a strict run records
it without any extra plumbing, and the same text lands in the entry's
`warnings` list in the deserialize report.

The report also carries a per-entry `deleted` count, and a run-level
`totalDeleted`, next to `created` / `updated` / `skipped` / `failed`. It is
non-zero only for an entry that opted into `replaceStrategy: truncate`, which
makes `deleted == 0` a machine-checkable assertion for a delivery gate running
Merge.

## WHERE clauses

The optional `where` field filters rows at serialize time. Use it to
include only a subset of rows from a mixed-purpose table:

```json
{
  "name": "AccessUser-Roles",
  "providerType": "SqlTable",
  "table": "AccessUser",
  "where": "AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors','CMS Editors')",
  "excludeFields": ["AccessUserPassword", "AccessUserPasswordSalt"]
}
```

This emits only the admin and editor group rows, leaving customer users
(AccessUserType = 1) out of the baseline.

### Validation rules

Every WHERE clause is validated at config-load and at admin-UI save:

- **Banned tokens** (literal substring scan, case-insensitive): `;`, `--`,
  `/*`, `*/`, `xp_`, `sp_executesql`.
- **Banned keywords** (whole-word, case-insensitive): `SELECT`, `UPDATE`,
  `DELETE`, `INSERT`, `MERGE`, `EXEC`, `EXECUTE`, `DROP`, `TRUNCATE`,
  `ALTER`, `CREATE`, `GRANT`, `REVOKE`, `UNION`, `INTO`, `WAITFOR`,
  `SHUTDOWN`.
- **Identifier check.** After stripping string literals, every
  identifier-shaped token (starts with a letter or underscore, not a
  known operator keyword) must match a column on the target table per
  `INFORMATION_SCHEMA.COLUMNS`.
- **Safe operators.** `AND`, `OR`, `NOT`, `IN`, `IS`, `NULL`, `LIKE`,
  `BETWEEN`, `TRUE`, `FALSE` pass through.

String literals are elided before tokenization so content like
`'Admin Select Group'` passes. Rejection messages name the offending
token and show the full clause:

```
WHERE clause references unknown identifier 'AccessUserUsrName'.
Not a column on the target table. Check INFORMATION_SCHEMA.
Clause: AccessUserUsrName = 'admin'
```

### Limits

- Single-table only. No joins, subqueries, or `EXISTS` blocks.
- Literal values only. String values in single quotes, numeric literals
  inline. Parameterization is not supported — the serializer runs under
  admin trust and reads only, so value injection is a non-issue; the
  surface is identifier splicing, which the validator closes.
- No function calls. `LOWER(AccessUserUserName) = 'admin'` is rejected
  (`LOWER` is not an allowed identifier).

Source: `src/Truvio.Commerce.Serializer/Configuration/SqlWhereClauseValidator.cs`.

## includeFields / excludeFields

**excludeFields** strips columns from serialization output. Common uses:

- Masking credentials (`AccessUserPassword`, `PaymentMerchantNum`,
  `PaymentGatewayMD5Key`, `CarrierAccount`).
- Masking environment-specific columns that the auto-exclude list
  doesn't cover yet.
- Dropping identity / audit columns like `CreatedBy` or timestamps
  where they add noise to diffs.

```json
"excludeFields": [
  "AccessUserPassword",
  "AccessUserPasswordSalt",
  "AccessUserLastLoginDate"
]
```

**includeFields** opts a column back IN that would otherwise be removed
by `RuntimeExcludes` (the auto-exclude list for runtime-only columns).
See [`runtime-exclusions.md`](runtime-exclusions.md) for the auto-exclude
set.

```json
{
  "name": "Shops (with index)",
  "providerType": "SqlTable",
  "table": "EcomShops",
  "includeFields": ["ShopIndexRepository", "ShopIndexName"]
}
```

Both lists are validated against `INFORMATION_SCHEMA.COLUMNS` at
config-load. Misspelled columns fail loud.

Effective serialized column set, per row:

```
all columns of table
  minus RuntimeExcludes[table]
  plus predicate.includeFields
  minus predicate.excludeFields
```

`includeFields` wins over `RuntimeExcludes`. `excludeFields` wins over
everything else.

## xmlColumns and excludeXmlElements

DW stores rich configuration in XML-shaped string columns:
`PaymentGatewayParameters`, `PaymentCheckoutParameters`,
`ShippingServiceParameters`, `OrderFlowXml`. Raw SQL serialization would
write them as single-line encoded strings — unreadable in YAML, useless
in diffs.

`xmlColumns` marks these columns for pretty-printing. At serialize, the
column value is XML-parsed and written as a nested YAML structure. At
deserialize, it's reassembled back to XML before the MERGE:

```json
{
  "name": "EcomPayments",
  "providerType": "SqlTable",
  "table": "EcomPayments",
  "nameColumn": "PaymentName",
  "xmlColumns": [
    "PaymentGatewayParameters",
    "PaymentCheckoutParameters"
  ]
}
```

`excludeXmlElements` strips specific XML elements from every XML column
on every row. The typical use case is masking env-specific page-ID
references embedded in the XML payload so the baseline stays portable:

```json
"excludeXmlElements": [
  "EmptyCartRedirectPage",
  "ShoppingCartLink"
]
```

Both lists are case-sensitive at the element name level. Validation is
structural (well-formed XML on write) rather than schema-driven; DW's
own consumer of the XML column is the final arbiter of correctness.

## serviceCaches

DW caches many domain objects in process memory:
`CountryService`, `VatGroupService`, `PaymentService`. When
deserialize writes to the underlying SQL tables, those caches go stale
until the next TTL expiry. Pages rendering immediately after a deserialize
can serve cached values that no longer match the DB.

`serviceCaches` lists service types to clear after the predicate's
deserialize completes:

```json
"serviceCaches": [
  "Dynamicweb.Ecommerce.International.CountryService",
  "Dynamicweb.Ecommerce.International.CountryRelationService"
]
```

Accepted forms:

- **Short name:** `CountryService`
- **Full type name:** `Dynamicweb.Ecommerce.International.CountryService`

Both resolve case-insensitively through `DwCacheServiceRegistry`.
Unknown names fail at config-load with the full supported-names list
(eighteen entries today). Adding a new service is a PR against
`DwCacheServiceRegistry.cs` — see
[`strict-mode.md`](strict-mode.md#adding-a-new-cache-service).

An entry read from a composed `{mode}-manifest.json` never passes through
config-load, so the same check runs again at manifest read, before the first
row of the run is written. An unknown name fails the call naming the entry and
the name, with nothing written — it used to reach `InvalidateCaches` only
after that entry's rows were on the target, where strict mode turned the
invalidation warning into an entry failure.

### Tables with no registry cache

The registry covers areas, countries, country relations, currencies,
languages, VAT groups, VAT-group country relations, payments and shippings.
It covers **no product, group, variant-group or variant-option service**, so
there is no valid `serviceCaches` name for the `EcomProducts` family:
`EcomProducts`, `EcomGroups`, `EcomVariantGroups`, `EcomVariantsOptions`,
`EcomProductCategory*`, `EcomPrices` and the relation tables between them.

Naming one anyway (`Dynamicweb.Ecommerce.Products.GroupService` and the
`ProductService` / `VariantGroupService` / `VariantOptionService` forms are the
ones layer authors reach for) is rejected now, at manifest read. Leave
`serviceCaches` off those entries.

The invalidation route for them is an **application-pool recycle** after the
run:

```powershell
& $env:SystemRoot\System32\inetsrv\appcmd.exe recycle apppool "<host>"
```

A product index build does not clear these caches either: it reads the
database, it does not invalidate the in-process object caches. Recycle, then
rebuild the index.

## compareColumns for change detection

`compareColumns` drives the "skip unchanged" path. If set, deserialize
reads the target row's values for the listed columns and compares them
to the YAML's values. Matching rows go to `skipped`; mismatching rows
go to `updated`.

```json
{
  "name": "EcomOrderFlow",
  "providerType": "SqlTable",
  "table": "EcomOrderFlow",
  "nameColumn": "OrderFlowName",
  "compareColumns": "OrderFlowName,OrderFlowDescription,OrderFlowActive"
}
```

Empty or absent: all non-identity columns are compared. That default is
usually correct; set `compareColumns` explicitly only when specific
columns should drive skip-detection (e.g. ignoring audit timestamps).

Column names must exist on the table; validated at config-load.

## resolveLinksInColumns

Columns holding cross-environment page references (`Default.aspx?ID=N`
strings) opt into link rewriting at deserialize time:

```json
{
  "name": "UrlPath",
  "providerType": "SqlTable",
  "table": "UrlPath",
  "resolveLinksInColumns": ["UrlPathRedirect"]
}
```

At deserialize:

1. Content predicates run first, building the source → target page ID
   map via `InternalLinkResolver.BuildSourceToTargetMap`.
2. `SqlTableWriter` reads the row's column value.
3. `InternalLinkResolver.ResolveInStringColumn(value)` rewrites
   `Default.aspx?ID=N` (and `"SelectedValue": "N"` in ButtonEditor JSON)
   using the map.
4. The rewritten string is parameter-bound into MERGE — the raw rewrite
   never reaches SQL composition.

Column names validated at config-load, same gate as `excludeFields`.
Unresolved references log `WARNING: Unresolvable page ID N in link` and
— in strict mode — escalate at end of run. See
[`link-resolution.md`](link-resolution.md) for the three-pass pipeline.

## Schema sync

One predicate-level directive bridges table-shape differences between
source and target DBs:

```json
"schemaSync": "EcomGroupFields"
```

Currently `EcomGroupFields` is the only recognized value. It runs
`EcomGroupFieldSchemaSync` before the predicate's row writes, ensuring
the `EcomProductGroupField` definitions exist on target and the
corresponding custom columns are added to `[EcomGroups]` before any
row data flows in. Without this, a fresh target without matching field
definitions rejects the row writes with column-not-found errors.

The normal operational answer to schema differences is to align DW
NuGet versions between source and target. `schemaSync: EcomGroupFields`
is a targeted mitigation for the one DW-native case where the schema
depends on row data in a separate table.

For the broader "DW NuGet versions don't match" problem, see
[`troubleshooting.md`](troubleshooting.md#source-column-tc-not-present-on-target-schema--skipping).

## The directory-read contract

A deserialize reads every `*.yml` in `_sql/<Table>/` except `_meta.yml`. Three
rules govern what that means, and all three are contracts, not accidents.

**Order is ordinal by file name.** `StringComparer.Ordinal`, so uppercase
sorts before lowercase and the order is identical on every host. The read used
the culture-sensitive default comparer before, which made the order — and with
it which of two same-identity documents was applied last — a property of the
host's culture.

**The manifest entry's `files[]` is consulted.** A document sitting in the
directory that no manifest entry names is applied anyway (dropping it would
silently change what a composed layer writes) and reported:

```
WARNING: [EcomGroups] 2 document(s) in _sql/EcomGroups/ are not named by the manifest entry's files[] and are applied anyway: zz-brand-1.yml, zz-brand-2.yml.
```

The `WARNING` prefix rides the strict-mode escalator, so a strict run fails on
the drift rather than absorbing it. Re-serialize or re-compose so the manifest
names every document it ships.

**Same identity in one pass is merged later-layer-wins.** Two documents can
carry the same row identity — the layered-composition case is a brand layer's
partial override row (`ownership: replace`, only some columns) next to a demo
layer's full row. They are merged into one row before anything is written:
every column of the later document overrides the earlier one, columns only the
earlier document carries are kept, and the later document's ownership header
applies. One row is written, not two, and the log names it:

```
  [EcomGroups] identity 'GROUP1' is carried by more than one document — merged later-layer-wins (3 column(s) from the later document).
```

Before this, both documents took the "not in the target snapshot" path and the
file that sorted last won outright, so on a blank target a partial document
could insert a row carrying only its own columns.

## See also

- [Configuration](configuration.md#sqltable-predicate-fields) — every field in one place
- [Runtime exclusions](runtime-exclusions.md) — the auto-excluded column list and credential handling
- [Link resolution](link-resolution.md) — how `resolveLinksInColumns` works end to end
- [Strict mode](strict-mode.md) — escalation of SQL-related warnings
