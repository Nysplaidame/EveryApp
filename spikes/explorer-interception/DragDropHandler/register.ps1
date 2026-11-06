<#
.SYNOPSIS
  Registers the Ferry spike right-drag handler for the CURRENT USER only (HKCU). No admin rights needed.
.DESCRIPTION
  Writes only these keys under HKCU\Software\Classes and HKCU\...\Shell Extensions\Approved:
    CLSID\{4A158046-8ED4-476F-B58B-19D7B7D775FE}  (points at the built DLL)
    Directory\shellex\DragDropHandlers\FerrySpike
    Folder\shellex\DragDropHandlers\FerrySpike
    Drive\shellex\DragDropHandlers\FerrySpike
  It does not restart Explorer. New Explorer windows usually pick it up; if not, sign out and back in.
  Undo with unregister.ps1.
.PARAMETER DllPath
  Path to FerrySpikeDragDrop.dll. Defaults to bin\FerrySpikeDragDrop.dll next to this script.
#>
[CmdletBinding()]
param(
    [string]$DllPath = '',
    # Test only: write under another HKCU sub-tree that Explorer never reads. Must start with HKCU:\.
    [string]$RegistryRoot = 'HKCU:\Software'
)
if ($RegistryRoot -notlike 'HKCU:\*') { throw 'RegistryRoot must be under HKCU:\.' }

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 can leave $PSScriptRoot empty inside param defaults, so resolve it here.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $DllPath) { $DllPath = Join-Path $here 'bin\FerrySpikeDragDrop.dll' }

$clsid = '{4A158046-8ED4-476F-B58B-19D7B7D775FE}'
$name = 'Ferry spike drag-drop handler (test)'

if (-not (Test-Path -LiteralPath $DllPath)) { throw "DLL not found: $DllPath. Run build.cmd first." }
$dll = (Resolve-Path -LiteralPath $DllPath).ProviderPath

$classes = "$RegistryRoot\Classes"
$clsidKey = "$classes\CLSID\$clsid"
$approved = "$RegistryRoot\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"

# Remember which parent keys did not exist before, so unregister.ps1 can remove exactly those (if still empty).
# Merged with any list from an earlier run, so registering twice does not forget what the first run created.
$created = New-Object System.Collections.Generic.List[string]
if (Test-Path $clsidKey) {
    $prev = (Get-ItemProperty -Path $clsidKey -Name 'FerrySpikeCreatedKeys' -ErrorAction SilentlyContinue)
    if ($prev) { foreach ($k in $prev.FerrySpikeCreatedKeys) { $created.Add($k) } }
}
$parents = @()
foreach ($type in 'Directory', 'Folder', 'Drive') {
    $parents += "$classes\$type", "$classes\$type\shellex", "$classes\$type\shellex\DragDropHandlers"
}
$parents += "$classes\CLSID", $approved
foreach ($p in $parents) {
    if (-not (Test-Path $p) -and -not $created.Contains($p)) { $created.Add($p) }
}

New-Item -Path "$clsidKey\InprocServer32" -Force | Out-Null
Set-Item -Path $clsidKey -Value $name
Set-Item -Path "$clsidKey\InprocServer32" -Value $dll
New-ItemProperty -Path "$clsidKey\InprocServer32" -Name 'ThreadingModel' -Value 'Apartment' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $clsidKey -Name 'FerrySpikeCreatedKeys' -Value ([string[]]$created) -PropertyType MultiString -Force | Out-Null

foreach ($type in 'Directory', 'Folder', 'Drive') {
    $key = "$classes\$type\shellex\DragDropHandlers\FerrySpike"
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $clsid
}

if (-not (Test-Path $approved)) { New-Item -Path $approved -Force | Out-Null }
New-ItemProperty -Path $approved -Name $clsid -Value $name -PropertyType String -Force | Out-Null

Write-Host "Registered for this user: $dll" -ForegroundColor Green
Write-Host 'Open a NEW File Explorer window, then right-drag a file onto a folder.'
Write-Host 'If the "with Ferry (test)" items do not show, sign out and back in. Undo with unregister.ps1.'
