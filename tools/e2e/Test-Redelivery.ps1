<#
.SYNOPSIS
Live strict redelivery check: deliver the staged folders in strict mode, deliver them again,
and prove the second and later deliveries leave links and content rows as they were.

.DESCRIPTION
Unit tests do not show what a repeated live delivery does to a real database. Two engine
regressions (#30, #32) were only visible on a live strict-mode delivery, and a Merge redelivery
once re-mapped page links that already pointed at the right page on every run (the header
navigation root moved 52 -> 54 -> 56 and the megamenu broke). This check is the release
evidence for engine changes (docs/releasing.md).

What it does, against one running Dynamicweb host with the Serializer installed and the
delivery folders already staged under Files/System/Serializer/SerializeRoot:

  1. Delivery 1: POST /Admin/Api/Deserialize?mode=<m>&strictMode=true for every -Modes entry,
     in order (skipped with -AlreadyDelivered). Every call must answer status ok.
  2. Snapshot the content tables of the target database: Area, Page, Paragraph, GridRow,
     every ItemType_* table and any -ExtraTables, every row and column as text, keyed by
     the primary key.
  3. Deliveries 2..-Runs: the same calls, strict, each must answer status ok; snapshot after
     each and compare with the snapshot after delivery 1.
  4. FAIL when a later delivery adds or removes a row, or changes a column outside
     -IgnoreColumns. A changed value that is a page link (Default.aspx?ID=n, or a column whose
     name says link/page id/root/shortcut) is reported as a link change first.
     Changes to -IgnoreColumns are counted and reported, never failed.

Default -IgnoreColumns are the update timestamps a no-op Replace still rewrites today (#71).
Once #71 is fixed, pass -IgnoreColumns @() so a timestamp rewrite fails the check too.

Evidence lands in tools/e2e/runs/redelivery-<yyyyMMdd-HHmmss>/ (gitignored): one JSON per API
call, diff.md (the readable comparison) and summary.json (Disposition PASS or FAIL, the engine
version and DLL hash when -SerializerDll is given, and the per-delivery totals).

Exit codes: 0 PASS, 1 FAIL (a call not ok, or a later delivery changed rows or links),
2 prerequisite missing (no token, database unreachable, no table to snapshot).

.PARAMETER BaseUrl
Host base URL, for example https://localhost:5001.

.PARAMETER Token
Bearer token for the Management API: an API key, or a token from
/Admin/TokenAuthentication/authenticate. Default $env:DW_API_KEY.

.PARAMETER ConnectionString
Connection string of the host's database. Or pass -SqlServer and -Database for integrated security.

.PARAMETER Modes
Deserialize modes, delivered in this order on every run. Default replace, merge.

.PARAMETER Runs
Total deliveries, the first included. Default 3 (the first, then two redeliveries).

.PARAMETER AlreadyDelivered
The target already holds a first delivery of these folders; snapshot first, then deliver
-Runs - 1 more times.

.PARAMETER ExtraTables
More tables to snapshot (for example a SqlTable predicate's table such as EcomGroups).

.PARAMETER IgnoreColumns
Columns whose changes are reported but do not fail the check.

.PARAMETER SerializerDll
The Truvio.Commerce.Serializer.dll deployed on the host; its product version and sha256 go into
summary.json so the evidence names the engine it proves.

.PARAMETER SelfTest
Run the comparison against built-in fixtures (no host, no database) and exit.

.PARAMETER SnapshotOnly
Snapshot the database twice without any delivery and compare (proves the snapshot is stable
on this database). Needs only the database.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Token = $env:DW_API_KEY,
    [string]$ConnectionString,
    [string]$SqlServer,
    [string]$Database,
    [string[]]$Modes = @('replace', 'merge'),
    [ValidateRange(2, 10)][int]$Runs = 3,
    [switch]$AlreadyDelivered,
    [string[]]$ExtraTables = @(),
    [string[]]$IgnoreColumns = @('PageUpdatedDate', 'ParagraphUpdatedDate', 'GridRowUpdatedAt'),
    [string]$SerializerDll,
    [string]$OutDir,
    [switch]$SelfTest,
    [switch]$SnapshotOnly
)

$ErrorActionPreference = 'Stop'
$LinkValuePattern = '(?i)default\.aspx\?id=\d+'
$LinkColumnPattern = '(?i)(link|pageid|root|shortcut|masterpage)'

# ---------------------------------------------------------------------------
# Snapshot and comparison
# ---------------------------------------------------------------------------

function ConvertTo-CellText($v) {
    if ($null -eq $v -or $v -is [System.DBNull]) { return '<null>' }
    if ($v -is [byte[]]) { return 'b64:' + [Convert]::ToBase64String($v) }
    if ($v -is [datetime]) { return $v.ToString('o') }
    if ($v -is [datetimeoffset]) { return $v.ToString('o') }
    if ($v -is [IFormattable]) { return $v.ToString($null, [Globalization.CultureInfo]::InvariantCulture) }
    return [string]$v
}

function Get-DbSnapshot([string]$cs, [string[]]$extra) {
    $conn = [System.Data.SqlClient.SqlConnection]::new($cs)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT t.name FROM sys.tables t WHERE SCHEMA_NAME(t.schema_id) = 'dbo' AND (t.name IN ('Area','Page','Paragraph','GridRow') OR t.name LIKE 'ItemType[_]%')"
        $names = [System.Collections.Generic.List[string]]::new()
        $r = $cmd.ExecuteReader(); while ($r.Read()) { $names.Add($r.GetString(0)) }; $r.Close()
        foreach ($e in $extra) { if ($e -and -not $names.Contains($e)) { $names.Add($e) } }

        $snap = [ordered]@{}
        foreach ($t in ($names | Sort-Object)) {
            $cmd.CommandText = "SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@t) ORDER BY ic.key_ordinal"
            $cmd.Parameters.Clear(); [void]$cmd.Parameters.AddWithValue('@t', "dbo.[$t]")
            $keys = @(); $r = $cmd.ExecuteReader(); while ($r.Read()) { $keys += $r.GetString(0) }; $r.Close()
            $cmd.Parameters.Clear()
            $cmd.CommandText = "SELECT * FROM dbo.[$t]"
            $rows = @{}
            $r = $cmd.ExecuteReader()
            $cols = @(0..($r.FieldCount - 1) | ForEach-Object { $r.GetName($_) })
            while ($r.Read()) {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $cols.Count; $i++) { $row[$cols[$i]] = ConvertTo-CellText $r.GetValue($i) }
                if ($keys.Count) { $k = ($keys | ForEach-Object { $row[$_] }) -join '|' }
                else { $k = 'row:' + (($row.Values) -join [char]31); $n = 1; while ($rows.ContainsKey("$k#$n")) { $n++ }; $k = "$k#$n" }
                $rows[$k] = $row
            }
            $r.Close()
            $snap[$t] = @{ Key = $keys; Rows = $rows }
        }
        return $snap
    } finally { $conn.Dispose() }
}

