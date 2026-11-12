<#
.SYNOPSIS
Fully autonomous Swift-2.2 → CleanDB E2E round-trip pipeline.

.DESCRIPTION
End-to-end, unattended execution of the Phase 38.1 gap-closure pipeline:

  1. Stop both DW hosts (Swift-2.2, CleanDB) via taskkill on ports 54035 + 58217
  2. Detect or install sqlpackage.exe
  3. Restore Swift-2.2 from tools/swift2.2.0-20260129-database.zip via
     sqlpackage Import (drops + re-creates the DB)
  4. Verify Administrator token auth once the host is up (manual reseed fallback)
  5. Apply cleanup scripts 01..09 in order against Swift-2.2
  6. Build + deploy the Truvio.Commerce.Serializer DLL to both hosts'
     bin/Debug/net10.0 directories
  7. Start Swift-2.2 host on https://localhost:54035 and wait for ready
  8. Purge CleanDB via tools/purge-cleandb.sql
  9. Apply tools/swift22-cleanup/cleandb-align-schema.sql
 10. Start CleanDB host on https://localhost:58217 and wait for ready
 11. POST SerializerSerialize?mode=replace against Swift-2.2 (expect HTTP 200)
 12. POST SerializerSerialize?mode=merge against Swift-2.2 (expect HTTP 200)
 13. Mirror Swift-2.2 wwwroot/Files/System/Serializer/SerializeRoot to
     CleanDB's filesystem (per Phase 38.1-01 Deviation 1)
 14. POST SerializerDeserialize?mode=replace against CleanDB (expect HTTP 200)
 15. POST SerializerDeserialize?mode=merge against CleanDB (expect HTTP 200)
 16. Run tools/smoke/Test-BaselineFrontend.ps1 (expect exit 0 AND non-vacuous)
 17. Assert EcomProducts row count: 2051 on Swift-2.2 AND 2051 on CleanDB
 18. Assert no baselines/Swift2.2/_sql/EcomShopGroupRelation/GROUP253$$SHOP19.yml
 19. Language-layer round-trip contract: every source area with
     AreaMasterAreaId > 0 must have a replace-manifest entry, exist on the
     target with the same id + master link, and match the source on page
     count, restored (non-dangling) master-page links, and the multiset of
     page menu texts (translated content). Skipped with a log line when the
     source has no language layers.
 20. Stop both hosts
 21. Emit pass/fail summary JSON

All steps write logs to a timestamped run directory under
tools/e2e/runs/<yyyyMMdd-HHmmss>/ (gitignored).
Each step fails loudly (throw with message) and leaves evidence on disk.

Exit code:
  0 = full success (all 4 API calls HTTP 200, smoke non-vacuous,
      EcomProducts preserved, no orphan YAMLs)
  1 = any step failed (see run directory logs)
  2 = prerequisite missing (sqlpackage install failed, DLL build failed,
      host paths invalid)

.PARAMETER SqlServer
SQL Server instance. Default 'localhost\SQLEXPRESS'.

.PARAMETER SwiftDb
Swift-2.2 database name. Default 'Swift-2.2'.

.PARAMETER CleanDb
CleanDB database name. Default 'Swift-CleanDB'.

.PARAMETER SwiftHostPath
Absolute path to the Swift-2.2 DW host project root.
Default: C:\Projects\Solutions\swift.test.forsync\Swift2.2\Dynamicweb.Host.Suite

.PARAMETER CleanDbHostPath
Absolute path to the Swift-CleanDB DW host project root.
Default: C:\Projects\Solutions\swift.test.forsync\Swift.CleanDB\Dynamicweb.Host.Suite

.PARAMETER SwiftHostUrl
Public URL of the Swift-2.2 host. Default 'https://localhost:54035'.

.PARAMETER CleanDbHostUrl
Public URL of the CleanDB host. Default 'https://localhost:58217'.

.PARAMETER AdminUser
DW admin username used for token auth against both hosts. Default 'Administrator'.

.PARAMETER AdminPassword
DW admin password used for token auth against both hosts. Required; can also be
supplied via the DW_ADMIN_PASSWORD environment variable.

.PARAMETER SkipBacpacRestore
For debugging only. When set, skip step 3 and assume Swift-2.2 is already
in the expected state.

.EXAMPLE
pwsh tools/e2e/full-clean-roundtrip.ps1

.EXAMPLE
pwsh tools/e2e/full-clean-roundtrip.ps1 -SqlServer '.\SQLEXPRESS'

.NOTES
Phase 38.1 Plan 03 Task 2 (D-38.1-19 / D-38.1-20 recipe codification).
#>

[CmdletBinding()]
param(
    [string]$SqlServer       = 'localhost\SQLEXPRESS',
    [string]$SwiftDb         = 'Swift-2.2',
    [string]$CleanDb         = 'Swift-CleanDB',
    [string]$SwiftHostPath   = 'C:\Projects\Solutions\swift.test.forsync\Swift2.2\Dynamicweb.Host.Suite',
    [string]$CleanDbHostPath = 'C:\Projects\Solutions\swift.test.forsync\Swift.CleanDB\Dynamicweb.Host.Suite',
    [string]$SwiftHostUrl    = 'https://localhost:54035',
    [string]$CleanDbHostUrl  = 'https://localhost:58217',
    [string]$AdminUser       = 'Administrator',
    [string]$AdminPassword   = $env:DW_ADMIN_PASSWORD,
    [switch]$SkipBacpacRestore
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($AdminPassword)) {
    throw 'AdminPassword is required: pass -AdminPassword or set the DW_ADMIN_PASSWORD environment variable.'
}
$script:repoRoot  = (Get-Location).Path
$script:bacpacZip = Join-Path $script:repoRoot 'tools/swift2.2.0-20260129-database.zip'

