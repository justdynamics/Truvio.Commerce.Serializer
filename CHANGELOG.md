# Changelog

All notable changes to Truvio.Commerce.Serializer are recorded here. The
project follows semantic versioning, with `-beta` on every 1.0.x release while
the engine is shared with partners rather than fully productized.

Releases 1.0.0 through 1.0.2-beta shipped without release notes; this file
starts at 1.0.3-beta.

## 1.1.0-beta

Content ownership R7 (#52): item lists travel inside the document that owns them (#66,
#67, #68), and a delivered header, menu text and sort stick (#69). What an operator must
know before upgrading:

- **The format switches on the configuration.** While a SqlTable predicate on `ItemList`
  or `ItemListRelation` is configured, serialize writes lists as table rows exactly as
  1.0.9-beta did. Remove those predicates and serialize writes the new shape: `itemLists`
  inside the paragraph or page document.
- **A folder in the new shape needs this engine.** Its manifest declares
  `"minEngineVersion": "1.1.0-beta"`, and 1.0.9-beta refuses that manifest. Upgrade every
  environment a new-shape folder is delivered to first.
- **New list entries get numbers from the target host,** and the engine records which
  document entry each number is in its own table `TruvioSerializerItemListEntry`, created
  on the first write. The table is part of the database, so it travels with a backup.
- **A delivery never deletes a list entry.** Merge still re-creates an entry an editor
  deleted (known behaviour until R6).
- **The dry run reports list changes and converges.** Delivered paragraph headers, page
  menu texts and sorts now stick, so a dry run after a delivery reports no change for them.
- **Rolling back after a new-shape delivery is a database restore** (see Known behaviour).

### Added

- **Item lists inside paragraph and page documents (R7, #52).** A list edited with
  Dynamicweb's item relation list editor (accordion items, slides, tiers) is serialized
  into its paragraph's or page's document under `itemLists`, by field: ordered entries,
  each with the entry item's `id`, its `fields` and, for a list inside an entry,
  `itemLists` again (up to five levels). No list id is written. `itemListOptions` carries
  `changedOnLanguage` when a language layer's list differs from its master's. An empty
  list is written as `[]`, so the document owns the field either way.
- **A list follows its document's lifecycle.** Created with the document. Under
  `overwrite` (Replace) matched entries are overwritten, missing ones created, and the
  delivered entries take the document's order. Under `fill` (Merge) a matched entry
  keeps its values and its place and gets only its empty fields filled, a missing entry
  is appended, an entry only the host has stays, and a second delivery changes nothing.
  Under `keep` an existing document's lists are not touched.
- **A delivery never deletes a list entry.** Under Replace an entry the host has and
  the document does not is left in place, after the delivered entries in the relative
  order the host gave it, with the lists it carries, and reported in the run log as
  information naming the list, the entry id and the paragraph or page. It is not a
  `WARNING` (owner decision B of 2026-11-13): strict mode does not fail on it, and the
  dry run lists it as a note that does not count as a change. Merge never removed one.
  No path of the engine deletes a list entry or its item.
- **The dry run counts list changes.** Per list: entries to create, entries to update
  with their field diff (Merge: fields to fill), an order change, nested lists included;
  host-only entries left in place are listed as notes, not changes. A paragraph or page
  whose only change is in its lists counts as `updated` instead of `SKIP (unchanged)`
  (#67). The sums reach the deserialize
  response: each Content entry carries `itemLists` (lists and entries created, entries
  updated, fields filled, host-only entries, lists reordered, flags changed), in the dry
  run and in the real run.
- **Engine floor in the manifest.** A mode folder with at least one document carrying
  `itemLists` declares `"minEngineVersion": "1.1.0-beta"` in `<mode>-manifest.json`; a
  folder without lists is unchanged. An engine through 1.0.9-beta refuses the key (its
  manifest read is strict), so it stops at the manifest read and writes nothing from that
  mode folder; this engine refuses a floor above what it meets; and a delivery tool can
  compare the floor with the host's installed engine before it deserializes (#67).
- **Entry identity that survives another host (owner decision A of 2026-11-13).** An
  entry the host does not have is created under a new number from the target host's own
  item-type counter (`ItemTypeId`, through the platform's id generator, so the counter
  moves past it), never under the source's number and never under a text id, also when
  the document's id is text (`en-tier-1`). The entry's key (the document's GUID, the field
  and the document's id for the entry, `sl-` plus 24 hex characters) is recorded in the
  Serializer's own table `TruvioSerializerItemListEntry` on the host, created the first
  time an entry is recorded (a host without the table reads as having no recorded
  entries); an entry row and the list and relation rows have no column the platform leaves
  alone. The table is an ordinary table of the host database: it travels with a backup or
  bacpac and comes back with a restore, together with the entries it names. A delivery matches an entry inside the same list of the same
  document, first through that table, then by its own id, so an entry a host received as
  SqlTable rows (numeric or text id) is matched and kept as it is, never renamed or
  duplicated. A serialize writes an entry this engine created under the document id it
  was created for, so the same document id names the entry on every host (#68).
- **Entries are link-resolved and swept.** The fields of entries a run wrote go through
  the link pass (a Merge-filled entry only in the fields it filled); the serialize-time
  link sweep covers entry fields; the entries' item types are listed in
  `templates.manifest.yml`.

### Changed

- **`ItemList`, `ItemListRelation` and entry-table predicates are no longer needed, and
  decide the format while present.** With no SqlTable predicate on `ItemList` or
  `ItemListRelation`, serialize writes lists into the documents: a paragraph or page with
  an inline list field gets `itemLists` and loses that field from `fields`. While such a
  predicate is configured, documents are written exactly as 1.0.9-beta wrote them (one
  log line says so), because rows carry the source's list ids and the two forms cannot
  be mixed. A document without `itemLists` deserializes as before. The shipped
  `full-sync-example.json` no longer has the two predicates. A SqlTable predicate on an
  entry table (`ItemType_<entry type>`) whose entries the documents now carry is a
  `WARNING`. Upgrade every environment before delivering documents with `itemLists`: an
  engine through 1.0.9-beta refuses the folder's manifest (see Engine floor), and if the
  manifest's `minEngineVersion` were removed by hand it would ignore `itemLists` and null
  the list field.
- **Stricter than the rest of a document.** An item list entry with an unknown member,
  without an `id` or that is not a mapping fails the read. A list name that is not an
  item list field of the item type on the host, an entry item type the host lacks, a
  duplicate `id` and lists nested deeper than five levels fail the document. At
  serialize, a field pointing at a missing list or one of another item type, a relation
  whose item is gone and a list two documents share are `WARNING`s.

### Known behaviour

- **Merge re-creates a list entry an editor deleted** (until content ownership R6). Merge
  matches entries by id and creates a document entry the host's list lacks, so an entry
  removed on the host comes back with the next Merge delivery. Accepted until R6 adds
  tombstones.
- **A field that points at another item type's list** gets a new list of its own and the
  run logs a `WARNING` (strict mode stops). Kept a `WARNING` on purpose: a strict
  production delivery stops, and the Foundry's pre-delivery audit names such fields, and
  lists shared between owners, before a delivery fails on them.
- **Rollback after a new-shape delivery is a database restore.** Once a host has received
  documents with `itemLists`, its lists hold entries under new host numbers and
  `TruvioSerializerItemListEntry` records them. Re-installing 1.0.9-beta does not undo
  that, and 1.0.9-beta refuses the new-shape folders. To go back, restore the database
  backup taken before the delivery (the map table comes back with it), then install the
  older engine.
- **Dynamicweb cannot create a language version of a paragraph whose list has text entry
  ids** (a platform limit, retested on Dynamicweb 10.29.8 and 10.30.6; not filed upstream).
  `ParagraphService.CreateFromMaster` copies the list with SQL that puts the entry ids into
  an `IN (...)` list unquoted (`en-tier-1` fails with "Invalid column name 'en'"), throws,
  and leaves an empty `ItemList` row behind; numeric ids copy cleanly. Every entry this
  engine creates gets a host number, so delivered lists copy. Entries that already have a
  text id on a host (delivered as SqlTable rows before R7) keep it, and a language version
  of their paragraph still cannot be created until they are renumbered on the host.
  Saving a master paragraph or its item did not touch a linked language version's list.

### Fixed

- **An entry that arrived as a table row keeps its `ItemInstanceType`** when a delivery
  writes it through the item API (owner decision C of 2026-11-13). Dynamicweb writes an
  empty value on every save of an item type without a base; the value the row came with is
  put back on that one row.
- **The dry run of a Replace compares what the write would change** (owner decision D of
  2026-11-13). On the CABP clean room it reported about 1,100 updates on a target with the
  same content; it now reports 0 there. `GlobalRecordPageGuid` and `MasterParagraphGuid`
  are compared as the paragraph's links, not as item fields; `Text` and every other field
  are compared against the item the write goes to (a field the item type does not declare
  is ignored by the write and by the diff), after the link resolution the link pass
  applies, read from the item repository rather than the item cache, in the form a
  document carries values (`True`/`true`, a number and its text, CRLF and LF, null and
  empty, a blank button); system fields are not compared; an item field the write would
  null is reported; permissions are a change only when they differ from the host's; a grid
  row whose `Sort` is only renumbered in the same position is not a change; a Merge fill
  of an empty value into an empty column is not a change (#68).
- **A delivered paragraph header, page menu text and paragraph sort are what the row
  holds after the run.** Dynamicweb's own save re-derives them: `SaveParagraph` sets the
  paragraph's `Header` to its item's title and `SavePage` sets the page's `MenuText` to
  its item's title whenever that title is not empty (read from the item as it is at the
  time of the save, so on an existing paragraph it is the title from before the run wrote
  the item's fields), and a `Sort` of 0 becomes the page's highest sort + 1. A header or
  menu text that is not the item title (an editor's label, a Spanish menu text over an
  untranslated item) therefore never stuck on an existing document, and the dry run
  reported it after every delivery (on the CABP clean room after two deliveries: 79
  headers, 26 menu texts, 2 sorts), so a drift stop reading the dry run would fire on every
  deploy. Every page and paragraph save of the writer, the link pass and the deferred link
  finalization now goes through one guard that writes back, by plain SQL on that one row,
  whichever of the three columns the platform's save changed. The values are the
  lifecycle's: under Replace (`overwrite`) the document's; under Merge (`fill`) the host's,
  and the document's only where the host's header or menu text was empty (an editor's
  value is never replaced, also not by the platform's title when another fill saves the
  paragraph or page; a paragraph's sort is never moved); under `keep` nothing, as before.
  A save that only rewrites a link keeps what the row held (`DerivedColumnGuard`, #69).

### Not covered

- Lists whose entries are pages or paragraphs (item source other than inline), and lists
  on a grid row's, an area's or a page property item, keep travelling as a raw list id;
  serialize names each such field once.

## 1.0.9-beta

Content ownership: who owns a document after it is delivered (R1, R2, R3, R4, R13, R15).
A config without the new keys loads the same predicates and writes byte-identical YAML and
manifests. Two things change without new keys: Merge on existing grid rows and paragraphs
(R2) and the empty-GUID check at serialize (R13).

### Added

- **A create-only lifecycle: `onExisting` (R1, #55).** The ownership header of a document
  takes an optional `onExisting`: `overwrite`, `fill` or `keep`. Absent, the mode decides as
  before (replace overwrites, merge fills). With `keep` a missing document is created and an
  existing area, page, grid row, paragraph or SqlTable row is left exactly as the host has
  it: no scalar, item field, parent, sort, permission or master link is written, and it is
  left out of the link pass. A predicate's `onExisting` stamps it on every document the
  predicate serializes. An unknown value is a `WARNING` and the document runs with its mode.
  Config load rejects `onExisting: keep` with `replaceStrategy: truncate`.
- **Content predicates by identity (R3, #56).** `pageUniqueIds` (with `includeDescendants`),
  `navigationTags`, `itemTypes` and `excludePageUniqueIds` select pages by what they are
  instead of by menu-text path, so a rename never moves a page in or out of a predicate.
  They combine by OR, stay limited by `areaId` and `path`, and excludes win. A language
  layer is matched as its master. The admin tree, the editor alerts and the coverage counts
  use the same matcher, and the tree marks the ancestors of an identity-selected page as
  containing managed pages. An inline API scope is still given by path or page id.
- **Body and single-document stamping (R4, #57).** `bodyOnExisting` stamps a page's grid
  rows and paragraphs apart from the page itself. `ownershipOverrides` stamps one page, grid
  row or paragraph by (master) GUID with its own `mode`, `onExisting` and, for a page,
  `bodyOnExisting`. The most specific rule wins: the paragraph's entry, its row's entry, the
  page entry's `bodyOnExisting`, the predicate's `bodyOnExisting`, the page header.
- **Repository keys in the manifest (R15, #59).** `<mode>-manifest.json` accepts top-level
  `dataClass`, `systemOfRecord` and a free `metadata` object, carries them through a
  re-serialize and ignores them at deserialize. Any other unknown top-level key still fails
  the read. When the previous manifest exists but cannot be read, the serialize still
  succeeds and logs a `WARNING` naming the file and the keys that were not carried.

### Changed

- **Merge keeps existing grid rows and paragraphs (R2, #49).** Through 1.0.8-beta a Merge
  overwrote an existing grid row or paragraph source-wins and moved a page back under its
  YAML parent. Merge is now the field-level fill the documentation describes: a grid row
  keeps its sort, a paragraph keeps its grid row, column and sort, a moved page stays where
  it was moved, and only fields the host left empty are filled (item fields absent from the
  YAML are never nulled; module settings are filled element by element when the module is
  the same). Missing rows and paragraphs are still created in full. A change a merge layer
  makes to an existing row or paragraph therefore reaches a host only where the field is
  empty, or through Replace. An unchecked box or a 0 still counts as empty and is filled
  again.
- **Serialize refuses an empty unique id (R13, #58).** A Content predicate fails at
  serialize when a page, grid row or paragraph has the all-zero GUID, and names each one
  with its source id and path; such a document was inserted again on every deploy.
- **The editing-screen alert speaks for the document on screen.** The verdict is read off
  the header serialize stamps on the page, its body, the grid row or the paragraph, with
  `bodyOnExisting` and `ownershipOverrides` applied. It says "seeded" only when every
  managing predicate keeps the document and "overwritten" as soon as one overwrites it. It
  is decided on the predicates of both modes: `showReplaceIndicators` and
  `showMergeIndicators` only decide whether it is shown, so a page whose true verdict is a
  hidden cue shows no alert instead of a milder one. The merge alert states that a merge
  run does not move, re-sort or overwrite the page, its rows or its paragraphs.
- **`mode` and `onExisting` take exactly one name.** A number or a comma-joined list
  ("Replace,Merge", "overwrite, keep") was read as a valid value; it is now unknown. A
  predicate `mode` written that way is rejected at config load.

### Fixed

- **A Merge no longer moves links it kept.** The link pass read a value a Merge had kept
  (already a target page id) as a source id and mapped it again on every run, so a
  `NavigationRoot` drifted from page to page with each delivery. The link pass now resolves
  only what the Merge filled: the filled item fields, a shortcut or product page the run
  filled, and inside existing module settings only the elements the run filled. A merged
  page whose YAML carries no property fields is covered too (#49).
- **Merge fills a grid row's vertical alignment.** It was written by Replace only; `None`
  counts as empty on both sides (#49).

### Tooling

- The e2e round trip runs onto a blank Dynamicweb database: the purge reseeds only an
  identity that has been used, the schema-align script adds the seven `EcomGroups`
  group-field columns, and the README lists the `SqlServer` PowerShell module. The GUID-only
  integration test allows a document's own `sourcePageId` line (#64).

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
