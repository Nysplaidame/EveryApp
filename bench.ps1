<#
.SYNOPSIS
  Ferry speed benchmarks (M1 plan, Task 16). Not part of build.ps1 or CI: it needs real drives and FastCopy.
.DESCRIPTION
  Reads bench.local.json (git-ignored; copy bench.example.json). For each set it runs FastCopy and `ferry copy`, verify off and on,
  each tool verify off then on back to back, alternating which tool goes first each round, clearing the destination before every
  run and the standby cache when run as administrator.
  Writes the medians and ratios to artifacts/bench/comparison.md and .json. Drive models only: no drive letters or paths in the output.
.PARAMETER Generate
  Create each set's source data first (only into a missing or empty folder).
.PARAMETER Sweep
  Also run the tuning sweep (Ferry.Bench sweep) on each set.
.PARAMETER Micro
  Run the BenchmarkDotNet micro-benchmarks instead of the comparison.
.PARAMETER MicroFilter
  Which micro-benchmarks to run (default all), e.g. '*Hash*'. The copy one writes a few GB to the temp folder.
.PARAMETER Rounds
  Rounds per set (default 3, median reported).
.PARAMETER Only
  Run only the sets whose name matches this wildcard.
#>
[CmdletBinding()]
param(
    [switch]$Generate,
    [switch]$Sweep,
    [switch]$Micro,
    [ValidateRange(1, 20)][int]$Rounds = 3,
    [string]$Only = '*',
    [string]$MicroFilter = '*',
    [string]$Config = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Config) { $Config = Join-Path $root 'bench.local.json' } # Windows PowerShell leaves $PSScriptRoot empty in parameter defaults
$outDir = Join-Path $root 'artifacts\bench'
$ferry = Join-Path $root 'artifacts\bin\Ferry.Cli\release_win-x64\ferry.exe'
$bench = Join-Path $root 'artifacts\bin\Ferry.Bench\release_win-x64\Ferry.Bench.exe'

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) failed with exit code $LASTEXITCODE." }
}

Write-Host '==> build (Release)' -ForegroundColor Cyan
foreach ($project in 'src\Ferry.Cli\Ferry.Cli.csproj', 'bench\Ferry.Bench\Ferry.Bench.csproj') {
    Invoke-Native dotnet @('build', (Join-Path $root $project), '-c', 'Release', '--nologo', '-v', 'q')
}

New-Item -ItemType Directory -Force $outDir | Out-Null
if ($Micro) {
    Push-Location $root # BenchmarkDotNet finds the project from the current folder
    try { Invoke-Native $bench @('micro', '--filter', $MicroFilter, '--artifacts', (Join-Path $outDir 'micro')) }
    finally { Pop-Location }
    return
}

if (-not (Test-Path -LiteralPath $Config)) { throw "No $Config. Copy bench.example.json to bench.local.json and fill in your drives." }
$cfg = Get-Content -Raw -LiteralPath $Config | ConvertFrom-Json
$fastCopy = $cfg.fastCopyExe
if (-not (Test-Path -LiteralPath $fastCopy)) { throw 'fastCopyExe in the config does not point to FastCopy.exe.' }

function Get-DriveModel([string]$Path) {
    try {
        $letter = ([IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $Path).Path)).Substring(0, 1)
        return (Get-Partition -DriveLetter $letter | Get-Disk).FriendlyName
    }
    catch { return 'unknown drive' }
}

function Get-TreeSize([string]$Path) {
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force)
    [pscustomobject]@{ Count = $files.Count; Bytes = ($files | Measure-Object Length -Sum).Sum }
}

$markerName = '.ferry-bench-out'

# Empties a destination folder, but only one this script owns: new, empty, or marked by this script. Never a drive root. Links inside are
# removed as links, never followed.
function Clear-Destination([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Length -le 3) { throw "Refusing to empty a drive root: $full" }
    $marker = Join-Path $full $markerName
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $marker) -and (Get-ChildItem -LiteralPath $full -Force | Select-Object -First 1)) {
            throw "Refusing to empty $full : it is not empty and was not made by bench.ps1 (no $markerName file). Pick an empty folder."
        }
    }
    else {
        New-Item -ItemType Directory $full | Out-Null
    }
    if (-not (Test-Path -LiteralPath $marker)) { Set-Content -LiteralPath $marker -Value 'Ferry benchmark output: bench.ps1 empties this folder before every run.' }
    foreach ($child in @(Get-ChildItem -LiteralPath $full -Force | Where-Object Name -ne $markerName)) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { if ($child.PSIsContainer) { [IO.Directory]::Delete($child.FullName, $false) } else { [IO.File]::Delete($child.FullName) } }
        elseif ($child.PSIsContainer) { [IO.Directory]::Delete($child.FullName, $true) }
        else { [IO.File]::Delete($child.FullName) }
    }
}

function Assert-Separate([string]$Source, [string]$Dest) {
    $s = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
    $d = [IO.Path]::GetFullPath($Dest).TrimEnd('\') + '\'
    if ($s.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) -or $d.StartsWith($s, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The destination must not be the source, inside it, or around it: $Dest"
    }
}

$script:cacheCleared = $true
function Clear-StandbyCache {
    # Windows PowerShell turns a native tool's error output into a terminating error under 'Stop'; failure here is expected when not elevated.
    $ErrorActionPreference = 'Continue'
    & $bench flush 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $script:cacheCleared = $false }
}

