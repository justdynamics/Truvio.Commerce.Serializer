# Link resolution

DW stores internal page references as numeric IDs: `Default.aspx?ID=5862`
lives inside item-type string fields, rich-text HTML, grid-row XML, and
ButtonEditor JSON. Those numeric IDs differ across environments — the page
that is `5862` on dev may be `9421` on staging. Truvio.Commerce.Serializer
rewrites those references as part of deserialize, using the stable
`PageUniqueId` GUID as the bridge.

## Table of contents

- [The three-pass pipeline](#the-three-pass-pipeline)
- [Pass 1: serialize-time link sweep](#pass-1-serialize-time-link-sweep)
- [Pass 2a: Content deserialize rewriting](#pass-2a-content-deserialize-rewriting)
- [Pass 2b: SqlTable column opt-in](#pass-2b-sqltable-column-opt-in)
- [What the regex matches](#what-the-regex-matches)
- [Raw numeric IDs](#raw-numeric-ids)
- [ButtonEditor SelectedValue JSON](#buttoneditor-selectedvalue-json)
- [Paragraph anchors](#paragraph-anchors)
- [Strict-mode interaction](#strict-mode-interaction)
- [Values already resolved on the destination](#values-already-resolved-on-the-destination)
- [Acknowledged orphan IDs](#acknowledged-orphan-ids)

## The three-pass pipeline

Link resolution runs three passes split across serialize and deserialize:

```
 Pass 1 (serialize-time)
 -----------------------
 BaselineLinkSweeper walks the freshly-written YAML tree and validates
 every Default.aspx?ID=N reference against the set of SourcePageId values
 captured in the same tree. Orphans fail serialize.

 Pass 2a (deserialize-time, content)
 -----------------------------------
 ContentDeserializer + InternalLinkResolver rewrite Default.aspx?ID=N in
 every page's ShortCut, NavigationSettings.ProductPage, ItemType fields,
 and PropertyItem fields using a source->target page ID map built from
 the PageGuidCache.

 Pass 2b (deserialize-time, SqlTable)
 ------------------------------------
 After Content predicates complete, SqlTableWriter rewrites any column
 listed in the predicate's resolveLinksInColumns, using the same
 source->target map.
```

The map is built by combining, for every page: `SourcePageId` (from the
YAML), `PageUniqueId` (GUID), and the target's `PageID` (looked up in
`PageGuidCache` by GUID). Pages without a `SourcePageId` or missing from
the cache are skipped.

## Pass 1: serialize-time link sweep

`BaselineLinkSweeper.Sweep(allPages)` runs immediately after
`ContentSerializer` writes the YAML tree. It walks every page and:

1. Collects every `SourcePageId` into a `validSourceIds` HashSet (via
   `CollectSourceIds`).
2. Collects every `SourceParagraphId` into a `validParagraphIds` HashSet
   (via the shared `ParagraphIdCollector.Visit`).
3. Walks every string field on every page, grid row, and paragraph,
   matching two regexes.

For each `Default.aspx?ID=N` match: the page ID must be in `validSourceIds`.
If the match includes a `#NNN` anchor, the paragraph ID must be in
`validParagraphIds`.

For each `"SelectedValue": "N"` match: the value must be in
`validSourceIds` OR `validParagraphIds` (`ButtonEditor` with
`LinkType=paragraph` stores a paragraph ID here, not a page ID).

Unresolved references are returned to `ContentSerializer`, which fails
serialize with:

```
Baseline link sweep found 2 unresolvable reference(s):
  - ID 9579 in page 3fa... (/Footer) / PropertyFields.LinkButton: Default.aspx?ID=9579
  - ID 9575 in page 5bc... (/Header) / Fields.CtaHref: Default.aspx?ID=9575
Fix the source baseline: include the referenced pages in a predicate path, or remove the references.
```

The intent is catch-at-author-time. An orphan that survives serialize would
fire at deserialize time as an `Unresolvable page ID N in link` warning,
but by then the baseline is already in Git — harder to unwind.

Raw numeric references (e.g. a plain `"121"` string that the deserialize-time
resolver would rewrite) are **not** swept here. Too many false positives on
ordinary numeric fields (sort orders, widths, prices). The deserialize-time
resolver still handles them via its "entire string is a pure number in the
map" check.

Source: `src/Truvio.Commerce.Serializer/Infrastructure/BaselineLinkSweeper.cs`.

## Pass 2a: Content deserialize rewriting

`ContentDeserializer` constructs an `InternalLinkResolver` per-run with the
source → target page ID map and the source → target paragraph ID map, and
passes it to every pipeline stage that writes string values back into DW:

- `ShortCut` on every page
- `NavigationSettings.ProductPage`
- Area-level item-type field values (header/footer/master connections)
- Every item-type string field on every page and paragraph
- `PropertyItem` fields

The resolver `ResolveLinks` method runs three regex passes per string:

1. **Raw numeric short-circuit.** If the entire string parses as an int
   and that int is in the source → target map, return the target ID as
   a string. This handles LinkEditor values that store page IDs without
   the `Default.aspx?ID=` prefix.
2. **SelectedValue JSON.** Every `"SelectedValue": "N"` is rewritten if
   `N` is in the page map. Unresolvable matches are left unchanged (no
   warning emitted for this pattern — pass 1 already caught orphans).
3. **Default.aspx?ID=N with optional #PPP.** The full match is rewritten
   to use the target page ID. If the anchor `#PPP` is present, the
   paragraph ID is also rewritten via the paragraph map. Unresolvable
   page IDs emit `WARNING: Unresolvable page ID N in link`; unresolvable
   paragraph IDs emit `WARNING: Unresolvable paragraph ID N in anchor link`
   and the raw value is preserved.

The resolver counts resolved and unresolved refs across all calls. At end
of run, `ContentDeserializer` logs the totals as part of the run summary.

Source: `src/Truvio.Commerce.Serializer/Serialization/InternalLinkResolver.cs`.

## Pass 2b: SqlTable column opt-in

SqlTable columns holding `Default.aspx?ID=N` values — `UrlPath.UrlPathRedirect`
is the Swift 2.2 example — are rewritten only if the predicate opts in via
`resolveLinksInColumns`:

```json
{
  "name": "UrlPath",
  "providerType": "SqlTable",
  "table": "UrlPath",
  "resolveLinksInColumns": ["UrlPathRedirect"]
}
```

At deserialize, `SerializerOrchestrator` runs Content predicates first so
the source → target page ID map is fully built. Then for every SqlTable
predicate with at least one column in `resolveLinksInColumns`:

1. `SqlTableWriter` reads the row's column value.
2. `InternalLinkResolver.ResolveInStringColumn(value)` (an alias for
   `ResolveLinks`) rewrites `Default.aspx?ID=N` and `"SelectedValue": "N"`
   using the map.
3. The rewritten value is parameter-bound into the existing MERGE
   statement. The raw rewrite never reaches SQL composition — T-37-05-03
   mitigated.

Column names in `resolveLinksInColumns` are validated at config-load
against `INFORMATION_SCHEMA.COLUMNS`, same gate as `excludeFields` and
`includeFields`.

An integer column in the list (`EmailMarketingEmail.EmailPageId`) holds a
bare page id. It resolves through `InternalLinkResolver.ResolvePageId`, and
the serializer also records the page's `PageUniqueId` in the row's `pageRefs`
block so the row binds by GUID on any host. See
[SQL tables: integer page-id columns](sql-tables.md#integer-page-id-columns).

## What the regex matches

Two compiled regexes drive both sweep and resolve:

```csharp
// Matches "Default.aspx?ID=NNN" optionally followed by "#PPP"
// Group 1 = "Default.aspx?ID="
// Group 2 = page ID digits
// Group 3 = full fragment "#PPP"
// Group 4 = paragraph ID digits
(Default\.aspx\?ID=)(\d+)(#(\d+))?

// Matches `"SelectedValue": "NNN"` in ButtonEditor JSON
// Group 1 = `"SelectedValue": "`
// Group 2 = digits
// Group 3 = closing quote
("SelectedValue":\s*")(\d+)(")
```

The first regex is case-insensitive (`IgnoreCase`), so
`default.aspx?id=121`, `Default.aspx?ID=121`, and
`DEFAULT.aspx?ID=121` all match. Greedy `\d+` captures the full number,
so `ID=1` doesn't corrupt `ID=12` — there's no boundary ambiguity.

## Raw numeric IDs

Some DW editors (notably `LinkEditor`) store a page ID as a plain integer
string rather than as `Default.aspx?ID=N`. The resolver handles this via
the raw-numeric short-circuit in `ResolveLinks`:

```csharp
if (allowRawNumericPageIds && int.TryParse(fieldValue.Trim(), out var rawPageId) && rawPageId != 0
    && _sourceToTargetPageIds.TryGetValue(rawPageId, out var rawTargetId))
{
    _resolvedCount++;
    return rawTargetId.ToString();
}
```

The check only fires when the entire string parses as an integer AND that
integer is in the source → target map. A random `"121"` that's not a
known source page ID (e.g. a width in pixels) passes through untouched.

**Only reference-typed fields (issue #15).** "Not in the map" is not enough
protection: a literal that *does* collide with a source page id is rewritten
into a page id. `ImageAspectRatio: "0"` on a Swift `ProductMediaTable` paragraph
arrived on the host as `8453`, and the template rendered
`style="--bs-aspect-ratio: 8453"`. So the short-circuit now fires only for
fields the item type declares with a reference editor —
`LinkEditor` / any `*LinkEditor`, `ButtonEditor`, `*PageEditor`,
`*ParagraphEditor` (`ReferenceFieldClassifier.IsReferenceEditor`,
resolved per item type by `ReferenceFieldLookup`). Every other field keeps its
literal. Page id `0` is never a link target and is never mapped, in either
direction (`BuildSourceToTargetMap` skips `SourcePageId` 0 as well).

If the item type's metadata cannot be read — replace mode deploys the item-type
XML in the same run that writes the items — the lookup returns "unknown" and the
field stays permissive, i.e. the pre-#15 behaviour. The unambiguous
`Default.aspx?ID=N` form is rewritten on every field regardless.

`BaselineLinkSweeper` deliberately skips the raw-numeric case — the
false-positive rate on ordinary numeric fields (sort orders, widths) is
too high for a pre-commit gate. The deserialize-time resolver is
precise because it checks membership in the map.

**Item identity is never resolved.** The raw-numeric short-circuit means an
item's own numeric `Id` (its primary key) would be rewritten if that id
happened to match a source page id — which then persists the item under a
*different* row on save, silently overwriting a neighbouring item and orphaning
the original. `ContentDeserializer.ResolveLinkFields` therefore excludes every
`ItemSystemFields` entry (`Id`, `ItemInstanceType`, `Sort`,
`GlobalRecordPageGuid`, `MasterParagraphGuid`) from resolution — the same
carve-out `SaveItemFields` applies. Only content fields are ever link-rewritten;
system fields carry identity, not links.

## ButtonEditor SelectedValue JSON

DW's `ButtonEditor` serializes its value as a JSON blob embedded in an
item-type string field. The relevant shape:

```json
{
  "LinkType": "page",
  "SelectedValue": "5862",
  "DisplayText": "Shop now"
}
```

The resolver's `SelectedValuePattern` rewrites the `SelectedValue` digits
when `LinkType` is `page` (or indistinguishable from it at the regex
level — the resolver doesn't parse the JSON). When `LinkType` is
`paragraph`, `SelectedValue` holds a paragraph ID rather than a page ID;
`BaselineLinkSweeper` validates those matches
against paragraph IDs as well (source: `BaselineLinkSweeper.CheckField`
`SelectedValuePattern` block at ~line 156).

## Paragraph anchors

`Default.aspx?ID=4897#15717` is a page-plus-paragraph link. The `4897` is
a page; the `15717` is a paragraph within that page. Both segments are
rewritten independently:

- The page portion uses the source → target page ID map.
- The anchor portion uses a separate source → target paragraph ID map
  built via `InternalLinkResolver.BuildSourceToTargetParagraphMap`, which
  walks Pages → GridRows → Columns → Paragraphs and maps each
  `SourceParagraphId` via `ParagraphUniqueId` to the target's
  `ParagraphID`.

If the page resolves but the anchor doesn't, the page portion is
rewritten and the anchor is preserved as-is with a warning:

```
WARNING: Unresolvable paragraph ID 15717 in anchor link
```

`BaselineLinkSweeper` validates both segments: an
unresolved page OR an unresolved anchor fails the sweep.

## Strict-mode interaction

Unresolved references at deserialize time log a `WARNING:` line, which
the orchestrator's log wrapper records via `StrictModeEscalator.RecordOnly`.
In strict mode, every unresolved ref accumulates and throws a
`CumulativeStrictModeException` at end of run.

Serialize-time sweep failures do NOT go through the escalator — they're
a hard serialize error regardless of `strictMode`. A baseline with orphan
references is considered broken data that should never enter Git.

## Values already resolved on the destination

A deserialize reads link values back **from the destination** in its post-write
pass. On a host that already holds a deserialized composition — Replace, then
Merge of the same edition — those values hold *this host's* page ids, written by
the earlier run. They are not source ids and re-resolving them is meaningless:
before issue #13 the second Merge logged
`WARNING: Unresolvable page ID 8559 in link` and strict mode turned it into an
HTTP 400, with no entry, document or field named.

The resolver therefore knows the host's own page ids (the target ids of its map,
plus the local page ids the deserializer passes in). A link holding one of them
is reported as already resolved and left untouched:

```
Link already resolved: page ID 8559 is a local page id — left unchanged [entry 'swift-content', document 'page 'About' (ID=8559)', field 'item|Swift-v2_Button|41|FirstButton']
```

It counts towards `already local (destination-read)` in the run's
`Link resolution:` summary, not towards `unresolvable`, so it never escalates.

Every genuine `WARNING: Unresolvable page ID` line now carries the same
`[entry …, document …, field …]` suffix, so a strict-mode failure says which
entry, which document and which field to look at.

## Acknowledged orphan IDs

`acknowledgedOrphanPageIds` on a Content predicate lets a known-broken
source baseline serialize successfully while keeping the warning loud:

```json
{
  "name": "Content - Swift 2",
  "providerType": "Content",
  "areaId": 3,
  "path": "/",
  "acknowledgedOrphanPageIds": [15717, 8308]
}
```

When `BaselineLinkSweeper` finds an unresolvable reference, it checks
whether the orphan ID is in the predicate's
`acknowledgedOrphanPageIds`. If yes, the sweep logs:

```
WARNING: acknowledged orphan ID 15717 in ... (context)
```

and continues. If no, the sweep fails as normal. Acknowledged IDs are
range-checked at config-load to reject malicious inputs.

Use cases:

- Legitimate upstream bug that can't be cleaned before your release.
  (Example: Swift 2.2 ships with five known-broken page ID references
  that were cleaned via `tools/swift22-cleanup/01-null-orphan-page-refs.sql`
  before being shipped as a baseline.)

When the underlying data is cleaned, remove the entry from
`acknowledgedOrphanPageIds`. Leaving acknowledged IDs around silences
real future drift.

## See also

- [Concepts](concepts.md#the-serialize-flow) — where link resolution fits
- [SQL tables](sql-tables.md#resolvelinksincolumns) — `resolveLinksInColumns` in depth
- [Strict mode](strict-mode.md) — how warnings escalate
- [`BaselineLinkSweeper.cs`](../src/Truvio.Commerce.Serializer/Infrastructure/BaselineLinkSweeper.cs) — serialize-time sweep source
- [`InternalLinkResolver.cs`](../src/Truvio.Commerce.Serializer/Serialization/InternalLinkResolver.cs) — deserialize-time resolver source