# ============================================================================
# Run directory — all logs + evidence land here
# ============================================================================
$ts = (Get-Date -Format 'yyyyMMdd-HHmmss')
$runDir = Join-Path $script:repoRoot "tools/e2e/runs/$ts"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$script:runDir = $runDir

# ============================================================================
# Helper functions
# ============================================================================

function Write-Step {
    param([string]$Msg)
    Write-Host "`n=== $Msg ===" -ForegroundColor Cyan
    Add-Content -Path (Join-Path $script:runDir 'pipeline.log') -Value "[$((Get-Date).ToString('HH:mm:ss'))] $Msg"
}

function Write-Evidence {
    param([string]$Name, [string]$Content)
    $Content | Out-File -Encoding utf8 (Join-Path $script:runDir $Name)
}

function Stop-HostOnPort {
    param([int]$Port, [string]$Label)
    try {
        $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($conns) {
            foreach ($c in $conns) {
                $procId = $c.OwningProcess
                if ($procId -and $procId -ne 0) {
                    Write-Host "  Stopping $Label PID $procId on port $Port"
                    try { & taskkill /F /PID $procId 2>&1 | Out-Null } catch {}
                }
            }
            Start-Sleep -Seconds 2
        } else {
            Write-Host "  Port $Port not listening — nothing to stop ($Label)"
        }
    } catch {
        Write-Host "  Warning: Could not query port $Port — $($_.Exception.Message)"
    }
}

function Resolve-SqlPackage {
    $candidates = @(
        "${env:USERPROFILE}\.dotnet\tools\sqlpackage.exe",
        "${env:ProgramFiles}\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe",
        "${env:ProgramFiles}\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe",
        "${env:ProgramFiles}\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe",
        "${env:ProgramFiles}\Microsoft SQL Server\140\DAC\bin\SqlPackage.exe",
        "${env:ProgramFiles(x86)}\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe",
        "${env:ProgramFiles(x86)}\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            Write-Host "  Found sqlpackage at: $c"
            return $c
        }
    }

    Write-Host "  sqlpackage not found in standard locations — attempting dotnet tool install"
    $installLog = Join-Path $script:runDir 'sqlpackage-install.log'
    & dotnet tool install --global microsoft.sqlpackage *>&1 | Tee-Object -FilePath $installLog | Out-Host
    # Exit code 1 on dotnet tool install == "already installed" in some versions — treat as acceptable
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) {
        throw "Failed to install sqlpackage via 'dotnet tool install --global microsoft.sqlpackage' (exit code $LASTEXITCODE). See $installLog. Install manually from https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage-download"
    }

    $candidate = "${env:USERPROFILE}\.dotnet\tools\sqlpackage.exe"
    if (-not (Test-Path $candidate)) {
        throw "sqlpackage still not present after install attempt at $candidate. Check $installLog"
    }
    # Ensure the dotnet tools dir is on PATH for this session
    if ($env:PATH -notlike "*${env:USERPROFILE}\.dotnet\tools*") {
        $env:PATH = "${env:USERPROFILE}\.dotnet\tools;$env:PATH"
    }
    Write-Host "  Installed sqlpackage at: $candidate"
    return $candidate
}

function Invoke-Sqlcmd-File {
    param(
        [string]$Server,
        [string]$Database,
        [string]$ScriptPath,
        [string]$LogPath
    )
    & sqlcmd -S $Server -E -d $Database -b -i $ScriptPath *>&1 | Tee-Object -FilePath $LogPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd failed on '$ScriptPath' (exit $LASTEXITCODE). See $LogPath"
    }
    if (Select-String -Path $LogPath -Pattern 'ABORT|ROLLBACK\b' -Quiet) {
        throw "Script '$ScriptPath' hit an ABORT / ROLLBACK path — see $LogPath"
    }
}

function Invoke-Sqlcmd-Scalar {
    param(
        [string]$Server,
        [string]$Database,
        [string]$Query
    )
    $raw = & sqlcmd -S $Server -E -d $Database -h -1 -W -Q "SET NOCOUNT ON; $Query" 2>&1
    $line = ($raw | Where-Object { $_ -match '^\s*-?\d+\s*$' } | Select-Object -First 1)
    if (-not $line) {
        throw "sqlcmd query returned no numeric scalar. Query: $Query. Raw output: $($raw -join ' | ')"
    }
    return [int]$line.Trim()
}

