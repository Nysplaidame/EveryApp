<#
.SYNOPSIS
  Counter for the 200-drag stability check. You do the drags; this window only watches and counts.
.DESCRIPTION
  - Listens on the spike pipe and counts jobs the handler hands over ("Copy/Move here with Ferry (test)").
  - Counts right-drag menus shown and jobs from the handler's log file.
  - Watches File Explorer: if its process ID changes or Windows logs an Explorer crash, it says so in red.
  - Shows Explorer's handle count and memory so slow leaks are visible.
  Stop with Ctrl+C. Nothing is copied or changed.
.PARAMETER Target
  How many drags you plan to do (default 200).
.PARAMETER NoPipe
  Do not listen on the pipe, to test what the handler does when Ferry is not running.
#>
[CmdletBinding()]
param(
    [int]$Target = 200,
    [switch]$NoPipe,
    [int]$ExitAfterSeconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pipeName = 'FerrySpike-DragDrop'
$logFile = Join-Path $env:LOCALAPPDATA 'FerrySpike\dragdrop.log'
$start = Get-Date
$received = 0
$lastBody = ''

function Get-ExplorerState {
    $procs = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
    [pscustomobject]@{
        Pids    = (($procs | ForEach-Object { $_.Id } | Sort-Object) -join ',')
        Handles = ($procs | Measure-Object -Property HandleCount -Sum).Sum
        MemMB   = [math]::Round((($procs | Measure-Object -Property WorkingSet64 -Sum).Sum) / 1MB)
    }
}

function Get-LogCounts {
    $menus = 0; $invokes = 0; $fallbacks = 0
    if (Test-Path -LiteralPath $logFile) {
        foreach ($line in [IO.File]::ReadAllLines($logFile)) {
            if ($line -match ' event=menu ') { $menus++ }
            elseif ($line -match ' event=invoke ') { $invokes++; if ($line -match 'fall back') { $fallbacks++ } }
        }
    }
    [pscustomobject]@{ Menus = $menus; Invokes = $invokes; Fallbacks = $fallbacks }
}

$baseline = Get-LogCounts
$explorer0 = Get-ExplorerState
Write-Host "Watching. Explorer process: $($explorer0.Pids), handles $($explorer0.Handles), memory $($explorer0.MemMB) MB."
Write-Host ("Pipe: " + $(if ($NoPipe) { 'OFF (testing the "Ferry not running" case)' } else { "listening on \\.\pipe\$pipeName" }))
Write-Host "Goal: $Target drags. Press Ctrl+C to stop.`n"

$server = $null
$pending = $null
$lastStatus = ''
$lastCrashCheck = Get-Date
$crashes = 0
$doneShown = $false
try {
    while ($true) {
        if (-not $NoPipe) {
            if (-not $server) {
                $server = New-Object IO.Pipes.NamedPipeServerStream($pipeName, [IO.Pipes.PipeDirection]::In, 1,
                    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 65536, 0)
                $pending = $server.WaitForConnectionAsync()
            }
            if ($pending.IsCompleted) {
                $reader = New-Object IO.StreamReader($server, [Text.Encoding]::Unicode)
                $lastBody = $reader.ReadToEnd()
                $reader.Dispose()
                $server = $null
                $received++
                $lines = $lastBody -split "`n"
                $op = ($lines | Where-Object { $_ -like 'op=*' } | Select-Object -First 1)
                $count = ($lines | Where-Object { $_ -like 'count=*' } | Select-Object -First 1)
                Write-Host ("  job #{0} received: {1}, {2}" -f $received, $op, $count) -ForegroundColor Green
                # Listen again straight away so the next drag finds the pipe.
                $server = New-Object IO.Pipes.NamedPipeServerStream($pipeName, [IO.Pipes.PipeDirection]::In, 1,
                    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 65536, 0)
                $pending = $server.WaitForConnectionAsync()
            }
        }

        $now = Get-Date
        $ex = Get-ExplorerState
        $c = Get-LogCounts
        $menus = $c.Menus - $baseline.Menus
        $invokes = $c.Invokes - $baseline.Invokes
        $fallbacks = $c.Fallbacks - $baseline.Fallbacks

        if (($now - $lastCrashCheck).TotalSeconds -ge 10) {
            $lastCrashCheck = $now
            $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1002; StartTime = $start } -ErrorAction SilentlyContinue |
                    Where-Object { $_.Message -match 'explorer\.exe' })
            if ($events.Count -ne $crashes) {
                $crashes = $events.Count
                Write-Host "  WARNING: Windows logged $crashes Explorer crash/hang event(s) since the start." -ForegroundColor Red
            }
        }

        if ($ex.Pids -ne $explorer0.Pids) {
            Write-Host "  WARNING: Explorer process changed ($($explorer0.Pids) -> $($ex.Pids)). Explorer restarted or crashed." -ForegroundColor Red
            $explorer0 = $ex
        }

        $status = "Drags (menus shown): {0}/{1} | Ferry items chosen: {2} | jobs received on pipe: {3} | fallbacks: {4} | Explorer handles {5}, memory {6} MB | crashes {7}" -f `
            $menus, $Target, $invokes, $received, $fallbacks, $ex.Handles, $ex.MemMB, $crashes
        if ($status -ne $lastStatus) {
            Write-Host $status
            $lastStatus = $status
        }
        if ($menus -ge $Target -and -not $doneShown) {
            Write-Host "DONE: $Target drags reached. Write down the last line above for the ADR." -ForegroundColor Cyan
            $doneShown = $true
        }
        if ($ExitAfterSeconds -gt 0 -and ($now - $start).TotalSeconds -ge $ExitAfterSeconds) { break }
        Start-Sleep -Milliseconds 200
    }
}
finally {
    if ($server) { $server.Dispose() }
    Write-Host "Stopped. Jobs received on pipe: $received."
}
