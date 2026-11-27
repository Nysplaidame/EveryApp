<#
.SYNOPSIS
  Ferry soak test (M1 plan, Task 17). Not part of build.ps1 or CI: it runs for hours and writes real data.
.DESCRIPTION
  Loops copy -> verify -> move -> verify -> delete the output over one source folder. During copies and moves it sometimes freezes
  ferry.exe for 5 to 60 seconds (a stall) and sometimes kills it (a crash) and finishes the saved job with `ferry resume`.
  Every 30 seconds it records the working set and handle count of the running ferry.exe to artifacts/soak/samples.csv.
  At the end it fits a straight line through the samples of the last 3 hours (the last 75 % of a shorter run) and prints one line,
  SOAK PASS or SOAK FAIL. The summary also goes to artifacts/soak/summary.md. Exit code 0 on pass, 1 on fail.

  Safety: the source is only read. The script only ever deletes inside DestRoot, and only when DestRoot is new, empty, or marked by this
  script (a marker file). It never empties a drive root, never follows links, refuses to run as administrator, and refuses a DestRoot
  that is the source or inside or around it.

  Drive wear: every copy and move is limited to -LimitMBps, so a 4-hour run writes at most about 3600 x Hours x LimitMBps MB.
  `ferry resume` has no speed limit option, so a resumed job finishes the rest of its data at full speed.
.PARAMETER Source
  Folder to copy (read only). Default: the "mixed" set's source in bench.local.json.
.PARAMETER DestRoot
  Folder the script owns and empties. Default: a "soak" folder inside the "mixed" set's dest in bench.local.json.
.PARAMETER MoveRoot
  A second folder the script owns and empties, used as the target of the move step (it can be on another volume). Without it the move
  step is a same-volume rename inside DestRoot: nothing is copied, so a kill during the move cannot be exercised.
.PARAMETER Force
  Start even when one cycle at the speed limit would take longer than the whole time budget.
.PARAMETER Hours
  How long to keep starting new cycles (default 4). The cycle in progress always finishes.
.PARAMETER Minutes
  Overrides Hours, for short trials.
.PARAMETER LimitMBps
  Speed limit passed to every ferry copy, in MB/s (default 5).
.PARAMETER Seed
  Seed for the random choices (printed, so a run can be repeated).
.PARAMETER SampleSeconds
  Seconds between memory/handle samples (default 30).
#>
[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$DestRoot = '',
    [string]$MoveRoot = '',
    [switch]$Force,
    [ValidateRange(0.01, 168)][double]$Hours = 4,
    [double]$Minutes = 0,
    [ValidateRange(1, 10000)][int]$LimitMBps = 5,
    [int]$Seed = -1,
    [ValidateRange(1, 600)][int]$SampleSeconds = 30,
    [string]$Config = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Config) { $Config = Join-Path $root 'bench.local.json' } # Windows PowerShell leaves $PSScriptRoot empty in parameter defaults
$outDir = Join-Path $root 'artifacts\soak'
$ferry = Join-Path $root 'artifacts\bin\Ferry.Cli\release_win-x64\ferry.exe'
$markerName = '.ferry-soak-out'

if ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the soak test from a normal (not administrator) PowerShell. Ferry refuses to run elevated.'
}

