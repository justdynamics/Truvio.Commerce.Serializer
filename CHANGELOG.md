# Changelog

All notable changes to Truvio.Commerce.Serializer are recorded here. The
project follows semantic versioning, with `-beta` on every 1.0.x release while
the engine is shared with partners rather than fully productized.

Releases 1.0.0 through 1.0.2-beta shipped without release notes; this file
starts at 1.0.3-beta.

## 1.0.8-beta

### Fixed

- **An area item type must exist before it is used on an area.** When the area's item
  type (`AreaItemType`) or page-property item type (`AreaItemTypePageProperty`) is not
  registered on the host, the whole-area Content entry, or an entry that creates the area,
  fails before any area write, whatever strict mode says. The message names the item type,
  the area and the `ItemType_*.xml` file to deliver and recycle (#42).
- **Creating the area item no longer reverts the area properties.** A Replace whose
  whole-area Content entry targeted an existing area without an area item wrote the area
  properties, then saved the area object read before that write, so `AreaCulture`,
  `AreaItemTypePageProperty` and the rest fell back to the target's values and the entry
  reported success. The area item is now bound by writing only `AreaItemType` and
  `AreaItemId`. Pages created in that pass get their page-property item, and an existing
  page without one gets it on update (#42).
- **A Replace onto an existing area applies the area name from `area.yml`.** Replace is
  source-wins for the area row, so the whole-area Content entry writes `AreaName` with the
  area properties; on a blank DW10 database the delivered website was left named
  `Standard`. Under Merge the name only fills an area that has none. A solution that names
  its own website lists `AreaName` in the predicate's `excludeAreaColumns` (now pickable in
  the predicate editor) to keep the target's name. An area the run creates takes the YAML
  name as before (#43).

## 1.0.7-beta

### Added

- **Raise-only counters (`raiseOnlyColumns`).** A SqlTable predicate can list numeric
  columns whose shipped value only raises the target value and never lowers it
  (Foundry#1322). When a payload row's key matches a target row, each listed column is
  written as the larger of the target and shipped values, both in the engine's snapshot
  compare and in the SQL write itself, so a counter a live host advances during the
  deserialize is not put back. A shipped NULL never lowers or clears a value. A payload
  row with no target row inserts as shipped. A row whose only difference is a lower
  shipped counter is reported as skipped. Each entry logs one info line such as
  `[EcomNumbers] raiseOnlyColumns NumberCounter: 3 raised, 12 kept (target higher or equal)`.
  Config load rejects a listed column that is missing, not numeric, or a key column, and
  the field on a table without a primary key unless `keyColumns` is declared.
  `replaceStrategy: truncate` re-inserts from the snapshot, so use Merge for a counter
  table on a host taking orders.
  A layer ships its id counters with
  `{ "table": "EcomNumbers", "mode": "Merge", "raiseOnlyColumns": ["NumberCounter"] }`,
  so a blank DW10 database does not mint ids the layer already shipped, and a host
  whose counters are higher keeps them. An older engine ignores the field: a layer that
  declares it needs this engine as its floor.

### Fixed

- **An area's ecom language is checked after the whole run.** Replace ran every Content
  entry ahead of the SqlTable entries whenever one SqlTable entry resolves links, so on an
  empty schema an Area was validated before the same package's `sql/EcomLanguages` rows
  existed and strict mode failed on `Area 1 references ecom language 'ENU' which does not
  exist on target` (#35). The check now runs after every entry, and warns (strict: fails)
  only for a language still missing.
- **An `excludeFields` column the host lacks no longer invalidates the config.** A SqlTable
  predicate excluding a column the host schema does not have (a DW9-era column such as
  `EcomProducts.MyDouble` on a blank DW10 database) made every config load fail, and the
  admin Serializer screens answered HTTP 500 (#37). Config load and every serialize of the
  entry now log an info line naming the column and carry on. Every other column list keeps
  the strict check. A misspelled `excludeFields` name therefore no longer fails the load;
  the serialize run log names it.
- **Row identity is the key, never `nameColumn`.** A SqlTable entry with a `nameColumn`
  merged rows whose names repeat although their keys differ, so on a blank target the
  Distribution base's 18 `EcomOrderStates` wrote 14 and the entry reported success (#38).
  Matching, skip-on-unchanged, the Merge fill and the same-identity merge now use the
  table's match key; `nameColumn` names the file and nothing else. On an identity-PK table
  that sets a `nameColumn` (such as `EcomOrderFlow`), skip-on-unchanged and the Merge fill
  now read the target row with the same auto-id, the row the MERGE always wrote. A
  document that lacks a key column (a partial override that used to match its full row by
  name) now logs a `WARNING` naming the missing key column.

## 1.0.6-beta

### Fixed

- **An option-list field whose source yields a page or paragraph id is remapped again.**
  Since 1.0.3-beta (#15) a bare number was remapped only on fields whose editor is a link,
  page, paragraph or button editor. Swift 2's component selectors store a page id in a
  `RadioButtonListEditor` field over `<options sourceType="ItemType">` with
  `valueField="PageId"`, so `Swift-v2_ProductListComponentSelector.ComponentSource`,
  `Swift-v2_ProductComponentSelector.ComponentSource` and `Swift-v2_ProductBom.ListComponentSource`
  kept the source host's page id and the product list rendered zero rows (#32).
  A field is now also a reference when its option source (`ItemType` or `Sql`) declares
  `valueField="PageId"` (resolved through the page map) or `valueField="ParagraphId"`
  (resolved through the paragraph map), whatever the editor. A comma-separated value (a
  `CheckboxListEditor`) resolves id by id. An id that does not resolve logs
  `WARNING: Unresolvable page ID N in option field` or
  `WARNING: Unresolvable paragraph ID N in option field`, so strict mode escalates it;
  deferred, acknowledged and already-local page ids behave as for any other link.
  #15's guarantee holds: static options (`ImageAspectRatio` "0", a grid size "3") and
  option sources whose value field is anything else (an item `Id`, an order context id, a
  CSS class name) stay literal.

## 1.0.5-beta

### Fixed

- A SqlTable entry for a table with no PRIMARY KEY that declares `keyColumns` no longer logs
  a `WARNING`. The declared key is as deliberate as a primary key, so the resolution is an
  info line and nothing reaches the strict-mode escalator. Before, every strict-mode API
  deserialize of such a heap failed with HTTP 400 although every row was written (#30).
  Unique-index and all-columns inference still warn.

## 1.0.4-beta

### Fixed

- **An integer page-id column listed in `resolveLinksInColumns` is resolved.**
  `SqlTableWriter.ApplyLinkResolution` rewrote string columns only, and the
  provider converts each value to its column type first, so an `int` column
  such as `EmailMarketingEmail.EmailPageId` kept the source host's page id and
  landed pointing at an unrelated or missing page (#27). An integer value in a
  listed column (int, bigint, smallint, tinyint) now resolves through the same
  source-to-target page id map and keeps its type. Null and 0 are left alone.
  An id that does not resolve logs `WARNING: Unresolvable page ID N in int
  column [entry, field]`, the same class as the string path, so strict mode
  escalates it and the `unresolvable-link` quarantine applies to it. String and
  integer columns can share one list: the value's type decides the route.

### Added

- **Host-independent page binding for integer page-id columns.** On serialize,
  each integer page id in a `resolveLinksInColumns` column is recorded with the
  page's `PageUniqueId` in a reserved `pageRefs` mapping of the row document:

  ```yaml
  "EmailPageId": "5120"
  "pageRefs":
    "EmailPageId": "3f1c2a4e-..."
  ```

  On deserialize the GUID is looked up in `[Page]` on the target and wins over
  the id map, so a row binds to a page that carries `sourcePageId: 0` and is
  absent from the map. When the GUID is not on the target, the id map decides;
  when neither resolves, the warning above is logged.

  Compatibility: no manifest or config change. A row document without
  `pageRefs` (every document written before 1.0.4) reads as before and resolves
  through the id map. A document that carries `pageRefs` needs engine 1.0.4 or
  newer: an older engine treats the key as a column missing on the target and
  logs a `WARNING`, which fails a strict run.

## 1.0.3-beta

Proven on Dynamicweb release ring R1 (milestone 10.28, .NET 10); installs on
10.17.5 or newer.

### Fixed

- **A table with no primary key is no longer truncated.** The SqlTable provider
  decided its write strategy on `KeyColumns.Count == 0` before it read the mode,
  so a heap table was written with `DELETE FROM [table]` followed by inserts in
  Replace and in Merge alike, and the run reported `N created, 0 failed`. A
  Merge of one `DynamicStructures` row deleted a host's other Dynamic Workspaces
  and orphaned their level rows
  (justdynamics/Truvio.Commerce.Foundry#1305). The provider now resolves a match
  key and upserts through the same MERGE path a keyed table takes.

  Resolution order: the declared PRIMARY KEY, the entry's `keyColumns`, a UNIQUE
  index or constraint from a new `sys.indexes` query (not filtered, not
  disabled, no nullable column), then the full column tuple with identity
  columns excluded. An identity column is never a match key on its own, because
  auto-ids are environment-local.

  Merge never deletes. Replace upserts on the resolved key and leaves target
  rows absent from the payload alone.

- **`keyColumns` and `replaceStrategy` in `Serializer.config.json` now reach
  the manifest.** The config loader had neither field, so both were dropped
  silently and the serialized entry carried an empty key. They are mapped now;
  an unknown `replaceStrategy` value and either field on a non-SqlTable
  predicate are load errors, `replaceStrategy` under Merge warns at load, and a
  `keyColumns` entry passes the same column-name check as `nameColumn`. The
  admin-UI predicate save carries both fields over instead of dropping them.

- A NULL in a nullable inferred key column (from `keyColumns` or the
  all-columns fallback) matches with `IS NULL` and inserts NULL. It used to be
  bound as `''`, so such a row never matched its own target row and every
  changed write inserted a duplicate.

- Deserialize summary no longer counts the synthetic run-level outcome as a
  manifest entry, and the response names every walked entry with its own
  counts and errors (#10).

- A failed entry's error strings reach the response message, prefixed with the
  entry id, instead of the log file only (#11, engine half).

- A manifest entry's `serviceCaches` are validated before any row is written,
  naming the entry and the unknown name (#12). `docs/sql-tables.md` lists the
  tables with no registry cache.

- Unknown top-level config keys warn naming the key; the renamed
  `deployOutputSubfolder` / `seedOutputSubfolder` are a hard reject naming the
  replacement (#16).

- The SqlTable directory read orders by ordinal file name, reports a document
  no manifest entry names, and merges same-identity documents later-layer-wins
  before writing (#20, read path).

- `SqlTableWriter.BuildMergeCommand` throws a clear exception when the key
  column list is empty, instead of emitting the invalid `ON ()`.

- `TruncateAndInsertAll` no longer force-inserts payload auto-ids through a dead
  ternary. `SET IDENTITY_INSERT` is only set when the payload's identity values
  are the match key.

- A Merge over its own earlier output no longer fails on links already holding
  a local page id of a page this composition owns (pages whose GUID is in the
  YAML set being deserialized); an unresolvable source id that equals an
  unrelated host page id still warns and escalates under strict mode. Genuine
  `Unresolvable page ID` warnings name the entry, document and field (#13).

- The raw-numeric page-id remap fires only on fields the item type declares
  with a reference editor, and page id `0` is never mapped, so a literal `"0"`
  such as `ImageAspectRatio` is no longer rewritten into a page id (#15).

- Paragraphs placed directly on a page (`GridRowId 0`) are serialized as a
  page-level `paragraphs:` list (`paragraph-p<sort>.yml` beside `page.yml`) and
  deserialized back with their `Container`; stock Swift 2 service pages no
  longer ship empty (#24, Foundry #1315).

### Added

- `ShopService` and `GroupService` in `DwCacheServiceRegistry`, cleared
  automatically after a write to `EcomShops`, `EcomShopGroupRelation`,
  `EcomGroups` or `EcomGroupRelations` in addition to any declared
  `serviceCaches`; `docs/caches.md` documents what still needs a recycle (#14).

- `keyColumns` on a SqlTable predicate and manifest entry: the explicit match
  key for a table with no primary key. Optional; ignored on a keyed table.

- `replaceStrategy` on a SqlTable predicate and manifest entry: `truncate`
  restores whole-table replacement, under Replace only, as an explicit per-entry
  opt-in. Absent by default. Under Merge it is ignored with a WARNING.

- A `Deleted` count on `ProviderDeserializeResult`, `ProviderCounts` and the
  deserialize report's per-entry `PredicateSummary`, plus a run-level
  `TotalDeleted`. Non-zero only for an entry that opted into
  `replaceStrategy: truncate`, which makes `deleted == 0` a machine-checkable
  assertion for a Merge delivery gate.

- `EntryOutcome.Warnings` is populated again: it carries the key resolution for
  a table with no primary key and the `replaceStrategy` decisions. The run also
  logs `WARNING: [T] has no primary key; rows matched by <keyColumns | unique
  index (name) | all columns>; target rows not in the payload are preserved.`,
  so the strict-mode escalator records it with no extra plumbing.

- `DataGroupMetadataReader.GetUniqueIndexes`, the `sys.indexes` /
  `sys.index_columns` read behind resolution step 3. Only queried for a table
  with no primary key and no declared `keyColumns`, so a keyed table costs no
  extra round-trip.

- `docs/sql-tables.md` gains a "Tables without a primary key" section covering
  the resolution order, both new fields, the WARNING and the Deleted count.

### Changed

- The manifest schema version moves to 3 for the two new optional fields.
  Version 2 stays readable, so a tree serialized before this release still
  deserializes and no re-serialize is required.

- The documented platform floor in `README.md` and `docs/getting-started.md` is
  corrected to 10.17.5, the version the package is actually packed against.