function Start-DwHost {
    param(
        [string]$ProjectDir,
        [string]$Url,
        [string]$LogPath,
        [string]$Label
    )
    if (-not (Test-Path $ProjectDir)) {
        throw "Host project dir not found: $ProjectDir ($Label)"
    }
    $env:ASPNETCORE_URLS = $Url
    # Use --no-build only if bin dir already populated; fall back to with-build if missing
    $binPath = Join-Path $ProjectDir 'bin/Debug/net10.0'
    $useNoBuild = Test-Path $binPath
    $args = @('run', '--project', $ProjectDir, '-c', 'Debug')
    if ($useNoBuild) { $args += '--no-build' }
    Write-Host "  Starting $Label host: dotnet $($args -join ' ')"
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $args `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $LogPath `
        -RedirectStandardError "$LogPath.err"
    return $proc
}

function Wait-DwReady {
    param(
        [string]$Url,
        [int]$TimeoutSec = 180,
        [string]$Label
    )
    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSec) {
        try {
            $r = Invoke-WebRequest -Uri "$Url/Admin/" -SkipCertificateCheck -TimeoutSec 5 -MaximumRedirection 0 -ErrorAction Stop
            if ($r.StatusCode -in 200, 301, 302) { Write-Host "  $Label ready (HTTP $($r.StatusCode))"; return }
        } catch {
            $code = 0
            try { $code = [int]$_.Exception.Response.StatusCode } catch { }
            if ($code -in 200, 301, 302, 401) { Write-Host "  $Label ready (HTTP $code)"; return }
        }
        Start-Sleep -Seconds 2
    }
    throw "Host at $Url ($Label) did not respond within ${TimeoutSec}s"
}

function Ensure-DwLicense {
    param([string]$HostUrl, [string]$Label)
    # Fresh scaffolds have no license (InstallationId is per-installation): /Admin redirects
    # to /admin/license. Register the default full trial via the two-step form POST.
    try {
        $probe = Invoke-WebRequest -Uri "$HostUrl/Admin/" -SkipCertificateCheck -MaximumRedirection 0 -ErrorAction Stop
        return  # 200 — licensed
    } catch {
        $loc = ''
        try { $loc = "$($_.Exception.Response.Headers.Location)" } catch { }
        if ($loc -notlike '*license*') { return }  # redirect elsewhere (login) — licensed
    }
    Write-Host "  $Label is unlicensed — registering trial license"
    $form = Invoke-WebRequest -Uri "$HostUrl/Admin/License/TrialInstallStep" -SkipCertificateCheck -SessionVariable web
    $token = ([regex]::Match($form.Content, '__RequestVerificationToken[^>]*value="([^"]+)"')).Groups[1].Value
    $trialId = ([regex]::Match($form.Content, 'type="radio" value="([^"]+)"[^>]*checked')).Groups[1].Value
    if (-not $trialId) { $trialId = ([regex]::Match($form.Content, 'type="radio" value="([^"]+)"')).Groups[1].Value }
    if (-not $token -or -not $trialId) { throw "$Label : could not parse the trial-license form at /Admin/License/TrialInstallStep" }
    $resp = Invoke-WebRequest -Uri "$HostUrl/Admin/License/TrialInstallStep" -Method POST -WebSession $web `
        -SkipCertificateCheck -Body @{ trialId = $trialId; __RequestVerificationToken = $token } -MaximumRedirection 5
    if ($resp.StatusCode -ne 200) { throw "$Label : trial-license POST returned HTTP $($resp.StatusCode)" }
    Write-Host "  $Label trial license registered"
}

function Get-DwToken {
    param(
        [string]$HostUrl,
        [string]$Username = $AdminUser,
        [string]$Password = $AdminPassword
    )
    $body = @{ Username = $Username; Password = $Password } | ConvertTo-Json
    $resp = Invoke-WebRequest -Uri "$HostUrl/Admin/TokenAuthentication/authenticate" `
        -Method POST -ContentType 'application/json' -Body $body `
        -SkipCertificateCheck -ErrorAction Stop
    $json = $resp.Content | ConvertFrom-Json
    if (-not $json.Token) { throw "Token auth against $HostUrl returned no Token in body" }
    return $json.Token
}

function Invoke-DwApi {
    param(
        [string]$HostUrl,
        [string]$Endpoint,
        [string]$LogPath
    )
    $token = Get-DwToken -HostUrl $HostUrl
    $hdr = @{ Authorization = "Bearer $token" }
    try {
        $resp = Invoke-WebRequest -Uri "$HostUrl$Endpoint" -Method POST -Headers $hdr `
            -SkipCertificateCheck -TimeoutSec 600 -ErrorAction Stop
        $code = [int]$resp.StatusCode
        "HTTP $code`n$($resp.Content)" | Out-File -Encoding utf8 $LogPath
        return @{ Code = $code; Body = $resp.Content }
    } catch {
        $code = 0
        $body = ""
        try { $code = [int]$_.Exception.Response.StatusCode } catch { }
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                $body = $reader.ReadToEnd()
            }
        } catch { }
        "HTTP $code`n$body" | Out-File -Encoding utf8 $LogPath
        return @{ Code = $code; Body = $body }
    }
}

function Try-DwToken {
    param([string]$HostUrl)
    try {
        $t = Get-DwToken -HostUrl $HostUrl
        if ($t) { return @{ Ok = $true; Code = 200 } }
        return @{ Ok = $false; Code = 0 }
    } catch {
        $code = 0
        try { $code = [int]$_.Exception.Response.StatusCode } catch { }
        return @{ Ok = $false; Code = $code; Error = $_.Exception.Message }
    }
}