# ---- inputs ----------------------------------------------------------------------------------------------------------------------------
if (-not $Source -or -not $DestRoot) {
    if (-not (Test-Path -LiteralPath $Config)) { throw "Give -Source and -DestRoot, or create $Config (copy bench.example.json) with a mixed set." }
    $cfg = Get-Content -Raw -LiteralPath $Config | ConvertFrom-Json
    $set = @($cfg.sets | Where-Object { $_.kind -eq 'mixed' -or $_.name -like '*mixed*' }) | Select-Object -First 1
    if (-not $set) { throw "No mixed set in $Config. Give -Source and -DestRoot." }
    if (-not $Source) { $Source = $set.source }
    if (-not $DestRoot) { $DestRoot = Join-Path $set.dest 'soak' }
}
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Source folder not found: $Source" }
$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$DestRoot = [IO.Path]::GetFullPath($DestRoot).TrimEnd('\')
if ($MoveRoot) { $MoveRoot = [IO.Path]::GetFullPath($MoveRoot).TrimEnd('\') }
$leaf = Split-Path -Leaf $Source

function Assert-Separate([string]$A, [string]$B) {
    $s = [IO.Path]::GetFullPath($A).TrimEnd('\') + '\'
    $d = [IO.Path]::GetFullPath($B).TrimEnd('\') + '\'
    if ($s.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) -or $d.StartsWith($s, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The destination must not be the source, inside it, or around it: $B"
    }
}
Assert-Separate $Source $DestRoot
if ($MoveRoot) { Assert-Separate $Source $MoveRoot; Assert-Separate $DestRoot $MoveRoot }

# Refuses a folder, or any existing parent of it, that is a link or junction (a path through a link could lead into the source).
function Assert-NoLinks([string]$Path) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    while ($p -and $p.Length -gt 3) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing a folder that is or lies under a link: $p" }
        $p = Split-Path -Parent $p
    }
}
Assert-NoLinks $DestRoot
if ($MoveRoot) { Assert-NoLinks $MoveRoot }

# Empties DestRoot, but only a folder this script owns: new, empty, or marked by this script. Never a drive root. Links inside are removed
# as links, never followed. Never touches the source.
function Clear-Owned([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Length -le 3) { throw "Refusing to empty a drive root: $full" }
    Assert-Separate $Source $full
    Assert-NoLinks $full
    $marker = Join-Path $full $markerName
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $marker) -and (Get-ChildItem -LiteralPath $full -Force | Select-Object -First 1)) {
            throw "Refusing to empty $full : it is not empty and was not made by soak.ps1 (no $markerName file). Pick an empty folder."
        }
    }
    else {
        New-Item -ItemType Directory -Force $full | Out-Null
    }
    if (-not (Test-Path -LiteralPath $marker)) { Set-Content -LiteralPath $marker -Value 'Ferry soak test output: soak.ps1 empties this folder every cycle.' }
    foreach ($child in @(Get-ChildItem -LiteralPath $full -Force | Where-Object Name -ne $markerName)) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { if ($child.PSIsContainer) { [IO.Directory]::Delete($child.FullName, $false) } else { [IO.File]::Delete($child.FullName) } }
        elseif ($child.PSIsContainer) { [IO.Directory]::Delete($child.FullName, $true) }
        else { [IO.File]::Delete($child.FullName) }
    }
}

