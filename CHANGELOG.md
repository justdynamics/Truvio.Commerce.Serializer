# Changelog

All notable changes to Truvio.Commerce.Serializer are recorded here. The
project follows semantic versioning, with `-beta` on every 1.0.x release while
the engine is shared with partners rather than fully productized.

Releases 1.0.0 through 1.0.2-beta shipped without release notes; this file
starts at 1.0.3-beta.

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