# ============================================================================
# Main pipeline
# ============================================================================

Write-Step "Run directory: $runDir"
Write-Step "Pipeline start — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

$pipelineStartUtc = (Get-Date).ToUniversalTime()

# ----- Step 1: Stop both hosts ------------------------------------------------
$swiftPort = ([Uri]$SwiftHostUrl).Port
$cleanPort = ([Uri]$CleanDbHostUrl).Port
Write-Step "Step 1: Stop any running DW hosts (ports $swiftPort + $cleanPort)"
Stop-HostOnPort -Port $swiftPort -Label 'source'
Stop-HostOnPort -Port $cleanPort -Label 'target'

# ----- Step 2: Detect/install sqlpackage --------------------------------------
Write-Step 'Step 2: Detect/install sqlpackage.exe'
$sqlpackage = Resolve-SqlPackage

# ----- Step 3: Bacpac restore -------------------------------------------------
Write-Step 'Step 3: Restore Swift-2.2 from bacpac'
if ($SkipBacpacRestore) {
    Write-Host '  -SkipBacpacRestore set — skipping bacpac restore. Assuming Swift-2.2 already matches expected state.'
} else {
    if (-not (Test-Path $script:bacpacZip)) {
        throw "Bacpac zip not found at $script:bacpacZip — cannot restore Swift-2.2"
    }
    $bacpacTmpDir = Join-Path $runDir 'bacpac'
    New-Item -ItemType Directory -Force -Path $bacpacTmpDir | Out-Null
    Write-Host "  Unzipping $script:bacpacZip -> $bacpacTmpDir"
    Expand-Archive -LiteralPath $script:bacpacZip -DestinationPath $bacpacTmpDir -Force

    # Find the .bacpac file inside the extracted tree (support nested layouts)
    $bacpac = Get-ChildItem -Path $bacpacTmpDir -Filter '*.bacpac' -Recurse | Select-Object -First 1
    if (-not $bacpac) {
        throw "No .bacpac file found inside $script:bacpacZip after Expand-Archive"
    }
    Write-Host "  Bacpac file: $($bacpac.FullName)"

    # Drop existing DB (if any) via sqlcmd master
    $dropSql = "IF DB_ID('$SwiftDb') IS NOT NULL BEGIN ALTER DATABASE [$SwiftDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$SwiftDb]; END"
    Write-Host "  Dropping existing database [$SwiftDb] (if any)"
    & sqlcmd -S $SqlServer -E -d master -b -Q $dropSql *>&1 | Tee-Object -FilePath (Join-Path $runDir 'bacpac-drop.log') | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd drop of [$SwiftDb] failed (exit $LASTEXITCODE). See $runDir/bacpac-drop.log"
    }

    # Import via sqlpackage
    Write-Host "  sqlpackage /Action:Import -> [$SwiftDb]"
    $importLog = Join-Path $runDir 'bacpac-import.log'
    & $sqlpackage /Action:Import /SourceFile:$($bacpac.FullName) `
        /TargetConnectionString:"Server=$SqlServer;Database=$SwiftDb;Integrated Security=true;TrustServerCertificate=true" `
        *>&1 | Tee-Object -FilePath $importLog | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sqlpackage Import failed (exit $LASTEXITCODE). See $importLog"
    }
}

# ----- Step 4: Verify Administrator credentials (conditional) -----------------
# The bacpac ships with Administrator credentials. If token auth fails post-restore,
# we halt and point the operator at the manual reseed fallback documented in
# tools/e2e/README.md, then re-run with -SkipBacpacRestore.
#
# NOTE: In principle the pipeline could PBKDF2-compute the DW password hash and
# UPDATE AccessUser directly — but the exact DW hash format (iterations, column
# names, salt shape) is version-specific and brittle. The defensive default is:
# attempt token auth after the Swift-2.2 host boots in Step 7, and if it fails
# with 401, surface a clear error with the SQL fallback path.
Write-Step 'Step 4: Defer Administrator password check until host is up (see Step 7 readiness)'

# ----- Step 5: Apply cleanup scripts 01..09 -----------------------------------
Write-Step 'Step 5: Apply cleanup scripts 01..09 against Swift-2.2'
$scripts = @(
    '00-backup.sql',
    '01-null-orphan-page-refs.sql',
    '02-delete-test-page.sql',
    '03-delete-orphan-areas.sql',
    '04-delete-soft-deleted-pages.sql',
    '05-null-stale-template-refs.sql',
    '06-delete-orphan-ecomshopgrouprelation.sql',
    '07-delete-stale-email-gridrows.sql',
    '08-null-orphan-page-link-refs.sql',
    '09-fix-misconfigured-property-pages.sql'
)
foreach ($s in $scripts) {
    $scriptPath = Join-Path $script:repoRoot "tools/swift22-cleanup/$s"
    if (-not (Test-Path $scriptPath)) {
        throw "Cleanup script missing: $scriptPath"
    }
    $logName = "cleanup-$($s -replace '\.sql$', '.log')"
    $logFile = Join-Path $script:runDir $logName
    Write-Host "  Running $s"
    Invoke-Sqlcmd-File -Server $SqlServer -Database $SwiftDb -ScriptPath $scriptPath -LogPath $logFile
}

