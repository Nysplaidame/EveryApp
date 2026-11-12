<#
.SYNOPSIS
  Non-interactive check that PasteCatcher unhooks cleanly on Ctrl+C. No keystrokes are synthesised:
  PasteCatcher runs in its own hidden console, and a separate helper process attaches to that console and
  raises CTRL_C_EVENT there with GenerateConsoleCtrlEvent (a console signal, not keyboard input).
.PARAMETER Exe
  Path to PasteCatcher.exe. Defaults to the Release build next to this script.
#>
[CmdletBinding()]
param([string]$Exe = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 can leave $PSScriptRoot empty inside param defaults, so resolve it here.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Exe) { $Exe = Join-Path $here 'bin\Release\net10.0-windows\PasteCatcher.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "Not found: $Exe. Build first." }

# CreateNoWindow = CREATE_NO_WINDOW: the child gets its OWN hidden console, so the signal reaches only it.
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = '--exit-after 60'   # safety net: it stops by itself if the signal never arrives
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$proc = [Diagnostics.Process]::Start($psi)
$outTask = $proc.StandardOutput.ReadToEndAsync()
Start-Sleep -Seconds 2

$sender = @'
using System;
using System.Runtime.InteropServices;
public static class CtrlC {
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    public static int Send(uint pid) {
        FreeConsole();
        if (!AttachConsole(pid)) return 10;
        SetConsoleCtrlHandler(IntPtr.Zero, true);       // this helper ignores the signal itself
        return GenerateConsoleCtrlEvent(0, 0) ? 0 : 11; // 0 = CTRL_C_EVENT, to every process on that console
    }
}
'@
$cmd = "Add-Type -TypeDefinition @'`n$sender`n'@; exit [CtrlC]::Send($($proc.Id))"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
$sw = [Diagnostics.Stopwatch]::StartNew()
$helper = Start-Process powershell -ArgumentList "-NoProfile -NonInteractive -EncodedCommand $encoded" -WindowStyle Hidden -PassThru -Wait
$exited = $proc.WaitForExit(10000)
$sw.Stop()

Write-Host "Signal helper exit code: $($helper.ExitCode) (0 = signal sent)"
Write-Host "PasteCatcher exited: $exited, exit code: $(if ($exited) { $proc.ExitCode } else { 'still running' }), $([int]$sw.Elapsed.TotalMilliseconds) ms after the signal was started"
Write-Host '--- PasteCatcher output ---'
Write-Host $outTask.Result
if (-not $exited) { $proc.Kill(); throw 'FAIL: PasteCatcher did not stop on Ctrl+C.' }
if ($outTask.Result -notmatch 'Hook removed: True') { throw 'FAIL: no clean unhook line.' }
Write-Host 'PASS: Ctrl+C stopped PasteCatcher and the hook was removed.' -ForegroundColor Green
