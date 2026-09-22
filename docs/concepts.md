# Concepts

The mental model for Truvio.Commerce.Serializer. Read this once before configuring a
real baseline — most "why isn't it doing X" questions resolve to one of these
concepts.

## Table of contents

- [Predicates select what to sync](#predicates-select-what-to-sync)
- [Identity is GUID-based, not numeric](#identity-is-guid-based-not-numeric)
- [The three-bucket split: Replace, Merge, Not-Serialized](#the-three-bucket-split-replace-merge-not-serialized)
- [Replace and Merge modes](#replace-and-merge-modes)
- [Folder layout](#folder-layout)
- [The serialize flow](#the-serialize-flow)
- [The deserialize flow](#the-deserialize-flow)
- [What lives in YAML vs what doesn't](#what-lives-in-yaml-vs-what-doesnt)

## Predicates select what to sync

A predicate is a config entry that names a slice of the database to include in
the baseline. There are two built-in provider types.

**Content predicates** target the DW content hierarchy (Area → Page → GridRow →
Column → Paragraph). They take an area ID, a root path, and optional exclude
paths and field/column exclusions:

```json
{
  "name": "Content - Swift 2",
  "providerType": "Content",
  "areaId": 3,
  "path": "/",
  "excludes": ["/Home", "/Posts"],
  "excludeFields": ["AreaDomain", "GoogleTagManagerID"]
}
```

**SqlTable predicates** target arbitrary SQL tables. They take a table name and
optional `nameColumn`, `compareColumns`, `where`, and field filters:

```json
{
  "name": "EcomVatGroups",
  "providerType": "SqlTable",
  "table": "EcomVatGroups",
  "nameColumn": "VatGroupName",
  "serviceCaches": [
    "Dynamicweb.Ecommerce.International.VatGroupService"
  ]
}
```

Predicates with OR semantics: if any predicate includes a page or a row, it is
serialized. Exclude rules inside a predicate beat its include rule. See
[`configuration.md`](configuration.md) for every field and
[`sql-tables.md`](sql-tables.md) for the SqlTable-specific surface.

## Identity is GUID-based, not numeric

Every DW page has two keys: `PageID` (numeric, environment-specific) and
`PageUniqueId` (GUID, stable across environments). The serializer uses GUIDs
as the canonical identity.

Serialize writes the `PageUniqueId` into every YAML file along with the numeric
`SourcePageId` from the source environment. Deserialize reads the YAML, looks
up the target `PageID` by GUID in `PageGuidCache`, and creates the row if the
GUID is missing or updates it if the GUID already exists. Numeric IDs diverge
freely; the GUID keeps the two environments aligned.

The same applies to paragraphs (`ParagraphUniqueId`) and — for SqlTable
predicates — to rows identified by `nameColumn` or by composite primary key.

## The three-bucket split: Replace, Merge, Not-Serialized

Every piece of DW state belongs to one of three buckets. Ready-made,
gate-proven applications of this split ship as layers and editions in the
[Truvio.Commerce.Distribution](https://github.com/justdynamics/Truvio.Commerce.Distribution).

| Bucket | Owned by | Captured in | Example contents |
|--------|----------|-------------|------------------|
| **REPLACE** | Developer / template | YAML, Replace mode | Shop structure, item types, VAT rates, country list, payment method definitions |
| **MERGE** | Developer initially, end-user thereafter | YAML, Merge mode | Customer Center welcome copy, FAQ body text, newsletter templates |
| **NOT-SERIALIZED** | Per-env operator | Filesystem config, Azure Key Vault, per-env Area fields | `GlobalSettings.config`, payment gateway credentials, `AreaDomain`, GTM IDs |

Replace data must be identical across environments. Merge data is a
bootstrap — a fresh install gets the values, and subsequent edits by end users
must not be overwritten on the next replace run. Not-serialized data is owned by
the target host's operator and never lives in YAML.

Getting the bucket wrong is the main source of "my replace run overwrote customer
edits" or "my credentials leaked into git" incidents. The Distribution's
editions enumerate exactly what belongs in each bucket for a typical Swift
install.

## Replace and Merge modes

The two buckets map to two modes the serializer supports natively.

**Replace mode** is source-wins. Re-running deserialize overwrites whatever the
target has. This is correct for pure structural data: developer-owned,
identical-across-envs, never customer-edited.

**Merge mode** is destination-wins, field-level fill. Rows whose natural key or
`PageUniqueId` is already present on target are preserved, not overwritten. This
is safe for first-run content that transitions to customer ownership after the
initial install.

Each mode has its own predicate list, its own exclusion maps, and its own
output subfolder:

```
Files/System/Serializer/SerializeRoot/
  replace/             <- source-wins
  merge/               <- destination-wins, field-level fill
```

The Management API and CLI both accept `?mode=replace` (default) and
`?mode=merge`. Most pipelines run them as two sequential steps: replace first
(structural data), merge second (first-run content).

## Folder layout

For a config with `outputDirectory: "Serializer"`, the full layout on the DW
host's filesystem is:

```
Files/
  Serializer.config.json             <- config source of truth

  System/Serializer/                 <- root set by outputDirectory
    SerializeRoot/
      replace/                       <- Replace mode output
        Content predicate files live in area-mirror form:
        Swift 2/
          area.yml
          Customer Center/
            page.yml
            paragraph-p0.yml           <- paragraph placed on the page itself
            grid-row-1/
              grid-row.yml
              paragraph-c1-1.yml
              paragraph-c1-2.yml
          ...
        SqlTable predicate files sit one-per-row:
        EcomOrderFlow/
          Default.yml
          Quote.yml
      merge/                         <- Merge mode output
        (same shape, different predicates)
    Upload/                          <- zips uploaded for PackageUnzip
    Download/                        <- PackageDownload zips
    Log/                             <- per-run logs
```

Content predicates produce a mirror tree: the folder hierarchy under `SerializeRoot/replace/`
matches the content tree in DW admin. SqlTable predicates produce a flat
directory per table, with one file per row named by `nameColumn` (or a composite
key derived from the primary key if `nameColumn` is unset).

A paragraph can sit inside a grid row or **directly on the page**
(`ParagraphGridRowId = 0`, rendered through `Model.Placeholder(...)` rather than
through the grid — stock Swift 2 builds its service pages that way). Grid-row
paragraphs are written inside their `grid-row-N/` folder as
`paragraph-c<column>-<sort>.yml`; page-level paragraphs are written beside
`page.yml` as `paragraph-p<sort>.yml` and carry the paragraph's `container`. Both
round-trip; a page-level paragraph is deserialized back with `GridRowId 0`, and
the count is logged on both sides so a drop is visible.

Every one of these files also starts with a small `ownership` header recording
the mode that wrote it (`replace` or `merge`). Deserialize honors that
per-document mode; the mode above is only the fallback for documents that
predate the header. See [Document ownership header](configuration.md#document-ownership-header).

## The serialize flow

1. `Serialize` reads `Files/System/Serializer/Serializer.config.json` and resolves the
   requested mode (Replace or Merge).
2. `SerializerOrchestrator` iterates the mode's predicates in order.
3. Content predicates: `ContentSerializer` walks the DW area → pages tree,
   applying exclude rules and field/column filters. Writes one YAML file per
   page, grid row, and paragraph.
4. SqlTable predicates: `SqlTableReader` issues a `SELECT` with the optional
   `WHERE` clause, applies `ExcludeFields`, auto-excludes runtime-only columns
   (see [`runtime-exclusions.md`](runtime-exclusions.md)), and writes one YAML
   file per row via `FlatFileStore`.
5. After all predicates complete, `BaselineLinkSweeper` walks the written YAML
   tree and validates every `Default.aspx?ID=N` and `"SelectedValue": "N"`
   reference against the set of serialized `SourcePageId` and
   `SourceParagraphId` values. Orphaned references cause serialize to fail
   with a multi-line breakdown (see [`link-resolution.md`](link-resolution.md)).
6. A template-asset manifest is written listing every template referenced in
   serialized content, so deserialize can validate their presence upfront.
7. Stale files (present in the manifest from a previous run but not written
   this run) are deleted.

## The deserialize flow

1. `Deserialize` reads the config and resolves the mode.
2. `StrictModeResolver` determines whether warnings escalate. Precedence:
   request parameter > `config.strictMode` > entry-point default (API/CLI on,
   admin UI off). See [`strict-mode.md`](strict-mode.md).
3. `TemplateAssetManifest` validates every template named in the baseline
   exists in `Files/Templates/`. Missing templates emit warnings (which
   escalate under strict mode).
4. Content predicates run first. `ContentDeserializer` reads the page tree,
   matches each page by `PageUniqueId` against the target's
   `PageGuidCache`, creates new pages or updates existing ones, and restores
   permissions, item-type fields, property fields, and grid-row layout.
   Internal `Default.aspx?ID=N` references in item-type string fields are
   rewritten via `InternalLinkResolver`.
5. `SerializerOrchestrator` builds the cumulative source → target page ID map
   from the Content predicates' cache writes.
6. SqlTable predicates run next. `SqlTableWriter` merges rows via parameterized
   MERGE statements, applying `resolveLinksInColumns` to rewrite
   `Default.aspx?ID=N` in opted-in columns using the map from step 5.
7. Foreign-key constraints are re-enabled after each SqlTable predicate
   completes. FK violations on re-enable surface as warnings (escalated in
   strict mode).
8. `CacheInvalidator` clears the DW service caches listed in each predicate's
   `serviceCaches` field (`CountryService`, `VatGroupService`, etc.) so the
   newly-written data is visible to the live host.
9. If strict mode was on and any warning was recorded, `StrictModeEscalator`
   throws `CumulativeStrictModeException` listing every warning verbatim.
   The Management API returns `Error` and the pipeline fails.

## What lives in YAML vs what doesn't

**In YAML (captured by the serializer):**

- Pages: all ~30 page-level properties (NavigationTag, ShortCut, UrlName,
  SEO meta, SSL mode, visibility, URL inheritance), item-type field values,
  property-field values (Icon, SubmenuType), explicit permissions.
- Grid rows: layout settings (top/bottom spacing, container width, visual
  properties), item-type XML content, explicit permissions.
- Paragraphs: content, item-type field values, column attribution, permissions.
- Areas: area metadata, area item-type fields (header/footer/master page
  connections).
- SqlTable rows: every non-excluded column, with XML columns pretty-printed
  for diff readability.
- Page navigation settings: `UseEcomGroups`, `ProductPage`, `MaxLevels`, etc.
- EcomProductGroupField custom column schema for `EcomGroups`.

**Not in YAML (out of scope by design):**

- Per-environment infrastructure: `AreaDomain`, `AreaCdnHost`, `GoogleTagManagerID`,
  `GlobalSettings.config`, `web.config`, Azure Key Vault secrets.
- Runtime-only columns: visit counters (`UrlPath.UrlPathVisitsCount`),
  search-index pointers (`EcomShops.ShopIndex*`). Auto-excluded, see
  [`runtime-exclusions.md`](runtime-exclusions.md).
- Files and media: images, documents, video uploads. Files already live in
  Git (or Azure Files, or a CDN) — the serializer is for DB state only.
- Payment gateway credentials: not auto-excluded; list them in a predicate's
  `excludeFields` (a curated credential registry is on the roadmap).

## See also

- [Configuration](configuration.md) — every config key and admin UI screen
- [SQL tables](sql-tables.md) — SqlTable predicate surface in depth
- [Link resolution](link-resolution.md) — the three passes of cross-env rewriting
- [Permissions](permissions.md) — role and group permission handling
