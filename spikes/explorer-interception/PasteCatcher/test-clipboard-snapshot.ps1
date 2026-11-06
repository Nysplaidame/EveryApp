<#
.SYNOPSIS
  Non-interactive check of the clipboard snapshot design. No keystrokes, no Explorer.
.DESCRIPTION
  1. Starts PasteCatcher (hidden console, stops by itself).
  2. From THIS process, puts two dummy %TEMP% files on the clipboard as a file drop, twice:
     a. "cut" (Preferred DropEffect = 2) with delayed rendering, like Explorer does (data stays in this process);
     b. "copy" (Preferred DropEffect = 5), flushed.
  3. Checks PasteCatcher logged a snapshot for each, with the same clipboard sequence number this process sees,
     and that no thread other than the clipboard watcher ever opened the clipboard.
  Your clipboard text (if any) is restored afterwards; other clipboard content is replaced by the test.
#>
[CmdletBinding()]
param([string]$Exe = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Exe) { $Exe = Join-Path $here 'bin\Release\net10.0-windows\PasteCatcher.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "Not found: $Exe. Build first." }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell (STA).' }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace FerrySpike -Name Clip -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();'

$savedText = $null
if ([Windows.Forms.Clipboard]::ContainsText()) { $savedText = [Windows.Forms.Clipboard]::GetText() }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('FerrySpikeClip-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$files = @('dummy-a.txt', 'dummy-b.txt') | ForEach-Object { $p = Join-Path $dir $_; Set-Content -LiteralPath $p 'dummy'; $p }

$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = '--exit-after 8'
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$proc = [Diagnostics.Process]::Start($psi)
$outTask = $proc.StandardOutput.ReadToEndAsync()
Start-Sleep -Seconds 2

function Set-FileDrop([string[]]$Paths, [int]$Effect, [bool]$Flush) {
    $data = New-Object Windows.Forms.DataObject
    $list = New-Object Collections.Specialized.StringCollection
    $list.AddRange($Paths)
    $data.SetFileDropList($list)
    $data.SetData('Preferred DropEffect', (New-Object IO.MemoryStream(, [BitConverter]::GetBytes($Effect))))
    [Windows.Forms.Clipboard]::SetDataObject($data, $Flush)
}

try {
    Set-FileDrop $files 2 $false
    Start-Sleep -Milliseconds 1500
    $seqCut = [FerrySpike.Clip]::GetClipboardSequenceNumber()
    [Windows.Forms.Application]::DoEvents()   # let OLE serve the delayed render request

    Set-FileDrop $files 5 $true
    Start-Sleep -Milliseconds 1500
    $seqCopy = [FerrySpike.Clip]::GetClipboardSequenceNumber()

    $proc.WaitForExit(15000) | Out-Null
    $out = $outTask.Result
}
finally {
    if ($null -ne $savedText) { [Windows.Forms.Clipboard]::SetText($savedText) } else { [Windows.Forms.Clipboard]::Clear() }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '--- PasteCatcher output ---'
Write-Host $out
Write-Host "Test process saw sequence numbers: after cut = $seqCut, after copy = $seqCopy"

$fail = @()
if ($out -notmatch "sequence $seqCut\): 2 file\(s\), cut \(move\), Preferred DropEffect=2") { $fail += 'no matching snapshot for the cut' }
if ($out -notmatch "sequence $seqCopy\): 2 file\(s\), copy, Preferred DropEffect=5") { $fail += 'no matching snapshot for the copy' }
if ($out -notmatch 'any other thread \(paste path\) 0,') { $fail += 'the clipboard was opened outside the watcher thread' }
if ($out -notmatch 'Hook removed: True') { $fail += 'no clean exit' }
if ($fail) { throw ('FAIL: ' + ($fail -join '; ')) }
Write-Host 'PASS: snapshots taken on clipboard change with matching sequence numbers; paste path never opened the clipboard.' -ForegroundColor Green
