<#
.SYNOPSIS
  Test helper: a "hung Ferry". Opens the spike pipe with a tiny buffer, accepts one connection and never reads,
  so the handler's write cannot complete and its timeout + fallback path runs. Exits after -Seconds.
  Used with harness.exe only (outside Explorer). Nothing is copied.
#>
[CmdletBinding()]
param([int]$Seconds = 20)

$ErrorActionPreference = 'Stop'
$server = New-Object IO.Pipes.NamedPipeServerStream('FerrySpike-DragDrop', [IO.Pipes.PipeDirection]::In, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 1, 0)
$wait = $server.WaitForConnectionAsync()
Write-Host 'Hung server listening (will accept and never read).'
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
    if ($wait.IsCompleted -and -not $said) { Write-Host 'Client connected; not reading.'; $said = $true }
    Start-Sleep -Milliseconds 200
}
$server.Dispose()
Write-Host 'Hung server stopped.'
