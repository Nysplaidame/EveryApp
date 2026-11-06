<#
.SYNOPSIS
  Restore, format-check, build and test the solution. CI calls exactly this script.
.PARAMETER Configuration
  Release (default) or Debug.
.PARAMETER SkipTests
  Skip the test step.
.PARAMETER Sign
  Reserved for release signing (wired up in a later milestone).
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Sign
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$solution = Join-Path $root 'EveryApp.slnx'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Command)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Step '$Name' failed with exit code $LASTEXITCODE."
    }
}

Push-Location $root
try {
    $hasLockFiles = [bool](Get-ChildItem -Path $root -Recurse -Filter 'packages.lock.json' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\/](obj|bin|artifacts)[\/]' } |
            Select-Object -First 1)

    if ($hasLockFiles) {
        Invoke-Step 'restore (locked)' { dotnet restore $solution --locked-mode }
    }
    else {
        Write-Host 'No lock files found: generating them (commit the result).' -ForegroundColor Yellow
        Invoke-Step 'restore (generate lock files)' { dotnet restore $solution }
    }

    Invoke-Step 'format check' { dotnet format $solution --verify-no-changes --no-restore }
    Invoke-Step 'build' { dotnet build $solution -c $Configuration --no-restore }

    if (-not $SkipTests) {
        Invoke-Step 'test' { dotnet test --solution $solution -c $Configuration --no-build }
    }

    if ($Sign) {
        Write-Warning 'Signing is not wired up yet; -Sign is a no-op in this milestone.'
    }

    Write-Host 'Build OK.' -ForegroundColor Green
}
finally {
    Pop-Location
}
