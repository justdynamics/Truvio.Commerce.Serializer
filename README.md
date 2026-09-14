# Truvio.Commerce.Serializer

**Git-versioned content and data sync for Truvio Commerce 10.**

> Truvio Commerce is the platform formerly known as Dynamicweb. The platform's
> NuGet packages, API namespaces, and host binaries still carry the `Dynamicweb`
> name (e.g. `Dynamicweb 10.23.9`, `Dynamicweb.Host.Suite`), and this project's
> docs use "DW" as shorthand for the platform throughout.

Truvio.Commerce.Serializer is a Truvio Commerce AppStore app that serializes and deserializes
database state to and from YAML files on disk. Teams treat YAML as the single source
of truth for content, shop configuration, payment and shipping definitions, VAT rules,
and URL routing — committing diffs to Git, reviewing changes in pull requests, and
applying them across dev, test, QA, and production through ordinary CI/CD.

Identity is GUID-based, so pages survive environments where numeric IDs differ.
Cross-environment `Default.aspx?ID=N` references are rewritten automatically on
deserialize. Strict mode escalates recoverable warnings to hard failures
so CI/CD pipelines fail loud on content drift, schema drift, or missing templates.

## Why it exists

Hand-editing content across Truvio Commerce environments is slow, error-prone, and leaves
no audit trail. Staging drifts from production. Nobody remembers who changed the VAT
rates last March. Rolling a bad content change back means restoring a whole database.

Truvio.Commerce.Serializer fixes that by treating the database state a DW instance depends
on — shop structure, payment definitions, item types, pages, permissions, navigation
— as code. Git becomes the audit log. Pull requests become the review step. Rollback
becomes `git revert` followed by a redeploy.

## Features

- **Predicate-based selective sync.** Content predicates pick subtrees of pages,
  grids, and paragraphs. SqlTable predicates pick arbitrary tables with optional
  `WHERE` clauses. Exclude rules, per-item-type field exclusions, and embedded-XML
  element filters keep per-environment noise out of the baseline.
- **GUID identity, not numeric IDs.** `PageUniqueId` matches source and target.
  Numeric `PageID` is resolved per environment at deserialize time.
- **Cross-environment link rewriting.** `Default.aspx?ID=N`, paragraph anchors,
  and `ButtonEditor` `SelectedValue` JSON are rewritten source → target on content
  deserialize. SqlTable columns opt in via `resolveLinksInColumns`.
- **Replace and Merge modes.** `Replace` is source-wins (baseline overwrites target).
  `Merge` is destination-wins (field-level fill: only fields the target left empty are
  filled) — customer content lands once and is never trampled by later replace runs.
- **Strict mode for CI/CD.** Recoverable warnings (unresolvable links, missing
  templates, schema drift, FK orphans, cache invalidation failures) accumulate and
  throw one `CumulativeStrictModeException` at end-of-run. HTTP 4xx on the API.
  Default: `on` for API/CLI callers, `off` for admin UI (interactive exploration).
- **SQL identifier whitelisting.** Predicate `table`, `nameColumn`, `excludeFields`,
  `includeFields`, and `where` clauses are validated against `INFORMATION_SCHEMA`
  before any SQL runs. `;`, `--`, `/*`, `xp_`, `DROP`, `EXEC`, and related tokens
  are rejected at config-load.
- **Admin UI + Management API.** Configure predicates, item types, and XML filters
  from `Settings > Developer > Serialize`. Run `Serialize` and `Deserialize` from
  CI/CD using the DW Management API, each call optionally scoped inline to one
  subtree or table without editing the config. Every serialized document carries
  an `ownership` header recording which mode wrote it.

## Quick start

### From a release (no build, no source environment)

