<#
.SYNOPSIS
  Restore, format-check, build and test the solution. CI calls exactly this script.
.PARAMETER Configuration
  Release (default) or Debug.
.PARAMETER SkipTests
  Skip the test step.
.PARAMETER Sign
  Sign every EveryApp.*.dll, Ferry.*.dll and ferry.exe in artifacts/ and verify each signature is Valid.
  Certificate: EVERYAPP_SIGN_THUMBPRINT (local, cert in CurrentUser\My) or EVERYAPP_SIGN_PFX_BASE64 +
  EVERYAPP_SIGN_PFX_PASSWORD (CI). In CI the public certificate is also added to LocalMachine\Root for the
  duration of the signing step (ephemeral runner only) so the strict Valid check can pass; it is removed afterwards.
.PARAMETER SignOnly
  Implies -Sign. Skips restore, format, build and test and only signs files already present in artifacts/.
  Lets a release workflow keep the signing secrets out of every build and test step.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Sign,
    [switch]$SignOnly
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

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $found) {
        $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd) { $found = $cmd.Source }
    }
    if (-not $found) {
        throw 'signtool.exe not found. Install the Windows SDK (Signing Tools for Desktop Apps) and retry.'
    }
    return $found
}

function Invoke-Signing {
    param([string]$ArtifactsRoot, [string]$Configuration)

    $signtool = Find-SignTool
    $importedThumbprint = $null
    $pfxFile = $null
    $cerFile = $null
    $rootThumbprint = $null
    try {
        if ($env:EVERYAPP_SIGN_PFX_BASE64) {
            # CI: decode the PFX to the runner's temp folder and import it for this user only.
            if (-not $env:EVERYAPP_SIGN_PFX_PASSWORD) { throw 'EVERYAPP_SIGN_PFX_PASSWORD is not set.' }
            $tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
            $pfxFile = Join-Path $tempRoot ('everyapp-' + [guid]::NewGuid().ToString('N') + '.pfx')
            [IO.File]::WriteAllBytes($pfxFile, [Convert]::FromBase64String($env:EVERYAPP_SIGN_PFX_BASE64))
            $securePassword = ConvertTo-SecureString -String $env:EVERYAPP_SIGN_PFX_PASSWORD -AsPlainText -Force
            $imported = Import-PfxCertificate -FilePath $pfxFile -CertStoreLocation Cert:\CurrentUser\My -Password $securePassword
            $thumbprint = $imported.Thumbprint
            $importedThumbprint = $thumbprint

            # Ephemeral CI runner only: trust the public part so the Valid check can pass, removed in finally.
            if ($env:CI -ne 'true' -or -not $env:GITHUB_ACTIONS) {
                throw 'Refusing to trust a certificate machine-wide outside CI. Use EVERYAPP_SIGN_THUMBPRINT locally.'
            }
            $cerFile = Join-Path $tempRoot ('everyapp-' + [guid]::NewGuid().ToString('N') + '.cer')
            [void](Export-Certificate -Cert $imported -FilePath $cerFile)
            [void](Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\Root)
            $rootThumbprint = $thumbprint
        }
        elseif ($env:EVERYAPP_SIGN_THUMBPRINT) {
            $thumbprint = $env:EVERYAPP_SIGN_THUMBPRINT -replace '\s', ''
        }
        else {
            throw 'No signing certificate configured. Set EVERYAPP_SIGN_THUMBPRINT (local) or EVERYAPP_SIGN_PFX_BASE64 and EVERYAPP_SIGN_PFX_PASSWORD (CI).'
        }

        if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'The signing thumbprint is not a valid SHA-1 thumbprint.' }

        # Binaries we ship: our own assemblies and the CLI. Test projects are never signed.
        $files = @(Get-ChildItem -Path $ArtifactsRoot -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object {
                    ($_.FullName -match ('[\\/]bin[\\/][^\\/]+[\\/]' + $Configuration.ToLowerInvariant() + '_') -or
                    $_.FullName -match '[\\/]publish[\\/]') -and
                    $_.FullName -notmatch '\.Tests[\\/]' -and
                    ($_.Name -like 'EveryApp.*.dll' -or $_.Name -like 'Ferry.*.dll' -or $_.Name -ieq 'ferry.dll' -or $_.Name -ieq 'ferry.exe')
                })
        if ($files.Count -eq 0) { throw "No binaries to sign under $ArtifactsRoot. Build first." }

        foreach ($file in $files) {
            Write-Host "==> sign $($file.Name)" -ForegroundColor Cyan
            & $signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /sha1 $thumbprint $file.FullName
            if ($LASTEXITCODE -ne 0) {
                # One retry: the timestamp server is a network service.
                Start-Sleep -Seconds 5
                & $signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /sha1 $thumbprint $file.FullName
                if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($file.Name) (exit code $LASTEXITCODE)." }
            }
        }

        $bad = @()
        foreach ($file in $files) {
            $sig = Get-AuthenticodeSignature -LiteralPath $file.FullName
            if ($sig.Status -ne 'Valid') { $bad += "$($file.Name): $($sig.Status)" }
        }
        if ($bad) { throw ('Signature check failed (every file must be Valid): ' + ($bad -join '; ')) }
        Write-Host "Signed and verified $($files.Count) file(s)." -ForegroundColor Green
    }
    finally {
        if ($importedThumbprint) {
            Remove-Item -Path "Cert:\CurrentUser\My\$importedThumbprint" -DeleteKey -Force -ErrorAction SilentlyContinue
        }
        if ($rootThumbprint) {
            Remove-Item -Path "Cert:\LocalMachine\Root\$rootThumbprint" -Force -ErrorAction SilentlyContinue
        }
        if ($cerFile) {
            Remove-Item -LiteralPath $cerFile -Force -ErrorAction SilentlyContinue
        }
        if ($pfxFile) {
            Remove-Item -LiteralPath $pfxFile -Force -ErrorAction SilentlyContinue
        }
    }
}

Push-Location $root
try {
    if (-not $SignOnly) {
        if ($env:CI) {
            $missing = Get-ChildItem -Path $root -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\/](obj|bin|artifacts)[\/]' } |
                Where-Object { -not (Test-Path (Join-Path $_.DirectoryName 'packages.lock.json')) }
            if ($missing) {
                throw ('Missing packages.lock.json in CI for: ' + (($missing | ForEach-Object { $_.Name }) -join ', ') + '. Generate them locally with ./build.ps1 and commit them.')
            }
        }

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
    }

    if ($Sign -or $SignOnly) {
        Invoke-Signing -ArtifactsRoot (Join-Path $root 'artifacts') -Configuration $Configuration
    }

    Write-Host 'Build OK.' -ForegroundColor Green
}
finally {
    Pop-Location
}
