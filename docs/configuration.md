# Configuration

The full reference for `Serializer.config.json` and the admin UI screens that
edit it. Use this page as a lookup when you're composing a new baseline or
debugging a config-load failure.

## Table of contents

- [Where the config lives](#where-the-config-lives)
- [Top-level config schema](#top-level-config-schema)
- [Per-predicate mode](#per-predicate-mode)
- [Document ownership header](#document-ownership-header)
- [Content predicate fields](#content-predicate-fields)
- [SqlTable predicate fields](#sqltable-predicate-fields)
- [Inline scope (API)](#inline-scope-api)
- [Global exclusion maps](#global-exclusion-maps)
- [Admin UI screens](#admin-ui-screens)
- [Full config example](#full-config-example)
- [Config validation at load time](#config-validation-at-load-time)

## Where the config lives

The canonical config path is:

```
{DW_host}/Files/System/Serializer/Serializer.config.json
```

The config lives inside the serializer folder so the folder travels as one
unit — config plus YAML, Upload and Download. Copy it between environments, or
upload an example config (such as the Swift starter) into the folder through
the file manager to start from it.

The location is convention-fixed relative to the Files root. It is never
derived from the config's own `outputDirectory` value — that would be circular
(the file would define where to find itself). `outputDirectory` only governs
where the data subfolders are created.

The admin UI at `Settings > Developer > Serialize` reads and writes this file.
Manual edits are picked up on the next screen load (no restart required). The
Management API commands (`Serialize`, `Deserialize`, `PackageDownload`, `PackageUnzip`, and,
through the beta, the deprecated aliases `SerializerSerialize` and
`SerializerDeserialize`) also read the same file on each call.

## Top-level config schema

The config is a single flat `predicates: [...]` list where each predicate carries its own
`mode`. Section-level `replace: { ... }` / `merge: { ... }` keys are rejected by ConfigLoader
with a clear actionable error.

```json
{
  "outputDirectory": "Serializer",
  "replaceOutputSubfolder": "replace",
  "mergeOutputSubfolder": "merge",
  "showMergeIndicators": false,
  "showReplaceIndicators": true,
  "excludeFieldsByItemType": {
    "Swift_Content": ["SystemName_Internal"]
  },
  "excludeXmlElementsByType": {
    "eCom_CartV2": ["Mail1Recipient", "DefaultPaymentId"]
  },
  "predicates": [
    { "name": "...", "mode": "Replace", "providerType": "Content", "areaId": 3, "path": "/" },
    { "name": "...", "mode": "Merge", "providerType": "SqlTable", "table": "EcomGroups" }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `outputDirectory` | string (required) | Top-level folder relative to `Files/System`. Subfolders `SerializeRoot/`, `Upload/`, `Download/`, `Log/` are created automatically. |
| `replaceOutputSubfolder` | string | Subfolder under `SerializeRoot/` for Replace-mode YAML output. Default: `replace`. Validated against a safe-name regex to prevent path traversal. |
| `mergeOutputSubfolder` | string | Subfolder under `SerializeRoot/` for Merge-mode YAML output. Default: `merge`. Same regex check. |
| `showMergeIndicators` | boolean | Show merge cues in the admin UI: the flower icon on content-tree pages covered by a merge predicate and the merge info message on content editing screens. Default: `false` — with broad merge coverage these would appear nearly everywhere and drown out the replace warnings. |
| `showReplaceIndicators` | boolean | Show replace cues in the admin UI: the sync icon on content-tree pages covered by a replace predicate, the replace warning on content editing screens, and the replace warning on commerce settings screens (payment methods, currencies, …) backed by a replace-managed SqlTable predicate. Default: `true` — these warn editors that changes are overwritten by the next replace run. Switch off where the warnings are noise, e.g. on the source environment itself. |
| `excludeFieldsByItemType` | map | Global per-item-type field exclusions, applied to every predicate regardless of mode. Key: item-type system name. Value: list of field names to strip. |
| `excludeXmlElementsByType` | map | Global per-XML-type element exclusions, applied to every predicate regardless of mode. Key: XML type name (paragraph module system name or URL provider type). Value: list of element names to strip. |
| `predicates` | list | The predicates serialized and deserialized. Each entry must carry its own `mode` (Replace or Merge). The orchestrator filters on `predicate.Mode` when iterating per mode. |

## Per-predicate mode

Every predicate must declare a `mode` value of `Replace` or `Merge` (case-insensitive on disk).

| Mode | Conflict strategy | When to use |
|------|-------------------|-------------|
| `Replace` | source-wins (YAML overwrites target on every replace run) | Reference data and structural items: countries, currencies, shop definitions, payment methods, page templates, item-type schemas. |
| `Merge` | destination-wins via field-level fill | One-time bootstrap content the customer is expected to edit: product catalog, marketing copy, FAQ body text, newsletter templates. The serializer fills fields the target has NOT set, preserving customer edits. |

The conflict strategy is hardcoded per mode and is not a config knob.
[`MergePredicate`](../src/Truvio.Commerce.Serializer/Infrastructure/MergePredicate.cs) and
[`XmlMergeHelper`](../src/Truvio.Commerce.Serializer/Infrastructure/XmlMergeHelper.cs) implement
the Merge-mode field-level fill.

```json
{
  "name": "EcomCountries",
  "mode": "Replace",
  "providerType": "SqlTable",
  "table": "EcomCountries"
}
```

```json
{
  "name": "EcomProducts",
  "mode": "Merge",
  "providerType": "SqlTable",
  "table": "EcomProducts",
  "nameColumn": "ProductName"
}
```

The predicate's mode is also written into every document it serializes, under
the `ownership` key (see [Document ownership header](#document-ownership-header)
below). The configured predicates remain the saved defaults for that mode, and
they are also the safety fence that bounds any inline scope passed on a
`Serialize` or `Deserialize` call (see [Inline scope (API)](#inline-scope-api)).

## Document ownership header

Every serialized document starts with a header under the reserved key `ownership`:

```yaml
ownership:
  mode: merge
```

It is written into `area.yml`, `page.yml`, `grid-row.yml`, `paragraph-*.yml`,
SqlTable `_meta.yml`, and every SqlTable row file. The value is the mode of the
predicate that serialized the document, `replace` or `merge`.

Deserialize honors the document's own mode: `replace` means source-wins,
`merge` means destination-wins field fill. This applies per page, per grid
row, per paragraph (for permissions), and per SqlTable row.

A document without an `ownership` header, serialized by a 0.9.x release, runs
with the mode of the pass reading it, which is the mode the config predicate
gave the files when they were serialized. The config predicate is therefore
the fallback for documents that predate the header. Hand-editing a header
changes ownership of that one document, independent of the config.

Compatibility: re-serialize with 1.0.0-beta to add the header to existing
YAML. A 0.9.x serializer reading 1.0.0-beta SqlTable row files sees
`ownership` as an unknown column, a schema-drift warning that strict mode
turns into a failure. Upgrade the app on every environment together.

## Content predicate fields

```json
{
  "name": "Content - Swift 2",
  "providerType": "Content",
  "areaId": 3,
  "path": "/Customer Center",
  "excludes": ["/Customer Center/Drafts"],
  "excludeFields": ["AreaDomain", "GoogleTagManagerID"],
  "excludeXmlElements": ["EmptyCartRedirectPage"],
  "excludeAreaColumns": ["AreaCdnHost", "AreaCookieWarningTemplate"],
  "acknowledgedOrphanPageIds": []
}
```

| Field | Type | Description |
|-------|------|-------------|
| `name` | string (required) | Unique human-readable name. Shows in logs and admin UI. |
| `providerType` | `"Content"` | Routes to `ContentProvider`. |
| `areaId` | int (required) | DW area ID containing the content tree. Must exist on source. |
| `path` | string | Root path for the predicate. `/` includes everything under the area. Case-insensitive. |
| `pageId` | int | Optional page ID hint for the content-tree picker in the admin UI. |
| `excludes` | list of strings | Paths to exclude. Case-insensitive, with path-boundary matching so `/Home` does not exclude `/HomePage`. |
| `excludeFields` | list of strings | Item-type field names to strip from serialization. Applies to all items touched by the predicate. |
| `excludeXmlElements` | list of strings | XML element names to strip from embedded XML columns. Useful for masking env-specific page-ID references inside item-type XML payloads. |
| `excludeAreaColumns` | list of strings | Columns on the `[Area]` SQL table to strip from area metadata. Populated by the admin UI from the live schema. |
| `acknowledgedOrphanPageIds` | list of ints | Page IDs whose unresolvable references are logged as warnings rather than fatal errors by `BaselineLinkSweeper`. Escape hatch for known-broken source data that can't be cleaned upstream in time. |

## SqlTable predicate fields

```json
{
  "name": "AccessUser-Roles",
  "providerType": "SqlTable",
  "table": "AccessUser",
  "nameColumn": "AccessUserUserName",
  "compareColumns": "AccessUserUserName,AccessUserType",
  "where": "AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')",
  "excludeFields": ["AccessUserPassword", "AccessUserPasswordSalt"],
  "includeFields": [],
  "xmlColumns": [],
  "excludeXmlElements": [],
  "serviceCaches": ["Dynamicweb.Ecommerce.Users.UserService"],
  "resolveLinksInColumns": []
}
```

| Field | Type | Description |
|-------|------|-------------|
| `name` | string (required) | Unique human-readable name. |
| `providerType` | `"SqlTable"` | Routes to `SqlTableProvider`. |
| `table` | string (required) | SQL table name. Validated against `INFORMATION_SCHEMA.TABLES` at config-load. |
| `nameColumn` | string | Column used as the natural key for per-row file naming. If absent, the composite primary key is used. Validated against `INFORMATION_SCHEMA.COLUMNS`. |
| `compareColumns` | string | Comma-separated columns used for change detection. Rows whose `compareColumns` match on target are skipped. Empty: compare all non-identity columns. |
| `where` | string | Optional row filter applied at serialize time. Every identifier must match `INFORMATION_SCHEMA.COLUMNS` of `table`. Banned tokens (`;`, `--`, `/*`, `xp_`, `sp_executesql`) and DDL/DML keywords are rejected. See [`sql-tables.md`](sql-tables.md). |
| `excludeFields` | list of strings | Columns to strip from serialization. Validated against `INFORMATION_SCHEMA.COLUMNS`. |
| `includeFields` | list of strings | Columns to KEEP in output even if they would otherwise be auto-excluded by `RuntimeExcludes`. See [`runtime-exclusions.md`](runtime-exclusions.md). |
| `xmlColumns` | list of strings | Columns containing embedded XML. Pretty-printed in YAML output for readable diffs. |
| `excludeXmlElements` | list of strings | XML element names to strip from every `xmlColumns` column. |
| `serviceCaches` | list of strings | DW service cache types to clear after deserialization. Accepts short name (`CountryService`) or full type name (`Dynamicweb.Ecommerce.International.CountryService`). Validated at config-load against `DwCacheServiceRegistry`. |
| `resolveLinksInColumns` | list of strings | Columns whose `Default.aspx?ID=N` strings should be rewritten source → target at deserialize. An integer column in the list is a page id: it resolves through the same map, binds by the page's `PageUniqueId` when the row carries one (`pageRefs`), and keeps its type. Validated against `INFORMATION_SCHEMA.COLUMNS`. See [`sql-tables.md`](sql-tables.md#integer-page-id-columns) and [`link-resolution.md`](link-resolution.md). |
| `schemaSync` | string | Optional schema-sync directive. `EcomGroupFields` is the only recognized value; runs `EcomGroupFieldSchemaSync` before row writes. |
| `keyColumns` | list of strings | Optional explicit match key for a table with no PRIMARY KEY. Beats unique-index inference and the all-columns fallback; ignored on a keyed table. See [`sql-tables.md`](sql-tables.md#tables-without-a-primary-key). |
| `replaceStrategy` | string | Optional. `truncate` deletes every target row before the payload is written, under Replace only, and is ignored with a WARNING under Merge. Absent (the default) means Replace upserts and preserves target rows the payload does not carry. |

## Inline scope (API)

`Serialize` and `Deserialize` accept an optional inline scope on each call: a
JSON body property `Scope`, or a query parameter `?scope=` holding the same
JSON. A scope narrows a single call to one subtree or table without editing
the config. It is predicate-shaped: it uses the same keys as a config
predicate.

```json
{"Mode":"replace","Scope":{"areaId":3,"path":"/Customer Center"}}
```

```json
{"Mode":"merge","Scope":{"table":"EcomProducts","where":"ProductActive = 1"}}
```

### Scope keys

| Key | Applies to | Notes |
|-----|-----------|-------|
| `name` | both | Optional. Names a configured predicate directly; that predicate becomes the fence. |
| `providerType` | both | `Content` or `SqlTable`. Defaults to `SqlTable` when `table` is set, otherwise `Content`. |
| `areaId`, `path` | Content | Same meaning as the matching content predicate field. |
| `pageId` | Content | Alternative to `path`: the scope root page, resolved on the host. A language-layer page resolves to its master page and master area. |
| `excludes`, `includeLanguageLayers`, `excludeAreaColumns` | Content | Same meaning as the matching content predicate field. |
| `table`, `where` | SqlTable | Same meaning as the matching SqlTable predicate field. |
| `includeFields` | SqlTable | Must be a subset of the fence's `includeFields`. |
| `excludeFields`, `excludeXmlElements` | both | Unioned with the fence's own exclusions, never replace them. |
| `nameColumn`, `compareColumns`, `xmlColumns`, `serviceCaches`, `schemaSync`, `resolveLinksInColumns`, `acknowledgedOrphanPageIds` | both | Owned by the configured predicate. Omit these, or pass exactly the configured value; any other value is rejected. |

### The boundary check (the fence)

A scope must fall inside a configured predicate of the same mode as the call.
The configured predicates remain the saved defaults and are also the safety
fence for every inline scope.

- **Content.** The scope must target the same area, and its path must be
  included by a configured predicate's path and not fall under one of that
  predicate's excludes. The most specific covering predicate is the fence,
  or, when `scope.name` names a predicate, that predicate is the fence.
- **SqlTable.** The scope must target a table covered by a configured
  predicate of the same mode. `scope.name` picks a specific predicate when
  several cover the same table.

A scope that falls outside the config, or that is owned by a predicate of the
other mode, is rejected with HTTP `Invalid`. The message names the covering
predicate and, when the other mode covers the scope, suggests calling that
mode instead.

### The scope can only narrow

- `excludes`, `excludeFields`, `excludeXmlElements`, and `excludeAreaColumns`
  are unioned with the fence's own values, never replace them. Fence excludes
  that fall under the scope path still apply.
- A SqlTable `where` is ANDed with the fence's `where`, as `(fence) AND (scope)`.
- `includeFields` must be a subset of the fence's `includeFields`.
- `includeLanguageLayers` cannot be switched on in the scope when the fence
  has it off.
- Every key the scope omits comes from the fence. The config predicates
  remain the saved defaults.
- Scope excludes must lie below the scope path.

SqlTable identifiers and the combined WHERE clause pass the same
`INFORMATION_SCHEMA` identifier whitelist and WHERE validator as config load
(see [Config validation at load time](#config-validation-at-load-time)).

On `Deserialize`, a SqlTable scope selects which of the table's
already-serialized rows to act on. `where`, `excludeFields`, `includeFields`,
and `excludeXmlElements` are serialize-time filters and are rejected on a
Deserialize scope.

### What a scoped call does

A scoped `Serialize` runs only the resolved predicate (language layers still
expand as usual), writes into the normal mode folder (`SerializeRoot/replace`
or `/merge`), and folds its entries into the existing `{mode}-manifest.json`
instead of replacing it: an existing entry with the same id, or one that
already covers the scope, gains the newly written files. A scoped serialize
does not run stale-file cleanup; the next full serialize of the mode prunes
stale files. It merges into `templates.manifest.yml` instead of shrinking it,
reuses existing page folders, and never overwrites a full `page.yml` of an
ancestor with a structural stub.

A scoped `Deserialize` reads the mode manifest and dispatches only the
entries inside the scope. An entry the scope only partly covers is narrowed
to the pages inside the scope; the pages above them run as structural stubs
(scalars only, no grid rows, no permissions). A scope that matches nothing in
the manifest fails with an error naming the scope and asking for a serialize
of that scope first. A scoped merge pass finalizes deferred page links but
not the cross-mode area item links of replace areas; the next full merge pass
does that.

See [Content predicate fields](#content-predicate-fields) and
[SqlTable predicate fields](#sqltable-predicate-fields) above for what each
key means.

## Global exclusion maps

Two dictionaries live at the top level of the config and apply across every predicate
regardless of mode. These live at the top level because
the same exclusions almost always apply to both Replace and Merge.

```json
{
  "excludeFieldsByItemType": {
    "Swift_Content": ["SystemName_Internal"],
    "Swift-v2_Button": ["DebugMarker"]
  },
  "excludeXmlElementsByType": {
    "ParagraphModule": ["cache"],
    "PageItem": ["EmptyCartRedirectPage", "ShoppingCartLink"]
  },
  "predicates": [
    { "name": "...", "mode": "Replace", "providerType": "Content", "areaId": 3, "path": "/" }
  ]
}
```

Use these for cross-predicate cleanup. Per-predicate exclusions still work;
the effective exclude set is the union of the predicate's list and the global
dictionary entry for that item type.

These maps are visible in four places in the admin UI: the settings screen
shows a per-type inventory of everything excluded; the Item Type Excludes and
Embedded XML Excludes sub-nodes edit them; content pages carrying an affected
type show as **partially managed** in the content tree (sync-slash icon) with
the excluded types named in the tooltip and a right-click "View excluded
fields" action; and the editing screens add a clickable header chip per
carved-out type ("eCom_CartV2 — 21 settings stay local — view") next to the
verdict alert. Both click-throughs open a read-only **"Stays local"** panel
listing the exact excluded fields — visible to every backend user, no Settings
access needed; administrators additionally get a "Manage exclusions" shortcut
into the editor. The cart page is the canonical case: covered by the replace
predicate, but its `eCom_CartV2` module settings (mail recipients, error
messages, default payment/shipping ids) stay local per environment.

## Admin UI screens

Navigation: `Settings > Developer > Serialize`.

| Node | Purpose |
|------|---------|
| **Serialize** | Top-level settings screen. Every top-level config value is visible here: output directory, replace/merge subfolders and the replace/merge indicator toggles are editable; the config file location, sync history (last replace/merge received), coverage counts, the two exclusion maps and the predicate list show as read-only summaries. Actions per mode: **Serialize (Replace/Merge)**, **Preview deserialize (Replace/Merge)** — the full pipeline without writing, per-field `[DRY-RUN]` detail in the Log Viewer — and **Deserialize (Replace/Merge)**. With no predicates configured the actions are replaced by a **Get started** group (apply the embedded Swift starter to a chosen website, or create an empty configuration). Per-mode conflict strategy is hardcoded — Replace=source-wins, Merge=destination-wins — and is not an admin-editable setting. |
| **Predicates** | CRUD for Content and SqlTable predicates. Each predicate carries its own `mode` field (Replace or Merge) — pick the mode on the predicate edit screen. Fields match the JSON schema above with dual-list pickers populated from the live DB schema. |
| **Item Types** | Browse item types by category, edit global per-type field exclusions (mode-agnostic). |
| **Embedded XML** | Browse XML types, edit global per-type element exclusions (mode-agnostic). |
| **Log Viewer** | Per-run logs with summary headers, per-predicate counts, and `AdviceGenerator` remediation hints. |

## Packages

Packages are Management API commands; there is no package UI in the admin.

**`PackageDownload`** builds a zip of a page subtree and returns it as a file
response; a copy lands in `Files/System/Serializer/Download/`. It does not
touch `SerializeRoot`.

```
POST /Admin/Api/PackageDownload {"PageId":12,"AreaId":1,"Scope":"PageOnly","IncludeAssets":false}
```

`Scope` is `PageAndSubpages` (default), `PageOnly` or `SubpagesOnly`.
`IncludeAssets` bundles the files the content references under `_assets/`.

**`PackageUnzip`** unzips a zip that is already on the host into
`SerializeRoot/{mode}/`, replacing that folder, so the next `Deserialize` of the
mode applies it. It does no upload: put the zip on the host with the standard
file upload (`POST /Admin/Api/Upload`, into `/Files/System/Serializer/Upload/`).

```
POST /Admin/Api/PackageUnzip {"FilePath":"/Files/System/Serializer/Upload/layer.zip","Mode":"replace"}
POST /Admin/Api/Deserialize  {"Mode":"replace"}
```

| Parameter | Meaning |
|-----------|---------|
| `FilePath` | `/Files` path of the zip. A bare file name resolves to `/Files/System/Serializer/Upload/`. Must stay inside the Files folder. |
| `Mode` | `replace` (default) or `merge`: the mode folder to replace. |
| `AreaId` | Website id; required for a `PackageDownload` zip, ignored for a mode tree zip. |

Two zip shapes are accepted:

- **Mode tree**: the contents of a `SerializeRoot/{mode}/` folder, with
  `{mode}-manifest.json` at the zip root. Unzipped as is. The manifest name must
  match `Mode`.
- **Content package**: a `PackageDownload` zip (`<area>/area.yml` at the root,
  no manifest). Unzipped under `_content/`, with a whole-area manifest entry for
  `AreaId`. Bundled `_assets/` files are unzipped with the content but not
  restored into the Files archive; the response says how many.

Rejected with `Invalid`, before anything is written: an entry with an absolute
or drive-qualified path, a `..` segment or a `:`; a zip over 256 MB, over 1 GB
unzipped or over 100,000 files; a zip that is neither shape, a mode tree zip
without its manifest or wrapped in a folder, and a manifest for the other mode.
The zip is unzipped into a staging folder under `Files/System/Serializer/` and
swapped in only when complete, so a rejected or failed zip leaves the existing
mode folder as it was. The response reports the file count, bytes, shape and
target folder.

Permissions: `PackageDownload` needs the package download grant and Read on the
page; `PackageUnzip` needs the package upload grant. Both grants are open until
an admin manages them.

The commerce settings edit screens — payment, shipping, country, currency,
ecommerce language, shop, order flow and order state — show the same
replace/merge alert as the content editors when a SqlTable predicate manages
their table. A predicate with exclusions adds a clickable "Stays local" header
chip that opens the predicate editor.

## Full config example

The Swift 2.2 reference baseline — a working config with one Content predicate
and seventeen SqlTable predicates. Lives at
`src/Truvio.Commerce.Serializer/Configuration/swift2.2-combined.json`. Abbreviated:

```json
{
  "outputDirectory": "Serializer",
  "replaceOutputSubfolder": "replace",
  "mergeOutputSubfolder": "merge",
  "predicates": [
    {
      "name": "Content - Swift 2 (full baseline as shipped)",
      "mode": "Replace",
      "providerType": "Content",
      "areaId": 3,
      "path": "/",
      "excludes": [],
      "excludeFields": [
        "AreaDomain", "AreaDomainLock", "AreaNoindex",
        "AreaNofollow", "AreaRobotsTxt", "AreaRobotsTxtIncludeSitemap",
        "GoogleTagManagerID"
      ],
      "excludeXmlElements": ["EmptyCartRedirectPage", "ShoppingCartLink"],
      "excludeAreaColumns": ["AreaCdnHost", "AreaCookieWarningTemplate"]
    },
    {
      "name": "EcomVatGroups",
      "mode": "Replace",
      "providerType": "SqlTable",
      "table": "EcomVatGroups",
      "nameColumn": "VatGroupName",
      "serviceCaches": [
        "Dynamicweb.Ecommerce.International.VatGroupService"
      ]
    },
    {
      "name": "EcomPayments",
      "mode": "Replace",
      "providerType": "SqlTable",
      "table": "EcomPayments",
      "nameColumn": "PaymentName",
      "xmlColumns": [
        "PaymentGatewayParameters",
        "PaymentCheckoutParameters"
      ],
      "serviceCaches": [
        "Dynamicweb.Ecommerce.Orders.PaymentService"
      ]
    },
    {
      "name": "UrlPath",
      "mode": "Replace",
      "providerType": "SqlTable",
      "table": "UrlPath",
      "resolveLinksInColumns": ["UrlPathRedirect"]
    },
    {
      "name": "EcomProducts",
      "mode": "Merge",
      "providerType": "SqlTable",
      "table": "EcomProducts",
      "nameColumn": "ProductName"
    }
  ]
}
```

Open the full file to see every predicate the Swift 2.2 storefront needs. The
Replace list covers reference data (countries, currencies, languages, VAT),
shop structure, payment and shipping definitions, order flows, and URL
redirects. The Merge list covers product catalog content.

## The Deserialize response

`POST /Admin/Api/Deserialize` answers with a message line and a model.

The message counts **manifest entries only**: the synthetic run-level outcome
that strict-mode escalation appends is not an entry, and counting it reported
"across 10 entries" for a nine-entry manifest. Its `Errors:` tail carries the
run-level errors **and** each failed entry's own error strings, prefixed with
the entry id — those used to reach the log file only, so a run reporting
"1 failed" could answer with an empty list.

The model names the entries the run walked:

```json
{
  "mode": "merge",
  "dryRun": false,
  "entryCount": 9,
  "totalCreated": 194, "totalUpdated": 8, "totalSkipped": 0, "totalFailed": 0,
  "entries": [
    { "entryId": "sql/EcomGroups", "providerType": "SqlTable", "status": "Succeeded",
      "created": 12, "updated": 0, "skipped": 0, "failed": 0, "errors": [] }
  ],
  "errors": [],
  "quarantinedWarnings": []
}
```

`entryCount` equals `entries.length`, and both equal the number of entries in
the manifest that was deserialized. A composition of N entries that answers
with anything else is a manifest/engine disagreement, and `entries[]` says
which entries the engine actually walked.

## Config validation at load time

`ConfigLoader` enforces several checks before the first SQL statement runs:

- **JSON shape.** Required fields must be present. Mode subfolders must match
  a safe-name regex (no path traversal).
- **SQL identifiers.** Every `table`, `nameColumn`, `compareColumns` value,
  every name in `excludeFields`, `includeFields`, `xmlColumns`, and
  `resolveLinksInColumns`, and every identifier inside `where` clauses is
  validated against `INFORMATION_SCHEMA.TABLES` / `INFORMATION_SCHEMA.COLUMNS`.
  Mismatches fail at config-load with a message naming the predicate and field.
- **WHERE clause.** Tokens are whitelist-checked (`AND`, `OR`, `IN`, etc.);
  banned tokens (`;`, `--`, `/*`, `xp_`, `sp_executesql`) and DDL/DML keywords
  (`SELECT`, `UPDATE`, `DROP`, `EXEC`, …) are rejected. String literals are
  elided before tokenization so legitimate values like `'Admin Select Group'`
  pass.
- **Service caches.** Every `serviceCaches` entry must resolve through
  `DwCacheServiceRegistry`. Unknown names fail with a message listing the
  eighteen supported short and fully-qualified names. The same check runs
  again over the entries of the manifest a deserialize reads, before any row
  is written — see [`sql-tables.md`](sql-tables.md#servicecaches).
- **Top-level keys.** A key the loader does not read is named, not dropped.
  `deployOutputSubfolder` and `seedOutputSubfolder` were renamed to
  `replaceOutputSubfolder` / `mergeOutputSubfolder` in 0.9.0-beta and are a
  hard reject naming the replacement: a config still carrying one reads the
  built-in default (`replace` / `merge`) instead of the folder it names, which
  is why such a config appeared to work. Any other unrecognised top-level key
  produces a warning naming the key. Keys starting with `_` are the
  file-comment convention and are ignored.
- **Acknowledged orphans.** `acknowledgedOrphanPageIds` values are
  range-checked to reject malicious inputs.

When any of these fail, the error message names the predicate by `name` and
the offending field. Config-load errors surface as HTTP `Invalid` on the
Management API. No SQL runs until the config is clean.

## See also

- [Getting started](getting-started.md) — minimal working config
- [Glossary](glossary.md) — baseline, predicate, replace/merge, drift, dry run
- [Concepts](concepts.md) — predicate semantics, Replace/Merge modes
- [SQL tables](sql-tables.md) — `WHERE` clauses, field filters, credentials
- [Strict mode](strict-mode.md) — warning escalation and entry-point defaults
- [Runtime exclusions](runtime-exclusions.md) — what's auto-excluded and why