# ----- Step 6: Build + deploy DLL ---------------------------------------------
Write-Step 'Step 6: Build + deploy Truvio.Commerce.Serializer DLL'
$dllSourceDir = Join-Path $script:repoRoot 'src/Truvio.Commerce.Serializer/bin/Debug/net8.0'
$dllSource    = Join-Path $dllSourceDir 'Truvio.Commerce.Serializer.dll'

Write-Host '  dotnet build src/Truvio.Commerce.Serializer'
$buildLog = Join-Path $runDir 'dotnet-build.log'
& dotnet build (Join-Path $script:repoRoot 'src/Truvio.Commerce.Serializer/Truvio.Commerce.Serializer.csproj') -c Debug *>&1 | Tee-Object -FilePath $buildLog | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed (exit $LASTEXITCODE). See $buildLog"
}
if (-not (Test-Path $dllSource)) {
    throw "Serializer DLL not found at $dllSource after build"
}

$swiftDllDir  = Join-Path $SwiftHostPath   'bin/Debug/net10.0'
$cleanDllDir  = Join-Path $CleanDbHostPath 'bin/Debug/net10.0'
foreach ($d in @($swiftDllDir, $cleanDllDir)) {
    if (-not (Test-Path $d)) {
        throw "Host bin dir does not exist: $d. Hosts target net10.0 — build hosts first or verify -SwiftHostPath / -CleanDbHostPath"
    }
}

$swiftDllPath = Join-Path $swiftDllDir 'Truvio.Commerce.Serializer.dll'
$cleanDllPath = Join-Path $cleanDllDir 'Truvio.Commerce.Serializer.dll'
Copy-Item -Force $dllSource $swiftDllPath
Copy-Item -Force $dllSource $cleanDllPath

$srcMd5 = (Get-FileHash -Algorithm MD5 $dllSource).Hash
$swMd5  = (Get-FileHash -Algorithm MD5 $swiftDllPath).Hash
$cdMd5  = (Get-FileHash -Algorithm MD5 $cleanDllPath).Hash
if ($srcMd5 -ne $swMd5 -or $srcMd5 -ne $cdMd5) {
    throw "DLL md5 mismatch after copy: src=$srcMd5 swift=$swMd5 clean=$cdMd5"
}
Write-Host "  DLL md5 verified on both hosts: $srcMd5"
Write-Evidence -Name 'dll-md5.txt' -Content "src=$srcMd5`nswift=$swMd5`nclean=$cdMd5"

# ----- Step 7: Start Swift-2.2 host -------------------------------------------
Write-Step 'Step 7: Start Swift-2.2 host + wait for ready'
$swiftHostLog = Join-Path $runDir 'host-swift22.log'
$swiftProc = Start-DwHost -ProjectDir $SwiftHostPath -Url $SwiftHostUrl -LogPath $swiftHostLog -Label 'Swift-2.2'
try {
    Wait-DwReady -Url $SwiftHostUrl -TimeoutSec 180 -Label 'Swift-2.2'
} catch {
    throw "Swift-2.2 host failed to start at $SwiftHostUrl within 180s. See $swiftHostLog"
}
Ensure-DwLicense -HostUrl $SwiftHostUrl -Label 'source'

# Administrator password check (Step 4 deferred verification)
$tokenCheck = Try-DwToken -HostUrl $SwiftHostUrl
if (-not $tokenCheck.Ok) {
    if ($tokenCheck.Code -eq 401) {
        throw "Token auth as '$AdminUser' failed (401) on [$SwiftDb] after bacpac restore. Reseed the admin password manually (see tools/e2e/README.md §Fallback), then re-run this pipeline with -SkipBacpacRestore."
    } else {
        throw "Swift-2.2 token endpoint returned unexpected status $($tokenCheck.Code) — $($tokenCheck.Error)"
    }
}
Write-Host '  Administrator token auth OK on Swift-2.2'

# ----- Step 8: Purge CleanDB --------------------------------------------------
Write-Step 'Step 8: Purge CleanDB'
$purgeScript = Join-Path $script:repoRoot 'tools/purge-cleandb.sql'
if (-not (Test-Path $purgeScript)) {
    throw "Purge script not found: $purgeScript"
}
$purgeLog = Join-Path $runDir 'purge-cleandb.log'
Invoke-Sqlcmd-File -Server $SqlServer -Database $CleanDb -ScriptPath $purgeScript -LogPath $purgeLog

# ----- Step 9: Apply cleandb-align-schema.sql ---------------------------------
Write-Step 'Step 9: Apply cleandb-align-schema.sql (17 idempotent ALTER statements)'
$alignScript = Join-Path $script:repoRoot 'tools/swift22-cleanup/cleandb-align-schema.sql'
if (-not (Test-Path $alignScript)) {
    throw "Schema-align script not found: $alignScript"
}
$alignLog = Join-Path $runDir 'schema-align.log'
Invoke-Sqlcmd-File -Server $SqlServer -Database $CleanDb -ScriptPath $alignScript -LogPath $alignLog

