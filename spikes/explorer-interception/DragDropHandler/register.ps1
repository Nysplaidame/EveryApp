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
param([string]$DllPath = (Join-Path $PSScriptRoot 'bin\FerrySpikeDragDrop.dll'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$clsid = '{4A158046-8ED4-476F-B58B-19D7B7D775FE}'
$name = 'Ferry spike drag-drop handler (test)'

if (-not (Test-Path -LiteralPath $DllPath)) { throw "DLL not found: $DllPath. Run build.cmd first." }
$dll = (Resolve-Path -LiteralPath $DllPath).ProviderPath

$classes = 'HKCU:\Software\Classes'
$clsidKey = "$classes\CLSID\$clsid"
New-Item -Path "$clsidKey\InprocServer32" -Force | Out-Null
Set-Item -Path $clsidKey -Value $name
Set-Item -Path "$clsidKey\InprocServer32" -Value $dll
New-ItemProperty -Path "$clsidKey\InprocServer32" -Name 'ThreadingModel' -Value 'Apartment' -PropertyType String -Force | Out-Null

foreach ($type in 'Directory', 'Folder', 'Drive') {
    $key = "$classes\$type\shellex\DragDropHandlers\FerrySpike"
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $clsid
}

$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
if (-not (Test-Path $approved)) { New-Item -Path $approved -Force | Out-Null }
New-ItemProperty -Path $approved -Name $clsid -Value $name -PropertyType String -Force | Out-Null

Write-Host "Registered for this user: $dll" -ForegroundColor Green
Write-Host 'Open a NEW File Explorer window, then right-drag a file onto a folder.'
Write-Host 'If the "with Ferry (test)" items do not show, sign out and back in. Undo with unregister.ps1.'
