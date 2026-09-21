# Changelog

All notable changes to Truvio.Commerce.Serializer are recorded here. The
project follows semantic versioning, with `-beta` on every 1.0.x release while
the engine is shared with partners rather than fully productized.

Releases 1.0.0 through 1.0.2-beta shipped without release notes; this file
starts at 1.0.3-beta.

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

- `SqlTableWriter.BuildMergeCommand` throws a clear exception when the key
  column list is empty, instead of emitting the invalid `ON ()`.

- `TruncateAndInsertAll` no longer force-inserts payload auto-ids through a dead
  ternary. `SET IDENTITY_INSERT` is only set when the payload's identity values
  are the match key.

### Added

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
