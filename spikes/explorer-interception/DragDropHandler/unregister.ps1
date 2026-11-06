<#
.SYNOPSIS
  Removes everything register.ps1 added (HKCU only). Safe to run more than once.
.DESCRIPTION
  Explorer may keep the DLL loaded until it is restarted or you sign out, so the DLL file
  can stay locked until then. The menu items stop appearing in new windows straight away.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$clsid = '{4A158046-8ED4-476F-B58B-19D7B7D775FE}'
$classes = 'HKCU:\Software\Classes'

foreach ($type in 'Directory', 'Folder', 'Drive') {
    $key = "$classes\$type\shellex\DragDropHandlers\FerrySpike"
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force }
}

$clsidKey = "$classes\CLSID\$clsid"
if (Test-Path $clsidKey) { Remove-Item -Path $clsidKey -Recurse -Force }

$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
if (Test-Path $approved) { Remove-ItemProperty -Path $approved -Name $clsid -ErrorAction SilentlyContinue }

Write-Host 'Unregistered. New Explorer windows will no longer show the "with Ferry (test)" items.' -ForegroundColor Green
Write-Host 'To unload the DLL completely, sign out and back in (or restart Explorer yourself).'
