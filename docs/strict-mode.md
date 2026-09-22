# Strict mode

Strict mode converts recoverable warnings into a single end-of-run failure.
It is the mechanism that makes the serializer safe to wire into CI/CD: a
deploy that would have silently logged "unresolvable page ID 3421" instead
fails the pipeline and blocks promotion.

## Table of contents

- [What strict mode does](#what-strict-mode-does)
- [What escalates](#what-escalates)
- [Entry-point defaults](#entry-point-defaults)
- [Override precedence](#override-precedence)
- [Quarantining a warning class](#quarantining-a-warning-class)
- [What a failing run looks like](#what-a-failing-run-looks-like)
- [Adding a new cache service](#adding-a-new-cache-service)
- [Known limits](#known-limits)

## What strict mode does

`StrictModeEscalator` sits on the deserialize hot path. Every `WARNING:`
line flowing through the orchestrator's log wrapper is captured. At end
of run, `AssertNoWarnings()` fires. In lenient mode, this is a no-op —
warnings logged, execution continues. In strict mode, any captured warning
throws `CumulativeStrictModeException` listing every warning verbatim.
The Management API returns `Error`; the command's HTTP status maps to
non-2xx; `curl -f` fails the pipeline.

Two code paths feed the escalator:

- **Direct escalation.** Call sites with specific context call
  `escalator.Escalate(msg)`. The escalator logs and records.
- **Log wrapper capture.** The orchestrator wraps the log sink. Any
  string starting with `WARNING:` emitted anywhere in the pipeline is
  recorded via `escalator.RecordOnly(msg)`. This guarantees coverage
  without every warning site knowing about the escalator.

Recorded warnings are capped at 10,000 per run (DoS guard). Beyond the
cap, warnings still log but stop accumulating; the end-of-run assertion
still throws because at least one warning is recorded.

## What escalates

Every warning class that flows through the deserialize pipeline is
subject to strict-mode escalation. The following are the recurring
sources, with the text the log emits:

| Source | Warning text |
|--------|--------------|
| `InternalLinkResolver` | `WARNING: Unresolvable page ID N in link` |
| `InternalLinkResolver` / `SqlTableWriter` | `WARNING: Unresolvable page ID N in int column` |
| `InternalLinkResolver` | `WARNING: Unresolvable paragraph ID N in anchor link` |
| `TargetSchemaCache` | `WARNING: source column [T].[C] not present on target schema — skipping` |
| `TemplateAssetManifest` | `WARNING: template 'T' not found at Files/Templates/T` (missing page-layout / grid-row / item-type template) |
| `SqlTableProvider` | `WARNING: Could not re-enable FK constraints for [T]: ...` (FK orphan row blocks constraint re-enable) |
| `SerializerOrchestrator` | `WARNING: Skipping predicate '...' — no provider for type '...'` (unknown `providerType`) |
| `SerializerOrchestrator` | `WARNING: Skipping predicate '...' — validation failed: ...` (predicate-level validation rejection) |
| `SerializerOrchestrator` | `WARNING: Cache invalidation failed for predicate '...': ...` (service throws during `ClearCache`) |
| `SerializerOrchestrator` | `WARNING: Predicate '...' declares N service cache(s) but no CacheInvalidator is wired — caches will NOT be cleared` |
| `SerializerOrchestrator` | `WARNING: Schema sync failed for predicate '...': ...` |
| `ContentDeserializer` | `Warning: Area with ID N not found. Skipping entry '...'` (the entry's target area is absent AND the serialized area carries no properties to create it from — matched case-insensitively like every other `WARNING` line). An absent area that DOES carry properties is created by a real run and reported as `[DRY-RUN] CREATE area N` by a dry run, so it never reaches this warning in either. |
| `ContentDeserializer` | `WARNING: Could not create area Item: ...` |
| `ContentDeserializer` | `WARNING: GridRow Item creation failed: ...` |
| `ContentDeserializer` | `WARNING: Could not load PropertyItem for page {GUID}` (dangling `PagePropertyItemId`) |
| `ContentDeserializer` | `WARNING: Could not load ItemEntry for type=..., id=...` |
| `ContentSerializer` | `WARNING: acknowledged orphan ID N in ... (context)` |
| `ContentProvider` | `WARNING: Could not build source→target page map after Content deserialize: ...` |
| `ContentDeserializer` | `WARNING: Could not read template manifest: ...` |

Unresolvable cache service names are a different class — they fail at
*config load*, before strict mode is evaluated. The message names the
predicate and lists the eighteen supported cache names. Add a new entry
to `DwCacheServiceRegistry.cs` and rebuild to extend the set.

## Entry-point defaults

The default strict-mode value depends on who called the serializer.
`StrictModeResolver.Resolve(entryPoint, configValue, requestValue)`
encodes the defaults in code:

| Entry point | Default | Rationale |
|-------------|---------|-----------|
| CLI commands (`Cli`) | **on** | CI/CD target — must fail loud on drift |
| Management API (`Api`) | **on** | Same CI/CD target as CLI |
| Admin UI action buttons (`AdminUi`) | **off** | Interactive exploration — warnings are informational |

The `DeserializeCommand.IsAdminUiInvocation` flag switches the
entry point from `Api` to `AdminUi`. Admin UI screens set it; Management
API callers and CLI never do.

## Override precedence

Three sources can set the strict-mode value. Precedence from highest to lowest:

1. **Request parameter.** The `StrictMode` property on the command (set via
   JSON body `{"StrictMode": true}` or query string `?strictMode=true`).
2. **Config value.** The top-level `strictMode` field in
   `Serializer.config.json`.
3. **Entry-point default.** The table above.

Concretely:

```
requestValue.HasValue  -> use requestValue
else configValue.HasValue -> use configValue
else entry-point default
```

Two useful patterns:

- **"Strict everywhere unless I override."** Set `config.strictMode: true`.
  Every call runs strict, including admin UI. Interactive use passes
  `?strictMode=false` for ad-hoc exploration.
- **"Default behavior but force strict for this pipeline run."** Leave
  `config.strictMode: null` (or omit the field). Pipelines pass
  `?strictMode=true` on every call; the admin UI stays lenient by default.

## Quarantining a warning class

Strict mode is deliberately blunt: any escalated warning fails the run.
For one class — **unresolvable links** — that bluntness is out of
proportion. An internal link whose target page is not on target is
cosmetic on the one row that carries it; the row still deserializes.
Failing the pass on it turns a single bad link into a total content
outage for every row the pass wrote.

A run may therefore **quarantine** that class. Quarantined warnings are:

- logged exactly as before (the emitting call site is unchanged),
- collected into a `QUARANTINED:` block at end of run listing each one,
- surfaced on `OrchestratorResult.QuarantinedWarnings` and appended to
  the command's result message,
- **excluded** from `AssertNoWarnings()` — they do not fail the pass.

Quarantine is opt-in and off by default: a pipeline that asks for strict
mode keeps failing on every class unless it names one to quarantine.
Every other class (missing templates, schema drift, FK orphans, cache
failures) still fails the run regardless.

```bash
curl -f -X POST \
  "https://host/Admin/Api/Deserialize?mode=merge&strictMode=true&quarantineUnresolvableLinks=true"
```

Or via the JSON body: `{"Mode":"merge","StrictMode":true,"QuarantineUnresolvableLinks":true}`.

The classes and their matching text live in `StrictModeWarningClass`:

| Class constant | Matches |
|----------------|---------|
| `unresolvable-link` | `Unresolvable page ID N in link`, `Unresolvable paragraph ID N in anchor link` |

A quarantined run looks like this:

```
[content/area-1] Succeeded: 283 created, 0 updated
QUARANTINED: 1 warning(s) were quarantined per-item and did NOT fail this pass (strict-mode quarantine is active for their class):
  -   WARNING: Unresolvable page ID 83 in link
```

Treat the block as a work item, not as a pass: fix the source data (see
[`link-resolution.md`](link-resolution.md)) or acknowledge the id via the
predicate's `acknowledgedOrphanPageIds`. Quarantine buys the run; it does
not fix the link.

## What a failing run looks like

A strict-mode deserialize that hits two escalated warnings produces a log like this:

```
=== Serializer Deserialize (API) started [mode: Replace] ===
=== Strict mode: True (entry-point: Api) ===
Loading predicate 'Content - Swift 2 (full baseline as shipped)'
Loading predicate 'EcomCountries'
...
  WARNING: Unresolvable page ID 3421 in link
  WARNING: template 'eCom_Catalog' not found at Files/Templates/eCom_Catalog.cshtml
...
ERROR: Strict mode: 2 warning(s) escalated to failure:
  - Unresolvable page ID 3421 in link
  - template 'eCom_Catalog' not found at Files/Templates/eCom_Catalog.cshtml
```

The Management API response:

```
HTTP/1.1 500 Internal Server Error  (or Error status mapped by the framework)

Deserialization failed: Strict mode: 2 warning(s) escalated to failure:
  - Unresolvable page ID 3421 in link
  - template 'eCom_Catalog' not found at Files/Templates/eCom_Catalog.cshtml
```

The pipeline step fails. The log upload step (if configured) still runs in
`if: always()` / `when: always` blocks so the full context is available
for debugging.

To diagnose:

1. **Unresolvable page ID.** The source YAML baseline references a page
   that is not in the baseline. Either include the target page in a
   Content predicate's `path`, or remove the reference from the source
   before re-serializing. If the source data is known-broken and can't be
   cleaned in time, add the ID to the predicate's
   `acknowledgedOrphanPageIds` list (escape hatch). See
   [`link-resolution.md`](link-resolution.md).
2. **Missing template.** The template file is absent from
   `Files/Templates/`. Deploy the template file alongside the DLL, or
   remove the reference from the source page. Templates are filesystem
   state, not DB state — see
   [`troubleshooting.md`](troubleshooting.md#template-t-not-found-at-filestemplatest).
3. **Schema drift.** A source column doesn't exist on the target table.
   Align DW NuGet versions between source and target hosts, or drop the
   column on the source. See
   [`troubleshooting.md`](troubleshooting.md#source-column-tc-not-present-on-target-schema--skipping).
4. **FK re-enable failure.** An orphan row references a parent that
   doesn't exist. Clean the source DB; the Swift 2.2 reference case is
   fixed by `tools/swift22-cleanup/06-delete-orphan-ecomshopgrouprelation.sql`.
5. **Cache invalidation failure.** The `serviceCaches` entry points at a
   service that's throwing during `ClearCache`. Check the DW host log for
   the underlying exception.

## Adding a new cache service

If config-load fails with:

```
Configuration is invalid — ServiceCaches validation failed:
  - predicates 'EcomSomething': cache service 'Dynamicweb.Ecommerce.New.XService'
    is not in DwCacheServiceRegistry.
    Supported (18 total): AreaService, CountryRelationService, CountryService, ...
```

You need to extend the registry. Open
`src/Truvio.Commerce.Serializer/Infrastructure/DwCacheServiceRegistry.cs` and
append an entry to the `Entries` array:

```csharp
new CacheClearEntry(
    "SomethingService",
    "Dynamicweb.Ecommerce.New.XService",
    () => ClearCacheOf<Dynamicweb.Ecommerce.New.XService>()),
```

Rules:

- **No reflection at runtime.** Every entry is a compile-time-typed direct
  call. If the DW service's type changes name or is removed upstream, the
  build fails here, which is the design intent.
- **Short name AND full type name.** Both are keys into the lookup map.
  The Swift 2.2 baseline config uses fully-qualified names; admin UI
  screens prefer short names. Resolution is case-insensitive.
- **Prefer the static locator.** For services with a
  `Dynamicweb.Ecommerce.Services.X` static accessor, use that directly —
  it mirrors the canonical DW10 access pattern. Only fall back to
  `ClearCacheOf<T>()` when no locator exists (the resolver goes through
  `DependencyResolver.Current` and requires the service to implement
  `ICacheStorage`).

Rebuild the DLL, redeploy, re-run config-load. The new service is now
referenceable from `serviceCaches` in config.

## Known limits

- **Warning cap.** The escalator records at most 10,000 warnings per run.
  Beyond the cap, warnings still log but aren't added to the end-of-run
  exception message. A run that hits the cap almost certainly has a
  systemic issue (schema-mismatched target, broken predicate); fix the
  root cause rather than the cap.
- **Lenient mode is not "silent".** Warnings still log at `info` level
  (or lower if `logLevel` suppresses them). Review logs periodically even
  when strict is off — a slow accumulation of warnings is a drift signal.
- **Config-load errors are not strict-mode.** Identifier validation, WHERE
  clause validation, service-cache resolution, and JSON shape checks fail
  immediately at `ConfigLoader.Load()`, regardless of `strictMode`. Those
  errors are always fatal.
- **One quarantinable class.** Per-class control exists only for
  `unresolvable-link` (see
  [Quarantining a warning class](#quarantining-a-warning-class)). Every
  other class is all-or-nothing: you cannot say "escalate link warnings
  but not template warnings." If you need finer control, either clean the
  source (the intended fix) or run lenient and post-process the log.
- **Links still serialize as raw ids.** Quarantine bounds the blast
  radius of an unresolvable link; it does not make links portable. Link
  fields that store a raw source page id remain non-remappable across
  environments. Serializing links as GUID-based refs (like every other
  page reference in the trees) is the real fix and is a serialized-format
  change — tracked separately.

## See also

- [CI/CD integration](cicd.md) — pipeline usage patterns for strict mode
- [Link resolution](link-resolution.md) — `Unresolvable page ID` warning class
- [Configuration](configuration.md) — `strictMode` config field
- [Troubleshooting](troubleshooting.md) — mapping common error messages to fixes