function Test-IsLinkChange([string]$col, [string]$old, [string]$new) {
    return ($old -match $LinkValuePattern) -or ($new -match $LinkValuePattern) -or ($col -match $LinkColumnPattern)
}

# Compare two snapshots. Returns an object with the failing changes and the ignored ones.
function Compare-DbSnapshot($before, $after, [string[]]$ignore) {
    $res = [pscustomobject]@{ Added = @(); Removed = @(); LinkChanges = @(); OtherChanges = @(); Ignored = @{} }
    $tables = @($before.Keys) + @($after.Keys) | Sort-Object -Unique
    foreach ($t in $tables) {
        if (-not $before.Contains($t)) { $res.Added += "table $t appeared"; continue }
        if (-not $after.Contains($t)) { $res.Removed += "table $t disappeared"; continue }
        $b = $before[$t].Rows; $a = $after[$t].Rows
        foreach ($k in $b.Keys) {
            if (-not $a.ContainsKey($k)) { $res.Removed += "$t [$k]"; continue }
            $ob = $b[$k]; $oa = $a[$k]
            foreach ($c in (@($ob.Keys) + @($oa.Keys) | Sort-Object -Unique)) {
                $old = if ($ob.Contains($c)) { $ob[$c] } else { '<no column>' }
                $new = if ($oa.Contains($c)) { $oa[$c] } else { '<no column>' }
                if ($old -ceq $new) { continue }
                if ($ignore -contains $c) { $res.Ignored["$t.$c"] = 1 + [int]$res.Ignored["$t.$c"]; continue }
                $line = "$t [$k] $c : '$old' -> '$new'"
                if (Test-IsLinkChange $c $old $new) { $res.LinkChanges += $line } else { $res.OtherChanges += $line }
            }
        }
        foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $res.Added += "$t [$k]" } }
    }
    $res | Add-Member -NotePropertyName Failed -NotePropertyValue (($res.Added.Count + $res.Removed.Count + $res.LinkChanges.Count + $res.OtherChanges.Count) -gt 0)
    return $res
}

