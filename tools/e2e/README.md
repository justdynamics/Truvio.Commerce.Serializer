# Swift 2.2 → CleanDB Autonomous E2E Pipeline

**Entry point:** `tools/e2e/full-clean-roundtrip.ps1`

Fully-unattended execution of the Phase 38.1 gap-closure pipeline: restore
Swift-2.2 from bacpac, apply cleanup scripts 01..09, run the 4 API calls,
smoke-test, and assert preservation. Closes the Phase 38.1 carry-forwards
(B.5.1, B.4.1, B.3.1, GRID-01) end-to-end under `strictMode: true`.

## Prerequisites

- PowerShell 7+ (`pwsh`)
- SQL Server 2019+ reachable at `localhost\SQLEXPRESS` (or via `-SqlServer`)
- `sqlcmd` on PATH (ships with SQL Server client tools)
- The `SqlServer` PowerShell module (`Invoke-Sqlcmd`), used by the Step 16 smoke tool
  (`tools/smoke/Test-BaselineFrontend.ps1`); without it the smoke tool exits 2:
  `Install-Module -Name SqlServer -Scope CurrentUser -Force -AcceptLicense -AllowClobber`
- Truvio Commerce Swift-2.2 and Swift-CleanDB host projects under
  `C:\Projects\Solutions\swift.test.forsync\` (or pass `-SwiftHostPath` /
  `-CleanDbHostPath` to override). Hosts must already be `dotnet build`-able
  (net10.0).
- `tools/swift2.2.0-20260129-database.zip` — the bacpac source for Swift-2.2.
- Truvio.Commerce.Serializer repo on disk; the pipeline runs `dotnet build` for
  `src/Truvio.Commerce.Serializer/Truvio.Commerce.Serializer.csproj` automatically.

The pipeline auto-installs `sqlpackage` via
`dotnet tool install --global microsoft.sqlpackage` if not found in the
standard locations (Program Files Microsoft SQL Server DAC, `$HOME/.dotnet/tools`).
If the install fails the pipeline throws with install instructions.

## Run

```powershell
pwsh tools/e2e/full-clean-roundtrip.ps1
```

Typical invocation from repo root. For a debugging re-run without the bacpac
restore step:

```powershell
pwsh tools/e2e/full-clean-roundtrip.ps1 -SkipBacpacRestore
```

## What it does (high-level)

| Step | Action | Asserts |
|-----:|--------|---------|
| 1  | Stop both DW hosts (taskkill by port 54035 + 58217)                  | ports no longer LISTENING |
| 2  | Detect/install sqlpackage                                            | `sqlpackage.exe` available on PATH or via dotnet-global |
| 3  | Unzip bacpac zip + `sqlpackage /Action:Import` -> Swift-2.2          | sqlpackage exit 0 |
| 4  | (deferred) Administrator password check runs after Step 7 host boot | token auth at Step 7 must return 200 |
| 5  | Apply cleanup scripts 01..09 on Swift-2.2                            | each script COMMIT, no ABORT/ROLLBACK |
| 6  | Build + deploy `Truvio.Commerce.Serializer.dll`                           | md5 matches across source + both hosts |
| 7  | Start Swift-2.2 host                                                 | `/Admin/` returns 200/301/302/401 within 180s AND token auth returns Administrator token |
| 8  | Purge CleanDB (`tools/purge-cleandb.sql`)                            | COMMIT, no ROLLBACK |
| 9  | Apply `cleandb-align-schema.sql`                                     | 17 ALTER or SKIP log lines |
| 10 | Start CleanDB host                                                   | `/Admin/` ready within 180s |
| 11 | POST SerializerSerialize?mode=replace                                 | HTTP 200, zero escalations |
| 12 | POST SerializerSerialize?mode=merge                                   | HTTP 200, zero escalations |
| 13 | Mirror Swift-2.2's SerializeRoot -> CleanDB's filesystem             | `replace/` + `merge/` dirs mirrored (Phase 38.1-01 Dev 1) |
| 14 | POST SerializerDeserialize?mode=replace                               | HTTP 200, zero escalations |
| 15 | POST SerializerDeserialize?mode=merge                                 | HTTP 200, zero escalations |
| 16 | Smoke tool (`Test-BaselineFrontend.ps1`)                             | exit 0 AND non-vacuous (no 'Nothing to test') |
| 17 | `SELECT COUNT(*) FROM EcomProducts` on both DBs                      | Swift-2.2 = 2051 AND CleanDB = 2051 |
| 18 | Orphan YAML assertion                                                | `SerializeRoot/replace/_sql/EcomShopGroupRelation/GROUP253$$SHOP19.yml` absent |
| 19 | Stop both hosts                                                      | clean shutdown via taskkill |
| 20 | Emit `summary.json` in run dir                                       | `Disposition: CLOSED` |

## Evidence

Each run creates a timestamped directory:

```
tools/e2e/runs/<yyyyMMdd-HHmmss>/
```

Expected contents:

- `pipeline.log` — step-by-step timestamped trace of the pipeline itself
- `sqlpackage-install.log` — only present when sqlpackage was auto-installed
- `bacpac-drop.log`, `bacpac-import.log` — Step 3
- `bacpac/` — extracted bacpac file
- `cleanup-00-backup.log` ... `cleanup-09-fix-misconfigured-property-pages.log` — Step 5
- `dotnet-build.log`, `dll-md5.txt` — Step 6
- `host-swift22.log`, `host-swift22.log.err` — Step 7 stdout/stderr
- `purge-cleandb.log` — Step 8
- `schema-align.log` — Step 9
- `host-cleandb.log`, `host-cleandb.log.err` — Step 10
- `serialize-replace.log`, `serialize-merge.log` — Steps 11-12
- `deserialize-replace.log`, `deserialize-merge.log` — Steps 14-15
- `smoke.log` — Step 16
- `summary.json` — Step 20 (only present on full success)

## Exit codes

| Code | Meaning |
|-----:|---------|
| `0` | Full success — all 4 API calls HTTP 200, smoke non-vacuous, EcomProducts preserved 2051→2051, no orphan YAMLs. Disposition `CLOSED`. |
| `1` | Any step failed (see run directory for the precise failure log). Pipeline threw with a descriptive message. |
| `2` | Prerequisite missing (sqlpackage install failed, DLL build failed, host paths invalid). Rare — caught at Step 2 or Step 6. |

## Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `-SqlServer`         | `localhost\SQLEXPRESS` | SQL Server instance |
| `-SwiftDb`           | `Swift-2.2`            | Swift-2.2 database name |
| `-CleanDb`           | `Swift-CleanDB`        | CleanDB database name |
| `-SwiftHostPath`     | `C:\Projects\Solutions\swift.test.forsync\Swift2.2\Dynamicweb.Host.Suite`   | Swift-2.2 host project dir |
| `-CleanDbHostPath`   | `C:\Projects\Solutions\swift.test.forsync\Swift.CleanDB\Dynamicweb.Host.Suite` | CleanDB host project dir |
| `-SwiftHostUrl`      | `https://localhost:54035` | Swift-2.2 public URL |
| `-CleanDbHostUrl`    | `https://localhost:58217` | CleanDB public URL |
| `-AdminUser`         | `Administrator`         | DW admin username for token auth |
| `-AdminPassword`     | `$env:DW_ADMIN_PASSWORD` | DW admin password for token auth (required) |
| `-SkipBacpacRestore` | (unset)                 | debugging flag — skip Step 3 bacpac restore; assume Swift-2.2 already restored |