Install the app from any
[GitHub release](https://github.com/justdynamics/Truvio.Commerce.Serializer/releases)
(`Truvio.Commerce.Serializer.<version>.nupkg`) or from the DW10 app store under
*Available apps*.

To start a Swift (or Digital Asset Portal) solution **from YAML alone**, pair the
app with ready-made content from the companion distribution:

**[justdynamics/Truvio.Commerce.Distribution](https://github.com/justdynamics/Truvio.Commerce.Distribution)**

The distribution is structured as versioned **layers** composed into gate-proven
**editions**. Clone it, pick an edition (`base-only`, `swift-demo`,
`headless-demo`), and apply its layers to the host — each layer ships serialized
mode trees the serializer deserializes into the database. Pin a specific
composition by its annotated tag (`editions/<name>/<semver>` /
`layers/<name>/<semver>`). It is git-clone consumption: there are no release
archives. See the distribution's
[README](https://github.com/justdynamics/Truvio.Commerce.Distribution/blob/main/README.md)
and [layer/edition catalog](https://github.com/justdynamics/Truvio.Commerce.Distribution/blob/main/LAYERS.md).

### From source

```bash
# 1. Build the DLL
dotnet build src/Truvio.Commerce.Serializer/ -c Release

# 2. Copy to your DW instance's bin/ directory
cp src/Truvio.Commerce.Serializer/bin/Release/net8.0/Truvio.Commerce.Serializer.dll \
   /path/to/your/dw-instance/bin/

# 3. Restart the DW host, then sign in and go to
#    Settings > Developer > Serialize > Predicates to configure what to sync.
#    Or edit Files/System/Serializer/Serializer.config.json directly.

# 4. Serialize on the source environment
curl -X POST https://source.example.com/Admin/Api/Serialize \
  -H "Authorization: Bearer CLD.your-api-key"

# 5. Commit the SerializeRoot/ YAML tree to Git, deploy it to the target, then deserialize
curl -X POST https://target.example.com/Admin/Api/Deserialize \
  -H "Authorization: Bearer CLD.your-api-key"
```

Full walkthrough: [`docs/getting-started.md`](docs/getting-started.md).

### Start from the Swift starter configuration

For a Swift site, copy
[`src/Truvio.Commerce.Serializer/Configuration/swift-starter.json`](src/Truvio.Commerce.Serializer/Configuration/swift-starter.json)
to `Files/System/Serializer/Serializer.config.json`. App-store installs drop the same file
into `Files/System/Serializer/swift-starter.example.json` — rename it to
`Serializer.config.json` to activate it. It encodes the analysed split (full rationale per
content item: [Swift replace/merge analysis](docs/swift-replace-merge-analysis.md)):

- **Replace** — `Site framework`: the platform-wired site (Shop + PLP/PDP, customer
  center, checkout, service pages, product components, system emails, navigation
  scaffold, page presets) plus the commerce framework tables (countries, currencies,
  languages, VAT, shops, payments, shippings, order flow, URL paths). Identical on
  every environment; later replace runs overwrite.
- **Merge** — the customer-owned content surfaces, one predicate per subtree: Home,
  the site chrome (Header / Footer), About, blog posts, footer legal/help pages,
  Find dealers and the example newsletters — plus starter catalog data (groups,
  products, variants, discounts). Lands once; afterwards the receiving environment
  owns it — later passes only fill fields that are still empty.
- **Everything else** (orders, users, logs) is environment data and is never serialized.

### Reading the content tree

The admin content tree shows per-page coverage so you can see which pages are managed without
opening the config:

| Icon | Meaning |
|---|---|
| sync | Replace-managed — tooltip names the predicate |
| sync-slash | Partially managed — tooltip lists the excluded paths below this page, the replace-managed subtrees under an unmanaged page, or the fields/settings on this page that are excluded by type and stay local (e.g. the cart page's `eCom_CartV2` module settings) |
| flower | Merge-managed starter content — lands once, local edits on the target are preserved. **Off by default** (broad merge coverage would mark nearly every page); enable via Settings > Developer > Serialize > "Show merge indicators" or `showMergeIndicators` in the config |
| *(none)* | Not serialized (environment-owned) |

The editing screens carry the same signal where it matters most: the visual editor, the
page properties editor, the paragraph dialog and the grid-row dialog show a screen alert
for the owning page — a warning
on replace-managed pages ("content here is overwritten by the next replace run") and, when
merge indicators are enabled, an info note on merge-managed pages ("your edits are preserved").
The `showMergeIndicators` setting gates both merge cues — tree icons and the editor note —
while the replace warning always shows. Both alerts name any fields excluded by type on
the page, so a partially managed page (like the cart) says exactly which settings stay
local. Field-level carve-outs (like the cart settings) appear as a clickable header chip —
"eCom_CartV2 — 21 settings stay local — view" — that opens the exact exclusion list, and
the tree's right-click menu carries the same "View excluded fields" action. Rows and
paragraphs always inherit their page's mode, so one page-level alert covers them all. The
commerce settings edit screens (payment, shipping, country, currency, language, shop,
order flow, order state) carry the same alert when a SqlTable predicate manages their
table.

## How it works

```
    Source environment                          Target environment
    (e.g. dev, QA)                              (e.g. production)

      DW database                                 DW database
            |                                          ^
            | 1. POST Serialize                        | 5. POST Deserialize
            v                                          |
    Files/System/Serializer/                    Files/System/Serializer/
      SerializeRoot/                              SerializeRoot/
        replace/                2. git add  .       replace/
        merge/      ----------> 3. git push  ---->  merge/
                                4. deploy pipeline
                                   copies YAML into
                                   target's Files volume
```

Predicates (configured per-mode) select what gets serialized. The Replace mode is
for data the developer owns (shop definitions, VAT groups, item types). The Merge
mode is for data the customer owns after first run (pages, product catalog). Both
modes sit in the same config and run through the same pipeline — they differ only
in conflict strategy and output subfolder.

## Documentation map

| Topic | Page |
|-------|------|
| Install, first serialize, first deserialize | [Getting started](docs/getting-started.md) |
| Mental model: predicates, GUID identity, folder layout | [Concepts](docs/concepts.md) |
| Every config key and admin UI screen | [Configuration](docs/configuration.md) |
| GitHub Actions, Azure DevOps, GitLab CI end-to-end | [CI/CD integration](docs/cicd.md) |
| Strict mode: what escalates, defaults, overrides | [Strict mode](docs/strict-mode.md) |
| Cross-environment `Default.aspx?ID=N` rewriting | [Link resolution](docs/link-resolution.md) |
| Role and group permission handling | [Permissions](docs/permissions.md) |
| `SqlTable` predicates, WHERE clauses, field filters | [SQL tables](docs/sql-tables.md) |
| Per-content-item replace/merge decisions for Swift | [Swift replace/merge analysis](docs/swift-replace-merge-analysis.md) |
| Auto-excluded runtime columns and credential caveats | [Runtime exclusions](docs/runtime-exclusions.md) |
| Common errors and remedies | [Troubleshooting](docs/troubleshooting.md) |
| Ready-made layers and editions for Swift / headless / DAP | [Truvio.Commerce.Distribution](https://github.com/justdynamics/Truvio.Commerce.Distribution) |

## CI/CD teaser

The intended flow is: serialize on source, commit YAML, deploy, deserialize on target.
A minimal GitHub Actions job that applies a baseline on deploy:

```yaml
- name: Apply baseline to target
  env:
    DW_HOST: ${{ secrets.DW_HOST }}
    DW_API_KEY: ${{ secrets.DW_API_KEY }}
  run: |
    # Deploy has already copied YAML into the target's Files volume.
    # Strict mode makes any warning (unresolvable link, missing template,
    # schema drift) fail the job.
    curl -f -X POST "$DW_HOST/Admin/Api/Deserialize?mode=replace&strictMode=true" \
      -H "Authorization: Bearer $DW_API_KEY"
```

Complete pipelines for GitHub Actions, Azure DevOps, and GitLab CI — including
secret management, pre-commit link sweeps, and the Merge-vs-Replace split — are in
[`docs/cicd.md`](docs/cicd.md).

## Supported environments

- .NET 8.0
- Truvio Commerce 10.23.9 or newer
- SQL Server (via the Truvio Commerce data layer)
- YamlDotNet 13.7.1

## Project status

Shared with partners as open source (beta) — usable today, not a fully productized
offering. Install from a [release](https://github.com/justdynamics/Truvio.Commerce.Serializer/releases)
or build from source, validate against your own solution, and read the docs; there
is no SLA or formal support channel. Issues and PRs are welcome.

Quality gates: the full Swift 2.2 round-trip — YAML-only sources onto a blank
database, including a translated website language layer — runs green end to end
via `tools/e2e/full-clean-roundtrip.ps1`, alongside 890 unit tests and integration
tests that require a live DW host.

The API surface (Management API commands, predicate shape, YAML format) is stable
for the current release line. Version 1.0.0-beta adds the `ownership` header to
the YAML format and renames the Management API commands; the pre-1.0.0-beta
names stay callable as deprecated aliases through the beta and are removed in
the 1.0.0 release. Config schema and runtime-exclusion defaults may still
evolve before 1.0.

## Upgrading to 1.0.2-beta

Version 1.0.2-beta fixes a data-loss bug in the SqlTable and Area writes
([#18](https://github.com/justdynamics/Truvio.Commerce.Serializer/issues/18)).
A NULL column written in the same row after an empty string was stored as
that empty string, so SQL Server converted it: `1900-01-01` in datetime
columns, `0` in numeric columns, `''` in text columns. An empty string written
after a NULL was stored as NULL. NULLs are now written as the `NULL` literal.

The YAML format is unchanged; no re-serialize is needed. Rows already written
by an earlier version keep their wrong values, and a Merge run does not
repair a `1900-01-01` date because Merge only fills unset columns. Rebuild
the affected tables from a clean database, or deserialize them with the rows
in Replace ownership.

## Upgrading to 1.0.1-beta

Version 1.0.1-beta adds `PackageUnzip` and removes the single-package admin UI.

- `POST /Admin/Api/PackageUnzip` unzips a zip that is already on the host into
  `SerializeRoot/{mode}/`, replacing that folder; `Deserialize` then applies it.
  Get the zip onto the host with the standard file upload
  (`POST /Admin/Api/Upload`, into `/Files/System/Serializer/Upload/`). It
  accepts a mode tree zip (`{mode}-manifest.json` at the root) or a
  `PackageDownload` zip (pass `AreaId`). See
  [Configuration](docs/configuration.md#packages).
- Removed: the `SerializerUploadRoot` command (use `PackageUnzip`), the
  Download Package and Upload Package actions in the content tree and on the
  page edit screen, the Import to database action on zip files in the file
  manager, the Deserialize from zip screen, and the Permissions group on the
  Serialize settings screen. The `DeserializeFromZip` and
  `DeserializeUploadedZip` routes are gone with the screens; unzip the package
  and call `Deserialize` instead.
- `PackageDownload` and its `SerializeSubtree` alias are unchanged. Grants
  stored for the package permission keys keep applying: the download key gates
  `PackageDownload`, the upload key gates `PackageUnzip`.

## Upgrading to 1.0.0-beta

Version 1.0.0-beta renames the Management API commands and adds an ownership
header to the YAML format. The theme: the config is the boundary, not the
program; callers can now drive the serializer per call with an inline scope
instead of only through the saved predicate list.

| Old name | New name | Route |
|----------|----------|-------|
| `SerializerSerialize` | `Serialize` | `POST /Admin/Api/Serialize` |
| `SerializerDeserialize` | `Deserialize` | `POST /Admin/Api/Deserialize` |
| `SerializeSubtree` | `PackageDownload` | `POST /Admin/Api/PackageDownload` |

The old names still work as deprecated aliases through the beta: same
parameters, same binding, same status codes, same work. `SerializerSerialize`
and `SerializerDeserialize` append a sentence to the response `Message`
(for example, `Deprecated: 'SerializerSerialize' is an alias of 'Serialize'
and is removed in the 1.0.0 release. Call 'Serialize' instead.`) and log a
`DEPRECATED:` line, which strict mode does not escalate. `SerializeSubtree`
returns the same zip response with no added notice, since a file response
carries no message. All three aliases are removed in the 1.0.0 release; call
the new names instead.

Re-serialize with 1.0.0-beta to add the `ownership` header to existing YAML.
Documents written by a 0.9.x serializer have no header and run with the
config predicate's mode as a fallback. A 0.9.x serializer reading 1.0.0-beta
SqlTable row files sees `ownership` as an unknown column (a schema-drift
warning, which strict mode turns into a failure), so upgrade the app on every
environment together.

## Links

- Source: <https://github.com/justdynamics/Truvio.Commerce.Serializer>
- Issue tracker: <https://github.com/justdynamics/Truvio.Commerce.Serializer/issues>
- License: [MIT](LICENSE)