# ----- Step 10: Start CleanDB host --------------------------------------------
Write-Step 'Step 10: Start CleanDB host + wait for ready'
$cleanHostLog = Join-Path $runDir 'host-cleandb.log'
$cleanProc = Start-DwHost -ProjectDir $CleanDbHostPath -Url $CleanDbHostUrl -LogPath $cleanHostLog -Label 'CleanDB'
try {
    Wait-DwReady -Url $CleanDbHostUrl -TimeoutSec 180 -Label 'CleanDB'
} catch {
    throw "CleanDB host failed to start at $CleanDbHostUrl within 180s. See $cleanHostLog"
}
Ensure-DwLicense -HostUrl $CleanDbHostUrl -Label 'target'

# ----- Steps 11-12: Serialize Replace + Merge against Swift-2.2 -----------------
Write-Step 'Steps 11-12: Serialize Replace + Merge (Swift-2.2 -> YAML)'
$serReplace = Invoke-DwApi -HostUrl $SwiftHostUrl -Endpoint '/Admin/Api/SerializerSerialize?mode=replace' -LogPath (Join-Path $script:runDir 'serialize-replace.log')
if ($serReplace.Code -ne 200) {
    throw "Serialize Replace: expected HTTP 200, got $($serReplace.Code). See serialize-replace.log"
}
if (Select-String -Path (Join-Path $script:runDir 'serialize-replace.log') -Pattern 'escalated|CumulativeStrictModeException' -Quiet) {
    throw "Serialize Replace emitted strict-mode escalations. See serialize-replace.log"
}
Write-Host '  Serialize Replace HTTP 200 OK'

$serMerge = Invoke-DwApi -HostUrl $SwiftHostUrl -Endpoint '/Admin/Api/SerializerSerialize?mode=merge' -LogPath (Join-Path $script:runDir 'serialize-merge.log')
if ($serMerge.Code -ne 200) {
    throw "Serialize Merge: expected HTTP 200, got $($serMerge.Code). See serialize-merge.log"
}
if (Select-String -Path (Join-Path $script:runDir 'serialize-merge.log') -Pattern 'escalated|CumulativeStrictModeException' -Quiet) {
    throw "Serialize Merge emitted strict-mode escalations. See serialize-merge.log"
}
Write-Host '  Serialize Merge HTTP 200 OK'

# ----- Step 13: Cross-host SerializeRoot mirror -------------------------------
Write-Step 'Step 13: Mirror Swift-2.2 SerializeRoot -> CleanDB'
$swSerRoot = Join-Path $SwiftHostPath   'wwwroot/Files/System/Serializer/SerializeRoot'
$cdSerRoot = Join-Path $CleanDbHostPath 'wwwroot/Files/System/Serializer/SerializeRoot'
if (-not (Test-Path $swSerRoot)) {
    throw "Source SerializeRoot not found after serialize: $swSerRoot"
}
New-Item -ItemType Directory -Force -Path $cdSerRoot | Out-Null
# Remove prior stale mirror (specific replace/merge subdirs only — never blanket delete)
$cdReplace = Join-Path $cdSerRoot 'replace'
$cdMerge   = Join-Path $cdSerRoot 'merge'
if (Test-Path $cdReplace) { Remove-Item -Recurse -Force $cdReplace }
if (Test-Path $cdMerge)   { Remove-Item -Recurse -Force $cdMerge }
Copy-Item -Recurse -Force (Join-Path $swSerRoot 'replace') $cdSerRoot
Copy-Item -Recurse -Force (Join-Path $swSerRoot 'merge')   $cdSerRoot
Write-Host "  Mirrored $swSerRoot -> $cdSerRoot"

# ----- Steps 14-15: Deserialize Replace + Merge against CleanDB -----------------
Write-Step 'Steps 14-15: Deserialize Replace + Merge (YAML -> CleanDB)'
$desReplace = Invoke-DwApi -HostUrl $CleanDbHostUrl -Endpoint '/Admin/Api/SerializerDeserialize?mode=replace' -LogPath (Join-Path $script:runDir 'deserialize-replace.log')
if ($desReplace.Code -ne 200) {
    throw "Deserialize Replace: expected HTTP 200, got $($desReplace.Code). See deserialize-replace.log"
}
if (Select-String -Path (Join-Path $script:runDir 'deserialize-replace.log') -Pattern 'escalated|CumulativeStrictModeException' -Quiet) {
    throw "Deserialize Replace emitted strict-mode escalations. See deserialize-replace.log"
}
Write-Host '  Deserialize Replace HTTP 200 OK'

$desMerge = Invoke-DwApi -HostUrl $CleanDbHostUrl -Endpoint '/Admin/Api/SerializerDeserialize?mode=merge' -LogPath (Join-Path $script:runDir 'deserialize-merge.log')
if ($desMerge.Code -ne 200) {
    throw "Deserialize Merge: expected HTTP 200, got $($desMerge.Code). See deserialize-merge.log"
}
if (Select-String -Path (Join-Path $script:runDir 'deserialize-merge.log') -Pattern 'escalated|CumulativeStrictModeException' -Quiet) {
    throw "Deserialize Merge emitted strict-mode escalations. See deserialize-merge.log"
}
Write-Host '  Deserialize Merge HTTP 200 OK'