function Format-Comparison([string]$title, $cmp) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("## $title")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("Result: $(if ($cmp.Failed) { 'FAIL' } else { 'PASS' }); link changes $($cmp.LinkChanges.Count), other changes $($cmp.OtherChanges.Count), rows added $($cmp.Added.Count), rows removed $($cmp.Removed.Count)")
    foreach ($sec in @(@('Link changes', $cmp.LinkChanges), @('Other changes', $cmp.OtherChanges), @('Rows added', $cmp.Added), @('Rows removed', $cmp.Removed))) {
        if ($sec[1].Count) {
            [void]$sb.AppendLine(''); [void]$sb.AppendLine("### $($sec[0])"); [void]$sb.AppendLine('')
            foreach ($l in ($sec[1] | Select-Object -First 200)) { [void]$sb.AppendLine("- $l") }
            if ($sec[1].Count -gt 200) { [void]$sb.AppendLine("- ... $($sec[1].Count - 200) more") }
        }
    }
    if ($cmp.Ignored.Count) {
        [void]$sb.AppendLine(''); [void]$sb.AppendLine('### Ignored columns that changed (reported, not failed)'); [void]$sb.AppendLine('')
        foreach ($k in ($cmp.Ignored.Keys | Sort-Object)) { [void]$sb.AppendLine("- $k : $($cmp.Ignored[$k]) row(s)") }
    }
    [void]$sb.AppendLine('')
    return $sb.ToString()
}

# ---------------------------------------------------------------------------
# Self test: the comparison on fixtures
# ---------------------------------------------------------------------------

function New-Fixture {
    $page = @{}
    $page['52'] = [ordered]@{ PageID = '52'; PageMenuText = 'Header'; PageUpdatedDate = '2026-11-01' }
    $page['60'] = [ordered]@{ PageID = '60'; PageMenuText = 'Home'; PageUpdatedDate = '2026-11-01' }
    $item = @{}
    $item['h1'] = [ordered]@{ Id = 'h1'; NavigationRoot = '52'; Link = 'Default.aspx?ID=60'; Title = 'Header' }
    return [ordered]@{
        Page = @{ Key = @('PageID'); Rows = $page }
        'ItemType_Swift-v2_Header' = @{ Key = @('Id'); Rows = $item }
    }
}

function Copy-Fixture($s) {
    $c = [ordered]@{}
    foreach ($t in $s.Keys) {
        $rows = @{}
        foreach ($k in $s[$t].Rows.Keys) { $o = [ordered]@{}; foreach ($col in $s[$t].Rows[$k].Keys) { $o[$col] = $s[$t].Rows[$k][$col] }; $rows[$k] = $o }
        $c[$t] = @{ Key = $s[$t].Key; Rows = $rows }
    }
    return $c
}