if (-not (Test-Path -LiteralPath $ferry)) {
    Write-Host '==> build (Release)' -ForegroundColor Cyan
    & dotnet build (Join-Path $root 'src\Ferry.Cli\Ferry.Cli.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}

$files = @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force)
if ($files.Count -eq 0) { throw "Source folder is empty: $Source" }
$sourceBytes = [long]($files | Measure-Object Length -Sum).Sum
if ($Minutes -gt 0) { $Hours = $Minutes / 60 }
if ($Seed -lt 0) { $Seed = Get-Random -Maximum 1000000 }
$rng = New-Object System.Random $Seed
$limitText = "$($LimitMBps)MB/s"
$estSeconds = [math]::Max(2.0, $sourceBytes / ($LimitMBps * 1e6))
$budgetSeconds = $Hours * 3600
$cycleBytes = 2 * $sourceBytes # one copy and one move
$cycleSeconds = $cycleBytes / ($LimitMBps * 1e6)
$estGB = [math]::Round([math]::Max($budgetSeconds * $LimitMBps * 1e6, $cycleBytes) / 1e9, 1)
if ($cycleSeconds -gt $budgetSeconds -and -not $Force) {
    throw ("One cycle (copy and move of {0:F1} GB) would take about {1:F1} h at {2}, longer than the {3:F2} h budget. Use a smaller source, a higher -LimitMBps, more -Hours, or -Force." -f ($sourceBytes / 1e9), ($cycleSeconds / 3600), $limitText, $Hours)
}
$moveNote = if ($MoveRoot) { 'move step: real copy-and-delete into MoveRoot' } else { 'NO -MoveRoot: the move step is a same-volume rename only, so the kill-during-move check does not apply' }

Write-Host ("Soak: {0} files, {1} MiB, {2:F2} h, limit {3}, seed {4}. Writes about {5} GB (estimate: the larger of limit x time and one cycle; resumed jobs have no limit, so the real figure can be higher). {6}." -f
    $files.Count, [math]::Round($sourceBytes / 1MB), $Hours, $limitText, $Seed, $estGB, $moveNote) -ForegroundColor Cyan

New-Item -ItemType Directory -Force $outDir | Out-Null
$work = Join-Path $outDir 'work'
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($old in @(Get-ChildItem -LiteralPath $work -File -Force)) { Remove-Item -LiteralPath $old.FullName -Force }
Clear-Owned $DestRoot
if ($MoveRoot) { Clear-Owned $MoveRoot }

# ---- suspend / resume a whole process --------------------------------------------------------------------------------------------------
if (-not ('SoakNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SoakNative
{
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
}
'@
}

# ---- state -----------------------------------------------------------------------------------------------------------------------------
$clock = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
$csv = Join-Path $outDir 'samples.csv'
Set-Content -LiteralPath $csv -Value 'utc,elapsed_s,cycle,step,pid,working_set_bytes,handles' -Encoding ascii
$script:nextSample = 0.0
$script:cycle = 0
$script:step = ''
$script:seq = 0
$script:current = $null
$script:suspended = $false
$stats = [ordered]@{ Cycles = 0; Pauses = 0; Kills = 0; Resumes = 0; EarlyKills = 0; Verifies = 0; VerifyFailures = 0; JobFailures = 0; LeftoverParts = 0; Steps = 0 }
$problems = [Collections.Generic.List[string]]::new()

function Join-Args([string[]]$List) {
    ($List | ForEach-Object { '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }) -join ' '
}

function Get-Prop($Object, [string]$Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

function Write-Sample($Process) {
    try {
        $Process.Refresh()
        if ($Process.HasExited) { return }
        $row = [pscustomobject]@{ Elapsed = $clock.Elapsed.TotalSeconds; Ws = [long]$Process.WorkingSet64; Handles = [int]$Process.HandleCount }
        $samples.Add($row)
        Add-Content -LiteralPath $csv -Encoding ascii -Value ('{0:o},{1:F1},{2},{3},{4},{5},{6}' -f [DateTime]::UtcNow, $row.Elapsed, $script:cycle, $script:step, $Process.Id, $row.Ws, $row.Handles)
    }
    catch { } # the process ended between the check and the read
}

function Read-Events([string]$File) {
    $events = @()
    if (Test-Path -LiteralPath $File) {
        foreach ($line in @(Get-Content -LiteralPath $File)) {
            if ($line -like '*"type":"result"*') { try { $events += ($line | ConvertFrom-Json) } catch { } }
        }
    }
    return $events
}

function Get-UnfinishedJobs {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& $ferry jobs --json 2>$null) } finally { $ErrorActionPreference = $previous }
    $found = @()
    foreach ($line in $lines) {
        if ("$line" -notlike '{*') { continue }
        try { $e = "$line" | ConvertFrom-Json } catch { continue }
        if ((Get-Prop $e 'phase') -eq 'History' -and (Get-Prop $e 'message') -eq 'Unfinished') {
            $found += [pscustomobject]@{ Id = [string]$e.jobId; Date = [datetime](Get-Prop $e 'date') }
        }
    }
    return $found
}

# Runs one ferry.exe to the end, sampling it and (when $Chaos) sometimes freezing or killing it. Returns what happened.
function Invoke-Ferry([string[]]$FerryArgs, [bool]$Chaos) {
    $script:seq++
    $outFile = Join-Path $work "$($script:seq).out"
    $errFile = Join-Path $work "$($script:seq).err"
    $action = 'none'
    $trigger = 0.0
    if ($Chaos) {
        $roll = $rng.NextDouble()
        if ($roll -lt 0.30) { $action = 'pause' } elseif ($roll -lt 0.55) { $action = 'kill' }
        $trigger = [math]::Max(1.0, $estSeconds * (0.1 + 0.7 * $rng.NextDouble()))
    }
    $p = Start-Process -FilePath $ferry -ArgumentList (Join-Args $FerryArgs) -PassThru -WindowStyle Hidden -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $null = $p.Handle # keeps the exit code readable after the process ends
    $script:current = $p
    $local = [Diagnostics.Stopwatch]::StartNew()
    $resumeAt = $null
    $killed = $false
    try {
        while (-not $p.HasExited) {
            Start-Sleep -Milliseconds 250
            if ($clock.Elapsed.TotalSeconds -ge $script:nextSample) {
                Write-Sample $p
                $script:nextSample = $clock.Elapsed.TotalSeconds + $SampleSeconds
            }
            if ($action -ne 'none' -and $local.Elapsed.TotalSeconds -ge $trigger -and -not $p.HasExited) {
                if ($action -eq 'pause') {
                    [void][SoakNative]::NtSuspendProcess($p.Handle)
                    $script:suspended = $true
                    $stats.Pauses++
                    $resumeAt = $local.Elapsed.TotalSeconds + 5 + 55 * $rng.NextDouble()
                    Write-Host ("  [{0}] froze ferry for {1:F0} s" -f $script:step, ($resumeAt - $local.Elapsed.TotalSeconds)) -ForegroundColor DarkYellow
                }
                else {
                    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; $killed = $true; $stats.Kills++ } catch { }
                    Write-Host "  [$($script:step)] killed ferry" -ForegroundColor DarkYellow
                }
                $action = 'none'
            }
            if ($null -ne $resumeAt -and $local.Elapsed.TotalSeconds -ge $resumeAt) {
                [void][SoakNative]::NtResumeProcess($p.Handle)
                $script:suspended = $false
                $resumeAt = $null
            }
        }
    }
    finally {
        if ($script:suspended -and -not $p.HasExited) { [void][SoakNative]::NtResumeProcess($p.Handle) }
        $script:suspended = $false
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        $script:current = $null
    }
    $p.WaitForExit()
    $result = $null
    foreach ($e in (Read-Events $outFile)) { if ((Get-Prop $e 'type') -eq 'result') { $result = $e } }
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Killed = $killed; Result = $result; Out = $outFile }
}

function Test-JobOk($Run, [bool]$RequireVerified) {
    if ($Run.Killed -or $null -eq $Run.Result) { return $false }
    if ((Get-Prop $Run.Result 'exitCode') -ne 0 -or $Run.ExitCode -ne 0) { return $false }
    if ((Get-Prop $Run.Result 'failed') -gt 0) { return $false }
    # A move on one volume is a rename and checks no data itself (allVerified is false); the verify step after it covers the moved tree.
    if ($RequireVerified -and (Get-Prop $Run.Result 'allVerified') -ne $true) { return $false }
    return $true
}

# A copy or move that may be frozen or killed; a killed one is finished with `ferry resume`. Returns $true when the job ended well.
function Invoke-Job([string]$Name, [string[]]$FerryArgs, [bool]$RequireVerified) {
    $script:step = $Name
    $stats.Steps++
    $before = @(Get-UnfinishedJobs | ForEach-Object { $_.Id })
    $run = Invoke-Ferry $FerryArgs $true
    if ($run.Killed) {
        $new = @(Get-UnfinishedJobs | Where-Object { $before -notcontains $_.Id } | Sort-Object Date -Descending)
        if ($new.Count -eq 0) {
            # Killed before the job was saved: nothing to resume, so start the step again from scratch.
            $stats.EarlyKills++
            Write-Host "  [$Name] killed before the job was saved; starting it again" -ForegroundColor DarkYellow
            $run = Invoke-Ferry $FerryArgs $false
        }
        else {
            $stats.Resumes++
            $script:step = "$Name-resume"
            Write-Host "  [$Name] resuming job $($new[0].Id)" -ForegroundColor DarkYellow
            $run = Invoke-Ferry @('resume', $new[0].Id, '--yes', '--json') $false
        }
    }
    if (-not (Test-JobOk $run $RequireVerified)) {
        $stats.JobFailures++
        $problems.Add("cycle $($script:cycle) $Name : job did not finish cleanly (exit $($run.ExitCode)); output $($run.Out)")
        return $false
    }
    return $true
}

function Invoke-Verify([string]$Name, [string]$A, [string]$B) {
    $script:step = $Name
    $stats.Verifies++
    $run = Invoke-Ferry @('verify', $A, $B, '--json') $false
    if ($run.Killed -or $run.ExitCode -ne 0) {
        $stats.VerifyFailures++
        $problems.Add("cycle $($script:cycle) $Name : verify failed (exit $($run.ExitCode)); output $($run.Out)")
        return $false
    }
    return $true
}

function Test-LeftoverParts([string]$Name) {
    $parts = @(foreach ($r in $DestRoot, $MoveRoot) { if ($r) { Get-ChildItem -LiteralPath $r -Recurse -Force -File -Filter '*.ferrypart' -ErrorAction SilentlyContinue } })
    if ($parts.Count -gt 0) {
        $stats.LeftoverParts += $parts.Count
        $problems.Add("cycle $($script:cycle) after $Name : $($parts.Count) leftover .ferrypart file(s), first $($parts[0].Name)")
    }
}

# ---- the loop --------------------------------------------------------------------------------------------------------------------------
$endAt = $Hours * 3600
$copyDir = Join-Path $DestRoot 'copy'
$movedDir = if ($MoveRoot) { $MoveRoot } else { Join-Path $DestRoot 'moved' }
$failedCycles = 0
$evidenceKept = $false

# Lists what is left in the output folders, so a failed cycle can be examined.
function Save-Evidence {
    $lines = foreach ($r in $DestRoot, $MoveRoot) {
        if ($r -and (Test-Path -LiteralPath $r)) {
            "== $r"
            foreach ($f in @(Get-ChildItem -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue)) { '{0}  {1}' -f $f.Length, $f.FullName.Substring($r.Length) }
        }
    }
    $lines | Set-Content -Encoding utf8 (Join-Path $outDir "failed-cycle-$($script:cycle).txt")
}
try {
    while ($clock.Elapsed.TotalSeconds -lt $endAt -and $failedCycles -eq 0) {
        $script:cycle++
        Write-Host ("==> cycle {0} ({1:F1} of {2:F1} min)" -f $script:cycle, ($clock.Elapsed.TotalMinutes), ($endAt / 60)) -ForegroundColor Cyan
        $ok = Invoke-Job 'copy' @('copy', $Source, $copyDir, '--yes', '--if-exists', 'overwrite', '--json', '--limit', $limitText) $true
        if ($ok) {
            Test-LeftoverParts 'copy'
            $ok = Invoke-Verify 'verify-copy' $Source (Join-Path $copyDir $leaf)
        }
        if ($ok) {
            # The move works on the COPY, never on the source.
            $ok = Invoke-Job 'move' @('copy', (Join-Path $copyDir $leaf), $movedDir, '--move', '--yes', '--if-exists', 'overwrite', '--json', '--limit', $limitText) $false
        }
        if ($ok) {
            Test-LeftoverParts 'move'
            $ok = Invoke-Verify 'verify-moved' $Source (Join-Path $movedDir $leaf)
        }
        $script:step = 'cleanup'
        if ($ok) {
            try {
                Clear-Owned $DestRoot
                if ($MoveRoot) { Clear-Owned $MoveRoot }
                $stats.Cycles++
            }
            catch { $ok = $false; $problems.Add("cycle $($script:cycle) cleanup failed: $($_.Exception.Message)") }
        }
        if (-not $ok) {
            # Keep the evidence: the output folders stay as they are, and the run stops.
            $failedCycles++
            $evidenceKept = $true
            try { Save-Evidence } catch { }
        }
        Write-Host ("    {0}; pauses {1}, kills {2}, verify failures {3}, job failures {4}" -f $(if ($ok) { 'cycle ok' } else { 'cycle FAILED' }), $stats.Pauses, $stats.Kills, $stats.VerifyFailures, $stats.JobFailures)
    }
}
finally {
    if ($null -ne $script:current -and -not $script:current.HasExited) {
        if ($script:suspended) { [void][SoakNative]::NtResumeProcess($script:current.Handle) }
        Stop-Process -Id $script:current.Id -Force -ErrorAction SilentlyContinue
    }
}
if (-not $evidenceKept) {
    try { Clear-Owned $DestRoot; if ($MoveRoot) { Clear-Owned $MoveRoot } } catch { $problems.Add("final cleanup failed: $($_.Exception.Message)") }
}
$totalSeconds = $clock.Elapsed.TotalSeconds

# ---- slopes ----------------------------------------------------------------------------------------------------------------------------
# Least-squares slope of the value against time, as a percentage of the mean value per hour.
function Get-SlopePercent([object[]]$Rows, [string]$Field) {
    $n = $Rows.Count
    $xs = @($Rows | ForEach-Object { $_.Elapsed / 3600.0 })
    $ys = @($Rows | ForEach-Object { [double]$_.$Field })
    $mx = ($xs | Measure-Object -Average).Average
    $my = ($ys | Measure-Object -Average).Average
    $sxx = 0.0; $sxy = 0.0
    for ($i = 0; $i -lt $n; $i++) { $sxx += ($xs[$i] - $mx) * ($xs[$i] - $mx); $sxy += ($xs[$i] - $mx) * ($ys[$i] - $my) }
    if ($sxx -le 0 -or $my -le 0) { return $null }
    return 100.0 * ($sxy / $sxx) / $my
}

$windowStart = [math]::Max($totalSeconds - 3 * 3600, 0.25 * $totalSeconds)
$window = @($samples | Where-Object { $_.Elapsed -ge $windowStart })
$spanMinutes = if ($window.Count -ge 2) { ($window[-1].Elapsed - $window[0].Elapsed) / 60 } else { 0 }
$meaningful = ($window.Count -ge 10 -and $spanMinutes -ge 10)
$wsSlope = $null; $handleSlope = $null
if ($meaningful) {
    $wsSlope = Get-SlopePercent $window 'Ws'
    $handleSlope = Get-SlopePercent $window 'Handles'
}
function Format-Slope($Value) { if ($null -eq $Value) { 'n/a' } else { '{0:+0.00;-0.00;0.00} %/h' -f $Value } }

$growth = @()
if ($null -ne $wsSlope -and $wsSlope -ge 1) { $growth += 'working set grows' }
if ($null -ne $handleSlope -and $handleSlope -ge 1) { $growth += 'handle count grows' }

$pass = ($stats.Cycles -gt 0 -and $stats.VerifyFailures -eq 0 -and $stats.JobFailures -eq 0 -and $stats.LeftoverParts -eq 0 -and $growth.Count -eq 0 -and $failedCycles -eq 0)
$writtenGB = [math]::Round($stats.Steps * $sourceBytes / 1e9, 1)
$slopeNote = if ($meaningful) { '' } else { " (slopes not meaningful: $($window.Count) samples over $([math]::Round($spanMinutes)) min in the window; needs 10 or more samples over 10 min or more)" }
$line = "{0}: {1} cycles, {2} pauses, {3} kills+resumes ({4} killed before the job was saved), {5} verify failures, {6} job failures, {7} leftover parts, working set {8}, handles {9}, at most about {10} GB written (steps x source size){11}" -f
    $(if ($pass) { 'SOAK PASS' } else { 'SOAK FAIL' }), $stats.Cycles, $stats.Pauses, $stats.Resumes, $stats.EarlyKills, $stats.VerifyFailures, $stats.JobFailures,
    $stats.LeftoverParts, (Format-Slope $wsSlope), (Format-Slope $handleSlope), $writtenGB, $slopeNote

$md = @(
    '# Ferry soak test',
    '',
    $line,
    '',
    "- Run: $([math]::Round($totalSeconds / 60, 1)) min, seed $Seed, limit $limitText, $($files.Count) files / $([math]::Round($sourceBytes / 1MB)) MiB per copy.",
    "- Samples: $($samples.Count) total, $($window.Count) in the slope window ($([math]::Round($spanMinutes)) min). Log: artifacts/soak/samples.csv.",
    '- Slope caveat: samples come from many short-lived ferry.exe processes (copy, move, verify, resume); the slope is fitted across all samples in the window, so a mix of step types can bias it. Read the CSV for a single long-lived process.',
    "- Data written: at most steps x source size ($($stats.Steps) steps). A move on one volume may not rewrite the data; a resumed job only writes what was left. The start-up estimate ($estGB GB) was limit x time or one cycle, whichever is larger.",
    '- `ferry resume` takes no --limit, so resumed jobs ran at full speed.',
    "- $moveNote."
)
if ($evidenceKept) { $md += '- A cycle failed: the output folders were NOT emptied, and artifacts/soak/failed-cycle-N.txt lists them. The next run empties them.' }
if ($problems.Count -gt 0) { $md += ''; $md += '## Problems'; foreach ($p in $problems) { $md += "- $p" } }
$md | Set-Content -Encoding utf8 (Join-Path $outDir 'summary.md')

if ($pass) { Write-Host $line -ForegroundColor Green } else { Write-Host $line -ForegroundColor Red; foreach ($p in $problems) { Write-Host "  $p" } }
if ($pass) { exit 0 } else { exit 1 }