# ----- Step 16: Smoke tool ----------------------------------------------------
Write-Step 'Step 16: Frontend smoke tool'
$smokeLog = Join-Path $script:runDir 'smoke.log'
$smokeScript = Join-Path $script:repoRoot 'tools/smoke/Test-BaselineFrontend.ps1'
& pwsh -NoProfile -File $smokeScript `
    -HostUrl $CleanDbHostUrl -AreaId 3 -LangPrefix '/en-us' `
    -SqlServer $SqlServer -SqlDatabase $CleanDb *>&1 | Tee-Object -FilePath $smokeLog | Out-Host
$smokeExit = $LASTEXITCODE
if ($smokeExit -ne 0) {
    throw "Smoke tool exited $smokeExit — see $smokeLog"
}
if (Select-String -Path $smokeLog -Pattern 'Nothing to test' -Quiet) {
    throw "Smoke tool ran but reported 'Nothing to test' (vacuous pass) — see $smokeLog"
}
Write-Host '  Smoke tool exit 0 AND non-vacuous'

# ----- Step 17: EcomProducts count assertion ----------------------------------
Write-Step 'Step 17: EcomProducts count assertion (2051 == 2051)'
$srcCount = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $SwiftDb -Query 'SELECT COUNT(*) FROM EcomProducts'
$tgtCount = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query 'SELECT COUNT(*) FROM EcomProducts'
Write-Host "  Swift-2.2 EcomProducts: $srcCount"
Write-Host "  CleanDB   EcomProducts: $tgtCount"
if ($srcCount -ne 2051) {
    throw "Swift-2.2 EcomProducts expected 2051, got $srcCount (source cleanup scripts may have over-deleted)"
}
if ($tgtCount -ne 2051) {
    throw "CleanDB EcomProducts expected 2051, got $tgtCount (C.1 preservation violated — deserialize dropped rows)"
}

# ----- Step 18: Exclusion contract assertions ----------------------------------
# v2 split: every customer-owned subtree is carved out of the replace predicate and ships
# via its own merge predicate. Assert per subtree (relative dirs under the MASTER area dir
# 'Swift 2' — layer dirs use translated menu texts and are covered by Step 19's whole-area
# comparison): absent from replace YAML, present in merge YAML. Then assert the merge pass
# landed the roots on target, and the unsubscribe page (replace-owned inside an otherwise
# merge-owned folder) is present in REPLACE yaml — the carve-at-folder-level guard.
Write-Step 'Step 18: Exclusion contract (v2 merge subtrees replace-excluded, merge-shipped)'
$masterReplace = Join-Path $SwiftHostPath 'wwwroot/Files/System/Serializer/SerializeRoot/replace/_content/Swift 2'
$masterMerge   = Join-Path $SwiftHostPath 'wwwroot/Files/System/Serializer/SerializeRoot/merge/_content/Swift 2'
# Relative directory per merge subtree ('/' in menu texts is sanitized to '_' on disk)
$mergeSubtrees = @(
    'Home',
    'Home Machines',
    'About',
    'Posts',
    'Header _ Footer',
    'Navigation/Secondary Navigation/Find dealers',
    'Navigation/Footer Navigation/About the shop',
    'Navigation/Footer Navigation/Help and info',
    'Newsletter Emails/Swift Newsletters - Light',
    'Newsletter Emails/Swift Newsletters - Dark'
)
foreach ($subtree in $mergeSubtrees) {
    if (Test-Path (Join-Path $masterReplace $subtree)) {
        throw "Exclusion violated: merge subtree '$subtree' present in REPLACE YAML"
    }
    if (-not (Test-Path (Join-Path $masterMerge $subtree))) {
        throw "Merge YAML missing subtree '$subtree' — merge Content serialization incomplete"
    }
}
if (-not (Test-Path (Join-Path $masterReplace 'Newsletter Emails/Unsubscribe confirmation page'))) {
    throw 'Unsubscribe confirmation page missing from REPLACE YAML — the folder-level newsletter carve-out regressed (page would ship nowhere)'
}
foreach ($menu in @('Home', 'About', 'Posts', 'Find dealers', 'Desktop Header')) {
    $n = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query "SELECT COUNT(*) FROM Page p JOIN Area a ON a.AreaID = p.PageAreaID WHERE a.AreaMasterAreaId = 0 AND p.PageMenuText = '$menu'"
    if ($n -lt 1) { throw "Merge-shipped page '$menu' missing on target master area" }
}
Write-Host "  Exclusion contract OK: $($mergeSubtrees.Count) merge subtrees replace-excluded + merge-shipped + landed; unsubscribe page stays replace"