function Invoke-SelfTest {
    $ign = @('PageUpdatedDate')
    function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "  ok   $name" } else { Write-Host "  FAIL $name" -ForegroundColor Red; $script:selfFails++ } }
    $script:selfFails = 0
    $base = New-Fixture

    $same = Copy-Fixture $base
    $c = Compare-DbSnapshot $base $same $ign
    Check 'identical snapshots pass' (-not $c.Failed)

    $ts = Copy-Fixture $base; $ts.Page.Rows['52'].PageUpdatedDate = '2026-11-15'
    $c = Compare-DbSnapshot $base $ts $ign
    Check 'a timestamp-only rewrite passes and is reported' ((-not $c.Failed) -and $c.Ignored['Page.PageUpdatedDate'] -eq 1)

    $nav = Copy-Fixture $base; $nav['ItemType_Swift-v2_Header'].Rows['h1'].NavigationRoot = '54'
    $c = Compare-DbSnapshot $base $nav $ign
    Check 'a moved navigation root fails as a link change' ($c.Failed -and $c.LinkChanges.Count -eq 1)

    $lnk = Copy-Fixture $base; $lnk['ItemType_Swift-v2_Header'].Rows['h1'].Link = 'Default.aspx?ID=62'
    $c = Compare-DbSnapshot $base $lnk $ign
    Check 'a re-mapped Default.aspx link fails as a link change' ($c.Failed -and $c.LinkChanges.Count -eq 1)

    $txt = Copy-Fixture $base; $txt.Page.Rows['60'].PageMenuText = 'Start'
    $c = Compare-DbSnapshot $base $txt $ign
    Check 'a changed menu text fails as another change' ($c.Failed -and $c.OtherChanges.Count -eq 1 -and $c.LinkChanges.Count -eq 0)

    $add = Copy-Fixture $base; $add.Page.Rows['61'] = [ordered]@{ PageID = '61'; PageMenuText = 'Dup'; PageUpdatedDate = 'x' }
    $c = Compare-DbSnapshot $base $add $ign
    Check 'a row created by the redelivery fails' ($c.Failed -and $c.Added.Count -eq 1)

    $del = Copy-Fixture $base; $del.Page.Rows.Remove('60')
    $c = Compare-DbSnapshot $base $del $ign
    Check 'a row removed by the redelivery fails' ($c.Failed -and $c.Removed.Count -eq 1)

    $strict = Copy-Fixture $base; $strict.Page.Rows['52'].PageUpdatedDate = '2026-11-15'
    $c = Compare-DbSnapshot $base $strict @()
    Check 'with -IgnoreColumns @() a timestamp rewrite fails' ($c.Failed)

    Write-Host ("self test: {0}" -f $(if ($script:selfFails) { "$($script:selfFails) failed" } else { 'all passed' }))
    exit ([int]($script:selfFails -gt 0))
}

if ($SelfTest) { Invoke-SelfTest }

# ---------------------------------------------------------------------------
# Live run
# ---------------------------------------------------------------------------

if (-not $ConnectionString) {
    if ($SqlServer -and $Database) { $ConnectionString = "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true" }
    else { Write-Host 'Pass -ConnectionString, or -SqlServer and -Database.'; exit 2 }
}
try { $first = Get-DbSnapshot $ConnectionString $ExtraTables } catch { Write-Host "Database snapshot failed: $($_.Exception.Message)"; exit 2 }
if ($first.Count -eq 0) { Write-Host 'No table to snapshot (no Area/Page/Paragraph/GridRow/ItemType_* in dbo).'; exit 2 }

if ($SnapshotOnly) {
    $again = Get-DbSnapshot $ConnectionString $ExtraTables
    $cmp = Compare-DbSnapshot $first $again $IgnoreColumns
    $rows = ($first.Values | ForEach-Object { $_.Rows.Count } | Measure-Object -Sum).Sum
    Write-Host ("snapshot-only: {0} tables, {1} rows; second snapshot {2}" -f $first.Count, $rows, $(if ($cmp.Failed) { 'DIFFERS' } else { 'identical' }))
    exit ([int]$cmp.Failed)
}

