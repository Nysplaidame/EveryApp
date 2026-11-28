<#
.SYNOPSIS
  Makes (or removes) the FerryTest folder on your desktop for the manual Explorer test. Only dummy files.
.DESCRIPTION
  Creates Desktop\FerryTest with:
    From\      5 small files (FerryTest-file1.txt ... FerryTest-file5.txt)
    To\        empty, paste/drop target
    Empty\     empty, paste target for the "take over" tests
    Drop1\     empty, drag target
    Drop2\     empty, drag target
    Many\      300 small dummy files, for "drag many files at once"
    Archive.zip  a small zip, to check Ferry's items do NOT appear when dropping onto a zip
  A hidden marker file (.ferrytest) marks the folder as made by this script. The script refuses to touch a
  FerryTest folder without that marker, so it can never delete a folder you made yourself.
.PARAMETER Remove
  Delete the FerryTest folder (asks first, unless -Force).
.PARAMETER Force
  With -Remove: do not ask.
#>
[CmdletBinding()]
param([switch]$Remove, [switch]$Force)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Join-Path ([Environment]::GetFolderPath('Desktop')) 'FerryTest'
$marker = Join-Path $root '.ferrytest'

if ($Remove) {
    if (-not (Test-Path -LiteralPath $root)) { Write-Host 'Nothing to remove: FerryTest does not exist.'; return }
    if (-not (Test-Path -LiteralPath $marker)) { throw "$root was not made by this script (no marker file). Not touching it." }
    $count = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force).Count
    Write-Host "This deletes $root and the $count test file(s) inside it."
    if (-not $Force) {
        $answer = Read-Host 'Type YES to delete'
        if ($answer -ne 'YES') { Write-Host 'Cancelled. Nothing deleted.'; return }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
    Write-Host 'FerryTest removed.' -ForegroundColor Green
    return
}

if ((Test-Path -LiteralPath $root) -and -not (Test-Path -LiteralPath $marker)) {
    throw "$root already exists and was not made by this script. Rename or move it, then run this again."
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Set-Content -LiteralPath $marker -Value 'Made by make-test-files.ps1 (Ferry Explorer spike). Safe to delete.'
(Get-Item -LiteralPath $marker -Force).Attributes = 'Hidden'

foreach ($sub in 'From', 'To', 'Empty', 'Drop1', 'Drop2', 'Many') {
    New-Item -ItemType Directory -Force -Path (Join-Path $root $sub) | Out-Null
}
1..5 | ForEach-Object { Set-Content -LiteralPath (Join-Path $root "From\FerryTest-file$_.txt") -Value "Ferry test file $_" }
1..300 | ForEach-Object { Set-Content -LiteralPath (Join-Path $root ('Many\FerryTest-many-{0:D3}.txt' -f $_)) -Value "dummy $_" }

$zip = Join-Path $root 'Archive.zip'
if (-not (Test-Path -LiteralPath $zip)) {
    Compress-Archive -LiteralPath (Join-Path $root 'From\FerryTest-file1.txt') -DestinationPath $zip
}

Write-Host "Ready: $root" -ForegroundColor Green
Write-Host 'Folders: From (5 files), To, Empty, Drop1, Drop2, Many (300 files), plus Archive.zip.'
Write-Host 'Remove it later with: make-test-files.ps1 -Remove'
explorer.exe $root