function Invoke-Timed([string]$Tool, [bool]$Verify, [string]$Source, [string]$Dest) {
    Clear-Destination $Dest
    Clear-StandbyCache
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($Tool -eq 'FastCopy') {
        $fcArgs = @('/cmd=force_copy', '/auto_close', '/no_ui')
        if ($Verify) { $fcArgs += '/verify' }
        $fcArgs += @("`"$Source`"", "/to=`"$Dest\`"")
        $p = Start-Process -FilePath $fastCopy -ArgumentList $fcArgs -Wait -PassThru -WindowStyle Hidden
        $code = $p.ExitCode
    }
    else {
        $fArgs = @('copy', $Source, $Dest, '--yes', '--if-exists', 'overwrite')
        if (-not $Verify) { $fArgs += '--no-verify' }
        & $ferry @fArgs | Out-Null
        $code = $LASTEXITCODE
    }
    $watch.Stop()
    if ($code -ne 0) { throw "$Tool failed with exit code $code." }
    return $watch.Elapsed.TotalSeconds
}

function Get-Median([double[]]$Values) {
    $s = @($Values | Sort-Object)
    $n = $s.Count
    if ($n % 2) { return $s[[math]::Floor($n / 2)] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2
}

$results = @()
foreach ($set in @($cfg.sets | Where-Object { $_.name -like $Only })) {
    $source = $set.source
    Assert-Separate $source $set.dest
    if ($Generate -and (-not (Test-Path -LiteralPath $source) -or -not (Get-ChildItem -LiteralPath $source -Force | Select-Object -First 1))) {
        Write-Host "==> generate $($set.kind) for $($set.name)" -ForegroundColor Cyan
        Invoke-Native $bench @('generate', $set.kind, $source)
    }
    if (-not (Test-Path -LiteralPath $source)) { throw "Set $($set.name): source folder missing. Run with -Generate." }

    $want = Get-TreeSize $source
    $leaf = Split-Path -Leaf $source
    $runs = @{}
    foreach ($key in 'FastCopy|False', 'Ferry|False', 'FastCopy|True', 'Ferry|True') { $runs[$key] = [Collections.Generic.List[double]]::new() }

    Write-Host "==> $($set.name): $($want.Count) files, $([math]::Round($want.Bytes / 1MB)) MiB, $Rounds rounds" -ForegroundColor Cyan
    # Fair order: each tool runs verify off then verify on back to back, and the tool that goes first swaps every round. A run straight
    # after the other tool's run can be slowed by what that run left behind (measured 2026-11-27: Ferry's verify-on run took 45 s alone and
    # 60-80 s straight after FastCopy), so neither tool's verify-on run may always follow the other tool.
    for ($round = 1; $round -le $Rounds; $round++) {
        $tools = if ($round % 2) { 'FastCopy', 'Ferry' } else { 'Ferry', 'FastCopy' }
        foreach ($tool in $tools) {
            foreach ($verify in $false, $true) {
                $seconds = Invoke-Timed $tool $verify $source $set.dest
                $got = Get-TreeSize (Join-Path $set.dest $leaf)
                if ($got.Count -ne $want.Count -or $got.Bytes -ne $want.Bytes) {
                    throw "$tool copied $($got.Count) files / $($got.Bytes) bytes, expected $($want.Count) / $($want.Bytes)."
                }
                $runs["$tool|$verify"].Add($seconds)
                Write-Host ("  round {0}: {1,-8} verify {2,-5} {3,8:F2} s" -f $round, $tool, $verify, $seconds)
            }
        }
    }
    Clear-Destination $set.dest

    foreach ($verify in $false, $true) {
        $fc = Get-Median $runs["FastCopy|$verify"].ToArray()
        $fe = Get-Median $runs["Ferry|$verify"].ToArray()
        $results += [pscustomobject]@{
            Set = $set.name; Gated = [bool]$set.gated; Verify = $verify
            From = Get-DriveModel $source; To = Get-DriveModel $set.dest
            Files = $want.Count; MiB = [math]::Round($want.Bytes / 1MB)
            FastCopyS = [math]::Round($fc, 2); FerryS = [math]::Round($fe, 2); Ratio = [math]::Round($fe / $fc, 3)
            FerryRuns = ($runs["Ferry|$verify"] | ForEach-Object { [math]::Round($_, 2) }) -join ' '
            FastCopyRuns = ($runs["FastCopy|$verify"] | ForEach-Object { [math]::Round($_, 2) }) -join ' '
        }
    }

    if ($Sweep) {
        Write-Host "==> sweep $($set.name)" -ForegroundColor Cyan
        Invoke-Native $bench @('sweep', $source, $set.dest, '--rounds', "$Rounds", '--json', (Join-Path $outDir "sweep-$($set.name).json"))
    }
}

$lines = @(
    "Ferry vs FastCopy, $Rounds rounds, median seconds (lower is better). Ratio = Ferry / FastCopy; the gate is 1.10 or less.",
    "Standby cache cleared between runs: $(if ($script:cacheCleared) { 'yes' } else { 'no (not run as administrator)' }).",
    '',
    '| Set | From -> To | Files | MiB | Verify | FastCopy s | Ferry s | Ratio | Gate |',
    '|---|---|---:|---:|---|---:|---:|---:|---|'
)
foreach ($r in $results) {
    $gate = if (-not $r.Gated) { 'not gated' } elseif ($r.Ratio -le 1.10) { 'pass' } else { 'FAIL' }
    $lines += "| $($r.Set) | $($r.From) -> $($r.To) | $($r.Files) | $($r.MiB) | $(if ($r.Verify) { 'on' } else { 'off' }) | $($r.FastCopyS) | $($r.FerryS) | $($r.Ratio) | $gate |"
}
$lines | Set-Content -Encoding utf8 (Join-Path $outDir 'comparison.md')
$results | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $outDir 'comparison.json')
$lines | ForEach-Object { Write-Host $_ }