## Fallback — manual password reseed

If the bacpac restore leaves Administrator without the expected password
(the one you pass via `-AdminPassword` / `DW_ADMIN_PASSWORD`) and the pipeline's
Step 7 token-auth check throws 401, the pipeline halts with a message pointing
here. Resolution:

1. Open SSMS against `[Swift-2.2]`.
2. Run a manual `UPDATE AccessUser SET AccessUserPassword = ...` for the
   Administrator row with a known-good PBKDF2 hash of that password. The
   exact column shape (`AccessUserPassword` / `AccessUserPasswordSalt`,
   iterations, hash format) is DW-version-specific — if unsure, the simplest
   path is to use the DW admin UI "Reset password" flow once, then export the
   resulting hash for future pipeline runs.
3. Re-run the pipeline with `-SkipBacpacRestore` so it picks up the now-
   authenticated DB:

   ```powershell
   pwsh tools/e2e/full-clean-roundtrip.ps1 -SkipBacpacRestore
   ```

This fallback exists because inlining a PBKDF2 reseed in PowerShell is brittle
across DW versions. The pipeline prefers to throw loudly over silently
fake-succeeding.

## Troubleshooting

**"sqlpackage still not present after install attempt"**
The auto-install via `dotnet tool install --global microsoft.sqlpackage` put the
binary somewhere unexpected. Install manually from
<https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage-download> to
`C:\Program Files\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe`.

**"Host bin dir does not exist"**
The host projects have not been built yet. Open each host's solution, run
`dotnet build -c Debug`, then re-run the pipeline.

**"Script NN failed (exit N)" or "hit an ABORT / ROLLBACK path"**
A cleanup script's pre-count assertion failed (e.g. script 06 expected exactly
1 SHOP19 row but found more/fewer). Inspect the script's log in the run
directory to see the concrete count and compare to the script's expectations.

**"CleanDB EcomProducts expected 2051, got N"**
C.1 preservation regression — deserialize dropped products. This is a serious
bug and should block release; gather the `deserialize-merge.log` + per-predicate
counts before re-running.

## Related

- Investigation artifact: `.planning/phases/38.1-close-phase-38-deferrals/38.1-02-orphan-investigation.md`
- Cleanup script catalog: `tools/swift22-cleanup/README.md`
- Schema-alignment script: `tools/swift22-cleanup/cleandb-align-schema.sql`
- Purge script: `tools/purge-cleandb.sql`
- Smoke tool: `tools/smoke/Test-BaselineFrontend.ps1`
- Phase 38.1 recipe provenance: `.planning/phases/38.1-close-phase-38-deferrals/38.1-01-e2e-results.md`