if (-not $BaseUrl) { Write-Host 'Pass -BaseUrl.'; exit 2 }
if (-not $Token) { Write-Host 'Pass -Token or set DW_API_KEY.'; exit 2 }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutDir) { $OutDir = Join-Path $repoRoot ("tools/e2e/runs/redelivery-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Invoke-Delivery([int]$n) {
    $ok = $true; $totals = @()
    foreach ($m in $Modes) {
        $uri = "$($BaseUrl.TrimEnd('/'))/Admin/Api/Deserialize?mode=$m&strictMode=true"
        try {
            $resp = Invoke-WebRequest -Uri $uri -Method POST -Headers @{ Authorization = "Bearer $Token" } -Body '{}' -ContentType 'application/json' -TimeoutSec 3600 -SkipHttpErrorCheck -SkipCertificateCheck
        } catch {
            $resp = [pscustomobject]@{ StatusCode = 0; Content = "request failed: $($_.Exception.Message)" }
        }
        $resp.Content | Set-Content (Join-Path $OutDir ("delivery{0}-{1}.json" -f $n, $m)) -Encoding utf8
        $j = $null; try { $j = $resp.Content | ConvertFrom-Json } catch { }
        $status = if ($j) { $j.status } else { "HTTP $($resp.StatusCode)" }
        $t = [pscustomobject]@{ delivery = $n; mode = $m; http = [int]$resp.StatusCode; status = $status
            created = $j.model.totalCreated; updated = $j.model.totalUpdated; skipped = $j.model.totalSkipped; failed = $j.model.totalFailed
            message = if ($j) { ($j.message -split "`n")[0] } else { [string]$resp.Content } }
        Write-Host ("  delivery {0} {1}: HTTP {2} status={3} created={4} updated={5} skipped={6} failed={7}" -f $n, $m, $t.http, $t.status, $t.created, $t.updated, $t.skipped, $t.failed)
        $totals += $t
        if ($status -ne 'ok') { Write-Host "  STOP: strict delivery $n ($m) did not answer status ok: $($t.message)"; $ok = $false; break }
    }
    return @{ Ok = $ok; Totals = $totals }
}

$deliveries = @()
$failed = $false
$report = [System.Text.StringBuilder]::new()
[void]$report.AppendLine("# Strict redelivery check"); [void]$report.AppendLine('')
[void]$report.AppendLine("Host $BaseUrl; modes $($Modes -join ', '); runs $Runs; ignored columns: $(if ($IgnoreColumns.Count) { $IgnoreColumns -join ', ' } else { 'none' })"); [void]$report.AppendLine('')

if (-not $AlreadyDelivered) {
    Write-Host 'Delivery 1 (strict)'
    $d = Invoke-Delivery 1; $deliveries += $d.Totals
    if (-not $d.Ok) { $failed = $true }
    else { $first = Get-DbSnapshot $ConnectionString $ExtraTables }
}

$comparisons = @()
if (-not $failed) {
    $prev = $first
    for ($n = 2; $n -le $Runs; $n++) {
        Write-Host "Delivery $n (strict redelivery)"
        $d = Invoke-Delivery $n; $deliveries += $d.Totals
        $after = Get-DbSnapshot $ConnectionString $ExtraTables
        $vsFirst = Compare-DbSnapshot $first $after $IgnoreColumns
        [void]$report.Append((Format-Comparison "Delivery $n against the state after delivery 1" $vsFirst))
        if ($n -gt 2) {
            $vsPrev = Compare-DbSnapshot $prev $after $IgnoreColumns
            [void]$report.Append((Format-Comparison "Delivery $n against delivery $($n - 1)" $vsPrev))
        }
        $comparisons += [pscustomobject]@{ delivery = $n; linkChanges = $vsFirst.LinkChanges.Count; otherChanges = $vsFirst.OtherChanges.Count
            rowsAdded = $vsFirst.Added.Count; rowsRemoved = $vsFirst.Removed.Count; ignored = $vsFirst.Ignored; failed = $vsFirst.Failed }
        Write-Host ("  after delivery {0}: link changes {1}, other changes {2}, rows added {3}, removed {4}; ignored {5}" -f $n, $vsFirst.LinkChanges.Count, $vsFirst.OtherChanges.Count, $vsFirst.Added.Count, $vsFirst.Removed.Count, (($vsFirst.Ignored.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '))
        if (-not $d.Ok -or $vsFirst.Failed) { $failed = $true; break }
        $prev = $after
    }
}

$engine = $null
if ($SerializerDll -and (Test-Path $SerializerDll)) {
    $engine = [pscustomobject]@{ path = $SerializerDll; productVersion = (Get-Item $SerializerDll).VersionInfo.ProductVersion; sha256 = (Get-FileHash $SerializerDll -Algorithm SHA256).Hash }
}
$summary = [pscustomobject]@{
    Disposition = if ($failed) { 'FAIL' } else { 'PASS' }
    host = $BaseUrl; modes = $Modes; runs = $Runs; alreadyDelivered = [bool]$AlreadyDelivered
    ignoredColumns = $IgnoreColumns; engine = $engine
    tables = $first.Count; rows = ($first.Values | ForEach-Object { $_.Rows.Count } | Measure-Object -Sum).Sum
    deliveries = $deliveries; comparisons = $comparisons
    finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'summary.json') -Encoding utf8
$report.ToString() | Set-Content (Join-Path $OutDir 'diff.md') -Encoding utf8
Write-Host ("{0}: evidence in {1}" -f $summary.Disposition, $OutDir)
exit ([int]$failed)
