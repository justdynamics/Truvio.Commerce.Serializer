# Troubleshooting

Common errors and what to do about them. Grouped by where the error
surfaces: config-load, serialize-time, deserialize-time, and deploy-time.

## Table of contents

- [Config-load errors](#config-load-errors)
- [Serialize-time errors](#serialize-time-errors)
- [Deserialize-time warnings (under strict mode)](#deserialize-time-warnings-under-strict-mode)
- [Deploy-time operational issues](#deploy-time-operational-issues)
- [Round-trip fidelity issues](#round-trip-fidelity-issues)
- [When to use the cleanup scripts](#when-to-use-the-cleanup-scripts)

## Config-load errors

These fire at `ConfigLoader.Load()` before any SQL runs. The Management
API returns `Invalid`; curl with `-f` exits non-zero.

### Table identifier not in INFORMATION_SCHEMA

```
Table identifier not in INFORMATION_SCHEMA: 'ExomShops'.
Check the 'table' value in your predicate config.
```

Misspelled `table` name in a SqlTable predicate. Compare against
`INFORMATION_SCHEMA.TABLES` on the source DB:

```sql
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE 'Ecom%';
```

Fix the predicate's `table` value and re-save.

### Column identifier not in INFORMATION_SCHEMA

```
Column identifier not in INFORMATION_SCHEMA: '[EcomVatGroups].[VatName]'.
Check exclude/include/where fields in your predicate config.
```

A column named in `nameColumn`, `compareColumns`, `includeFields`,
`xmlColumns`, `resolveLinksInColumns`, `keyColumns`, or a `where`
clause doesn't exist on the table. An `excludeFields` name the table lacks
does not fail the load (engine issue #37): it logs
`[Serializer] Info: ... excludeFields names column(s) the host's [Table] does not have`
and is ignored, so a misspelled exclusion shows up there, not here.
The reference Swift 2.2 baseline hit this three times (the `excludeFields` row
would now be the info line):

| Wrong name | Correct name | Location |
|------------|--------------|----------|
| `VatName` | `VatGroupName` | `EcomVatGroups.nameColumn` |
| `ShopAutoId` | `PaymentAutoId` | `EcomPayments.excludeFields` |
| `ShippingParameters` | `ShippingServiceParameters` | `EcomShippings.xmlColumns` |

Confirm against the live schema:

```sql
SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'EcomVatGroups'
ORDER BY ORDINAL_POSITION;
```

### WHERE clause contains banned token / keyword

```
WHERE clause contains banned keyword 'EXEC': AccessUserType = 2 AND EXEC xp_dothing()
```

Or:

```
WHERE clause contains banned token ';': AccessUserUserName = 'Admin'; DROP TABLE X
```

The clause includes something the validator rejects. See
[`sql-tables.md`](sql-tables.md#validation-rules) for the full banned-token
and banned-keyword lists. The clause must be a single-table row filter
with literal values — no DDL, no stored procs, no subqueries.

### WHERE clause references unknown identifier

```
WHERE clause references unknown identifier 'AccessUserUsrName'.
Not a column on the target table. Check INFORMATION_SCHEMA.
Clause: AccessUserUsrName = 'admin'
```

Typo in a column name inside the WHERE clause. The validator tokenizes
the clause and checks every identifier against the table's columns.
String literals are elided, so content like `'Admin Select Group'`
passes — only unquoted identifiers are checked.

### Cache service not in DwCacheServiceRegistry

```
Configuration is invalid — ServiceCaches validation failed:
  - predicates 'EcomSomething': cache service
    'Dynamicweb.Ecommerce.New.XService' is not in DwCacheServiceRegistry.
    Supported (18 total): AreaService, CountryRelationService, CountryService, ...
```

The `serviceCaches` entry points at a type that isn't in the curated
registry. Either:

- The name is misspelled. Re-check against the "Supported" list in the
  error message.
- The service needs to be added to the registry. Append an entry to
  `src/Truvio.Commerce.Serializer/Infrastructure/DwCacheServiceRegistry.cs`,
  rebuild the DLL, redeploy. See
  [`strict-mode.md`](strict-mode.md#adding-a-new-cache-service).

## Serialize-time errors

### Baseline link sweep found N unresolvable references

```
Baseline link sweep found 2 unresolvable reference(s):
  - ID 9579 in page 3fa... (/Footer) / PropertyFields.LinkButton: Default.aspx?ID=9579
  - ID 9575 in page 5bc... (/Header) / Fields.CtaHref: Default.aspx?ID=9575
Fix the source baseline: include the referenced pages in a predicate path, or remove the references.
```

`BaselineLinkSweeper` found `Default.aspx?ID=N` references pointing at
pages that aren't in the baseline. Three resolutions:

1. **Extend the Content predicate path** to include the missing pages.
   If the baseline should include the full area, use `"path": "/"`.
2. **Clear the orphan reference at the source** before re-serializing.
   For known-broken Swift 2.2 source data, see
   `tools/swift22-cleanup/01-null-orphan-page-refs.sql`.
3. **Acknowledge the orphan** if the source can't be cleaned in time:
   add the page ID to the predicate's `acknowledgedOrphanPageIds` list.
   This downgrades the fatal error to a warning. Remove the
   acknowledgement once the data is clean — leaving it silences real
   future drift. See [`link-resolution.md`](link-resolution.md#acknowledged-orphan-ids).

### Serialize fails with no predicates configured

```
No Replace predicates configured
```

No predicate carries the requested mode. Either you're running
`?mode=merge` when every predicate is `"mode": "Replace"`, or the predicate list
didn't save correctly through the admin UI. Confirm in
`Files/System/Serializer/Serializer.config.json`:

```json
"predicates": [
  { "name": "...", "mode": "Replace", ... }  // at least one with the requested mode
]
```

## Deserialize-time warnings (under strict mode)

Every message below flows through the log wrapper and is escalated by
`StrictModeEscalator` under strict mode. See [`strict-mode.md`](strict-mode.md)
for the escalation mechanics.

### Unresolvable page ID N in link

A page referenced via `Default.aspx?ID=N` is in the baseline YAML, but
its target-environment page ID couldn't be resolved. Two possible causes:

- **The source page wasn't included in the Replace mode baseline.** The
  page is in the Merge mode or excluded entirely. Move the referencing
  page (or the referenced page) so they're both in the same mode.
- **The source page has no `SourcePageId` or no `PageUniqueId`.** Older
  baseline YAML may lack `SourcePageId`. Re-serialize from a current
  serializer version.

Check the source for the referencing item:

```sql
SELECT * FROM [ItemType_Swift-v2_Logo]
WHERE Link LIKE '%Default.aspx?%=NNN%';
```

### Could not load PropertyItem for page {GUID}

The page's `PagePropertyItemId` column is set but `PagePropertyItemType`
is empty or NULL. DW's PropertyItem loader can't construct the
item without a type name.

For the Swift 2.2 reference case, the fix is
`tools/swift22-cleanup/09-fix-misconfigured-property-pages.sql`, which
nulls the dangling `PagePropertyItemId` on 10 known-misconfigured
Page rows.

For general cases, investigate:

```sql
SELECT PageID, PageMenuText, PagePropertyItemId, PagePropertyItemType
FROM [Page]
WHERE PagePropertyItemId IS NOT NULL
  AND PagePropertyItemId <> ''
  AND (PagePropertyItemType IS NULL OR PagePropertyItemType = '');
```

Decide whether the page should have a property item (set
`PagePropertyItemType` to a valid type) or whether the reference is
stale (null the `PagePropertyItemId`).

### source column [T].[C] not present on target schema — skipping

`TargetSchemaCache` found a column in the source that doesn't exist on
the target. The row's other columns are still written; the missing
column is silently dropped.

Most commonly caused by DW NuGet version drift between source and
target hosts. Align versions:

1. Check the source host's `Dynamicweb.Suite` version.
2. Bump the target to match, or vice versa.
3. `dotnet publish` and restart. DW runs any pending `UpdateProvider`
   classes at startup, which reconciles the schema.

For Swift 2.2 specifically, the reference fix
is `tools/swift22-cleanup/cleandb-align-schema.sql` (10 idempotent
`ALTER TABLE` statements).

### template 'T' not found at Files/Templates/T

The YAML references a page layout, grid-row, or item-type template
that isn't on the target's filesystem. Templates are not in the
baseline YAML by design — they ship as filesystem content
(typically via a `git clone` of the Swift design system).

Confirm:

```bash
ls Files/Templates/Designs/Swift/<Template>.cshtml
ls Files/System/ItemTypes/<ItemType>.xml
```

Grid-row definitions live in a per-grid-type registry under the design
package. Page grids use `Grid/Page/RowDefinitions/`; email pages use
`Grid/Email/RowDefinitions/`. The serialized grid row records only its
`definitionId`, so validation probes the page registry first and then
every other `Grid/<registry>/RowDefinitions/` folder in every deployed
design:

```bash
ls Files/Templates/Designs/*/Grid/*/RowDefinitions/<DefinitionId>.json
```

If the template is missing, deploy it. If it should not exist
(legitimate stale reference), clean the source:
`tools/swift22-cleanup/05-null-stale-template-refs.sql` is the
reference fix for the known-stale Swift 2.2 template
(`Swift-v2_PageNoLayout.cshtml`). Note that the email row definitions
(`1ColumnEmail`, `2ColumnsEmail`) are **not** stale — they are
legitimate email-grid rows that older engine builds mis-reported
because only `Grid/Page/RowDefinitions/` was consulted (engine issue #4).

### Could not re-enable FK constraints for [T]

```
WARNING: Could not re-enable FK constraints for [EcomShopGroupRelation]:
  The ALTER TABLE statement conflicted with the FOREIGN KEY constraint
  "DW_FK_EcomShopGroupRelation_EcomShops"
```

After SqlTable deserialize, the writer re-enables FK constraints
(`ALTER TABLE ... WITH CHECK CHECK CONSTRAINT ALL`). The validation
fails because the table contains rows that reference a parent row
missing from the target.

For the Swift 2.2 reference case, the fix is
`tools/swift22-cleanup/06-delete-orphan-ecomshopgrouprelation.sql`
(deletes one orphan row in `EcomShopGroupRelation` that references a
non-existent shop).

For general cases, find the orphan rows:

```sql
-- Example pattern — substitute the actual FK columns and tables
SELECT r.*
FROM [EcomShopGroupRelation] r
WHERE NOT EXISTS (
  SELECT 1 FROM [EcomShops] s WHERE s.ShopId = r.ShopGroupShopId
);
```

Clean the source and re-serialize, or exclude the offending rows via a
`where` clause on the predicate.

### Cache invalidation failed for predicate 'X'

The `serviceCaches` entry resolved, but the `ClearCache()` call threw.
Most common cause: the DW service isn't healthy (DI container
misconfigured, underlying DB connection dropped). Check the DW host
log for the underlying exception.

Under lenient mode, deserialize continues — target data is written
correctly, but the in-process cache is stale until the next TTL
expiry or host restart. Under strict mode, the warning escalates.

## Deploy-time operational issues

### Management API returns 404

The DLL isn't loaded. Confirm:

```bash
ls /path/to/dw-host/bin/Truvio.Commerce.Serializer.dll
```

If missing, build and redeploy. If present, check the DW host log for
load errors — mismatched DW NuGet version is the usual cause.

### Management API returns 401

Bearer token is wrong or expired. Create a new token in
`Settings > Integration > API management` and update the secret.
Token format is `CLD.<random-chars>`.

### SerializeRoot directory not found

```
Mode subfolder not found: /path/to/Files/System/Serializer/SerializeRoot/replace
```

The YAML wasn't copied to the target. Your deploy pipeline's YAML-sync
step didn't land. Check the pipeline logs; confirm Azure Files share /
rsync / storage-sync permissions; manually copy the directory to
confirm the baseline applies.

### YAML files deployed but no change on target

Most likely: the predicate list is empty for the invoked mode, or the
YAML was copied to the wrong subfolder. Confirm:

```bash
# YAML lives in the mode's outputSubfolder
ls Files/System/Serializer/SerializeRoot/replace/
ls Files/System/Serializer/SerializeRoot/merge/
```

Both directories should contain YAML if both modes ran. If `replace/`
has files but `merge/` is empty, and you only POST `?mode=merge`, the
call reports `0 rows`.

## Round-trip fidelity issues

### Serialized baseline on source differs from target after deserialize

Run serialize on both hosts after deserialize, then diff. Any difference
is a fidelity bug in one of these classes:

- **Excluded column on source** but not on target (or vice versa).
  Confirm `excludeFields` is identical in both hosts' config.
- **`compareColumns` skip** evaluated unchanged on target when it should
  have written. Usually caused by `compareColumns` not covering a
  column that did change.
- **XML pretty-printing whitespace.** Some DW XML consumers add
  whitespace in write paths. If the diff is pure whitespace inside an
  XML column, either add the surrounding element to
  `excludeXmlElements` or accept the noise.

### A button (ButtonData) field did not change on target

The ButtonData editor (`Dynamicweb.Content.Items.Editors.ButtonEditor`)
round-trips its value out as a JSON **string**, but binds it back only as
an **object**. Writing the read-back string — or an empty string to clear
the button — fails model binding and the save **silently no-ops**: no
error, the prior target value stays.

The engine therefore reshapes every ButtonData-typed field before the
write (`ButtonDataNormalizer`):

- the round-tripped JSON string becomes the object it describes,
- a cleared field (null / empty — the shape source-wins produces for a
  field the payload does not assert) becomes a blank-membered object
  `{SelectedValue:"", Label:"", Link:"", LinkType:"page", Style:"primary"}`,
  which is what actually clears the button.

Which fields are ButtonData-typed comes from item-type metadata
(`ButtonDataFieldLookup`). If that lookup cannot read the item type, the
log carries:

```
Could not read field metadata for item type 'X' (...) — ButtonData fields are written unreshaped.
```

and the pre-fix string write happens — i.e. the button may again no-op.
Confirm the item type's XML is deployed to `Files/System/Items/` and that
the replace pass ran before the content that references it.

A ButtonData value the normalizer cannot parse (not JSON, a JSON array)
is written untouched and logged:

```
ButtonData field 'FirstButton' holds an unrecognised value shape — written as-is (it may not bind on the target).
```

Fix the source value rather than the target.

### EcomProducts count dropped during serialize

If source has 2051 rows and YAML has 582, you're hitting the
`FlatFileStore` monotonic-counter dedup path for rows whose
`nameColumn` collides. Two rows with the same `ProductName` would
otherwise overwrite each other's YAML file.

The serializer writes per-row monotonic counters; confirm the DLL
is current (`grep -r "FlatFileStore" tests/`). If the issue persists,
check for duplicate `nameColumn` values:

```sql
SELECT ProductName, COUNT(*) FROM EcomProducts GROUP BY ProductName HAVING COUNT(*) > 1;
```

Fixes: pick a different `nameColumn`, or use the composite-key file
naming (remove `nameColumn` entirely).

## When to use the cleanup scripts

`tools/swift22-cleanup/` contains SQL scripts that clean data-hygiene
issues in the shipped Swift 2.2 reference install. They're not part of
the serializer itself; they're operational tooling for the one
baseline the reference config targets.

Run them when:

- You're bootstrapping a fresh Swift 2.2 → CleanDB baseline round-trip
  from a bacpac.
- Your strict-mode deserialize is escalating warnings that trace to
  known-bad source data (orphan page IDs, stale template names,
  misconfigured property pages).
- You want to validate the reference config end-to-end via
  `tools/e2e/full-clean-roundtrip.ps1`.

Don't run them against an arbitrary customer DB without reading each
script's `@before` / `@after` assertions. They are targeted at the
Swift 2.2 reference install — some assertions (e.g. "exactly 1 orphan
row") may fail loud against other databases. See
[`tools/swift22-cleanup/README.md`](../tools/swift22-cleanup/README.md)
for the complete inventory with per-script detect / fix / rollback
recipes.

## See also

- [Strict mode](strict-mode.md) — the escalation mechanics
- [Link resolution](link-resolution.md) — `Unresolvable page ID` deep dive
- [SQL tables](sql-tables.md) — `WHERE` clause validation rules
- [Runtime exclusions](runtime-exclusions.md) — credential-leak checks
- [`tools/swift22-cleanup/README.md`](../tools/swift22-cleanup/README.md) — operational cleanup inventory
- [`tools/e2e/README.md`](../tools/e2e/README.md) — reference round-trip harness