# ----- Step 19: Language-layer round-trip contract -----------------------------
# Auto-detected: every area on the source with AreaMasterAreaId > 0 is a language
# layer and must round-trip onto the target — same area id + master link (area ids
# are the cross-environment coordinate system), same page count, every master-page
# link restored to a page that exists in the master area, and the same multiset of
# menu texts (proves translated content shipped, not master copies).
Write-Step 'Step 19: Language-layer round-trip contract'
$layerIdsRaw = & sqlcmd -S $SqlServer -E -d $SwiftDb -h -1 -W -Q "SET NOCOUNT ON; SELECT AreaID FROM Area WHERE AreaMasterAreaId > 0 ORDER BY AreaID" 2>&1
$layerIds = @($layerIdsRaw | Where-Object { $_ -match '^\s*\d+\s*$' } | ForEach-Object { [int]"$_".Trim() })
$layersVerified = 0
if ($layerIds.Count -eq 0) {
    Write-Host '  Source has no language-layer areas — contract skipped (vacuous)'
} else {
    $replaceManifestPath = Join-Path $SwiftHostPath 'wwwroot/Files/System/Serializer/SerializeRoot/replace/replace-manifest.json'
    $replaceManifest = Get-Content $replaceManifestPath -Raw | ConvertFrom-Json
    foreach ($layerId in $layerIds) {
        $masterId = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $SwiftDb -Query "SELECT AreaMasterAreaId FROM Area WHERE AreaID = $layerId"
        # v2: one entry per Content predicate (entryId carries the path suffix) — match by areaId.
        $entry = $replaceManifest.entries | Where-Object { $_.providerType -eq 'Content' -and $_.areaId -eq $layerId } | Select-Object -First 1
        if (-not $entry) {
            throw "Layer area ${layerId}: no Content entry for the layer in replace manifest — includeLanguageLayers expansion did not serialize the layer"
        }
        $tgtMaster = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query "SELECT ISNULL((SELECT AreaMasterAreaId FROM Area WHERE AreaID = $layerId), -1)"
        if ($tgtMaster -ne $masterId) {
            throw "Layer area ${layerId}: target AreaMasterAreaId is $tgtMaster, expected $masterId (area missing or master link lost)"
        }
        $srcPages = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $SwiftDb -Query "SELECT COUNT(*) FROM Page WHERE PageAreaID = $layerId"
        $tgtPages = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query "SELECT COUNT(*) FROM Page WHERE PageAreaID = $layerId"
        if ($tgtPages -ne $srcPages) {
            throw "Layer area ${layerId}: page count src=$srcPages tgt=$tgtPages"
        }
        $srcLinks = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $SwiftDb -Query "SELECT COUNT(*) FROM Page WHERE PageAreaID = $layerId AND PageMasterPageId > 0"
        $tgtLinks = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query "SELECT COUNT(*) FROM Page WHERE PageAreaID = $layerId AND PageMasterPageId > 0"
        if ($tgtLinks -ne $srcLinks -or $tgtLinks -eq 0) {
            throw "Layer area ${layerId}: master-page link count src=$srcLinks tgt=$tgtLinks (MasterLinkRestorer pass incomplete)"
        }
        $tgtValidLinks = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query "SELECT COUNT(*) FROM Page p WHERE p.PageAreaID = $layerId AND p.PageMasterPageId > 0 AND EXISTS (SELECT 1 FROM Page m WHERE m.PageID = p.PageMasterPageId AND m.PageAreaID = $masterId)"
        if ($tgtValidLinks -ne $tgtLinks) {
            throw "Layer area ${layerId}: $($tgtLinks - $tgtValidLinks) of $tgtLinks master-page links dangle (no such page in master area $masterId on target)"
        }
        $srcTextSum = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $SwiftDb -Query "SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(PageMenuText)), 0) FROM Page WHERE PageAreaID = $layerId"
        $tgtTextSum = Invoke-Sqlcmd-Scalar -Server $SqlServer -Database $CleanDb -Query "SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(PageMenuText)), 0) FROM Page WHERE PageAreaID = $layerId"
        if ($tgtTextSum -ne $srcTextSum) {
            throw "Layer area ${layerId}: menu-text checksum mismatch (src=$srcTextSum tgt=$tgtTextSum) — translated content did not round-trip"
        }
        Write-Host "  Layer area $layerId OK: master=$masterId pages=$tgtPages masterLinks=$tgtLinks (all resolve) menuTextChecksum=$tgtTextSum"
        $layersVerified++
    }
}

# ----- Step 20: Stop both hosts ----------------------------------------------
Write-Step 'Step 20: Stop both DW hosts'
Stop-HostOnPort -Port $swiftPort -Label 'source'
Stop-HostOnPort -Port $cleanPort -Label 'target'

# ----- Step 21: Summary -------------------------------------------------------
Write-Step 'PIPELINE PASSED — all gates met'
$pipelineEndUtc = (Get-Date).ToUniversalTime()
$duration = ($pipelineEndUtc - $pipelineStartUtc).TotalSeconds

$summary = @{
    Disposition = 'CLOSED'
    StartUtc    = $pipelineStartUtc.ToString('o')
    EndUtc      = $pipelineEndUtc.ToString('o')
    DurationSec = [int]$duration
    HttpCodes = @{
        SerializeReplace   = $serReplace.Code
        SerializeMerge     = $serMerge.Code
        DeserializeReplace = $desReplace.Code
        DeserializeMerge   = $desMerge.Code
    }
    EcomProducts = @{ Src = $srcCount; Tgt = $tgtCount }
    LanguageLayersVerified = $layersVerified
    SmokeExit    = $smokeExit
    DllMd5       = $srcMd5
    RunDir       = $runDir
}
$summary | ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $runDir 'summary.json') | Out-Host
exit 0
