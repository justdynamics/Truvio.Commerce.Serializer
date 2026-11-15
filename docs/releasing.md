# Releasing

How a Serializer release is cut, and the evidence it needs before it reaches a demo or a
customer host.

## Why unit tests are not enough

The unit and integration tests prove the engine's decisions. They do not prove what a
delivery does to a real database, and that is where the expensive regressions showed up:

- #30 and #32 were only visible on a live strict-mode delivery.
- A Merge redelivery re-mapped page links that already pointed at the right page, on every
  run: the header navigation root moved from page 52 to 54 to 56 over three deliveries and
  the megamenu broke. The first delivery was correct; only the repeat showed it.
- #71, #72 and #73 (1.1.0-beta) came from a second delivery of an unchanged folder and a row
  diff, after the release had passed its unit tests.

A demo is delivered again and again (reprove, restamp, reconverge), so "the second delivery
changes nothing" is as much a release property as "the first delivery succeeds".

## The rule

A release whose diff since the previous tag touches `src/` (the engine) carries a **live
strict redelivery** as evidence in its release PR:

```powershell
git diff --stat v<previous>..HEAD -- src/   # non-empty: the rule applies
```

1. Build the DLL from the release commit and deploy it to a scratch host (a copy of a demo
   database is the best target: it has the content the engine meets in practice). Never run
   it against a customer or demo host that people are using.
2. Stage the delivery folders on that host as a real delivery would.
3. Run the check:

   ```powershell
   pwsh tools/e2e/Test-Redelivery.ps1 -BaseUrl https://<host> -Token $env:DW_API_KEY `
       -SqlServer '<server>' -Database '<database>' `
       -SerializerDll <host>/bin/Truvio.Commerce.Serializer.dll
   ```

   It delivers the folders strict, then delivers them twice more, and fails when a later
   delivery answers anything but `status: ok`, adds or removes a row, or changes a link or any
   other column of `Area`, `Page`, `Paragraph`, `GridRow` or an `ItemType_*` table. Add the
   tables of the configured SqlTable predicates with `-ExtraTables`. Details:
   [`tools/e2e/README.md`](../tools/e2e/README.md#strict-redelivery-check).
4. Put the result in the release PR: `Disposition`, the engine `productVersion` and `sha256`
   from `summary.json`, the host's Dynamicweb version, and the per-delivery totals. Attach or
   quote `diff.md` when it lists ignored-column changes.

`Disposition: FAIL` blocks the release. Fix the engine, or, when the change is a known and
filed behaviour, name the issue in the release PR and the CHANGELOG's Known behaviour section.

### Known exceptions

- Update timestamps (`PageUpdatedDate`, `ParagraphUpdatedDate`, `GridRowUpdatedAt`) are
  rewritten by a no-op Replace until #71 is fixed. The check reports them and does not fail
  on them. Once #71 is fixed, run with `-IgnoreColumns @()` and drop this exception.
- `UrlPath` reports one update on every run until #73 is fixed; it is a SqlTable, so it is
  only compared when passed in `-ExtraTables`.

A release that only touches docs, tools or tests needs no live delivery; say so in the PR.

## Cutting the release

1. Bump `<Version>` in `src/Truvio.Commerce.Serializer/Truvio.Commerce.Serializer.csproj` and
   move the CHANGELOG `Unreleased` entry under the new version.
2. Run `dotnet test tests/Truvio.Commerce.Serializer.Tests --configuration Release`.
3. Run the live strict redelivery above when the rule applies.
4. Open the release PR (`release/<version>`) with the test count and the redelivery evidence.
5. After merge, tag `v<version>` on the merge commit. `publish.yml` checks the tag against
   `<Version>`, packs, and creates the GitHub release.
