<#
.SYNOPSIS
  Removes everything register.ps1 added (HKCU only). Safe to run more than once.
.DESCRIPTION
  Removes the handler keys and the CLSID, the "Approved" entry, and any parent keys that register.ps1 itself
  created, but only if they are now empty (no values and no sub-keys), so nothing another program added is touched.
  Explorer may keep the DLL loaded until you sign out, so the DLL file can stay locked until then.
  The menu items stop appearing in new windows straight away.
#>
[CmdletBinding()]
param(
    # Test only: must match the -RegistryRoot given to register.ps1. Must start with HKCU:\.
    [string]$RegistryRoot = 'HKCU:\Software'
)
if ($RegistryRoot -notlike 'HKCU:\*') { throw 'RegistryRoot must be under HKCU:\.' }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$clsid = '{4A158046-8ED4-476F-B58B-19D7B7D775FE}'
$classes = "$RegistryRoot\Classes"
$clsidKey = "$classes\CLSID\$clsid"
$approved = "$RegistryRoot\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"

# Read the list of parent keys register.ps1 created, before the CLSID key (which holds it) is deleted.
$created = @()
if (Test-Path $clsidKey) {
    $prop = Get-ItemProperty -Path $clsidKey -Name 'FerrySpikeCreatedKeys' -ErrorAction SilentlyContinue
    if ($prop) { $created = @($prop.FerrySpikeCreatedKeys) }
}

foreach ($type in 'Directory', 'Folder', 'Drive') {
    $key = "$classes\$type\shellex\DragDropHandlers\FerrySpike"
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force }
}

if (Test-Path $clsidKey) { Remove-Item -Path $clsidKey -Recurse -Force }

if (Test-Path $approved) { Remove-ItemProperty -Path $approved -Name $clsid -ErrorAction SilentlyContinue }

function Test-EmptyKey([string]$Path) {
    $key = Get-Item -Path $Path
    # Property count excludes the (default) value; check that separately.
    $default = $key.GetValue('')
    return ($key.SubKeyCount -eq 0) -and ($key.ValueCount -eq 0) -and ($null -eq $default -or $default -eq '')
}

# Deepest first, so DragDropHandlers goes before shellex before the type key.
$removed = 0
foreach ($path in ($created | Sort-Object { $_.Length } -Descending)) {
    if ((Test-Path $path) -and (Test-EmptyKey $path)) {
        Remove-Item -Path $path -Force
        $removed++
    }
}

Write-Host "Unregistered. Also removed $removed empty key(s) that register.ps1 had created." -ForegroundColor Green
Write-Host 'New Explorer windows will no longer show the "with Ferry (test)" items.'
Write-Host 'To unload the DLL completely, sign out and back in.'
