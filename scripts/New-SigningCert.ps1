<#
.SYNOPSIS
  Creates the self-signed EveryApp code-signing certificate for this PC.
.DESCRIPTION
  1. Creates a 3072-bit RSA code-signing certificate in Cert:\CurrentUser\My (valid 3 years).
  2. Trusts the PUBLIC certificate for this user (CurrentUser\Root and CurrentUser\TrustedPublisher).
  3. Exports a password-protected PFX to a git-ignored path (default: signing\everyapp-signing.pfx).
  4. Prints the "gh secret set" commands for you to run. This script never uploads secrets itself.

  Changing what Windows trusts is a deliberate action: Windows shows its own confirmation for the Root store.
  Use -WhatIf to see every change without making any.
.PARAMETER Rotate
  Replace an existing EveryApp signing certificate (removes the old one from all three stores first).
.PARAMETER PfxPath
  Where to write the PFX. Must stay git-ignored (signing\ and *.pfx are).
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [switch]$Rotate,
    [string]$PfxPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'signing\everyapp-signing.pfx')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$subject = 'CN=EveryApp (self-signed)'
$stores = 'Cert:\CurrentUser\My', 'Cert:\CurrentUser\Root', 'Cert:\CurrentUser\TrustedPublisher'

$existing = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject })
if ($existing.Count -gt 0 -and -not $Rotate) {
    throw "A certificate with subject '$subject' already exists ($($existing[0].Thumbprint)). Re-run with -Rotate to replace it."
}

if ($Rotate) {
    foreach ($store in $stores) {
        foreach ($old in @(Get-ChildItem $store | Where-Object { $_.Subject -eq $subject })) {
            if ($PSCmdlet.ShouldProcess("$store\$($old.Thumbprint)", 'Remove old EveryApp signing certificate')) {
                Remove-Item -Path "$store\$($old.Thumbprint)" -DeleteKey:($store -like '*\My') -ErrorAction Stop
            }
        }
    }
}

$cert = $null
if ($PSCmdlet.ShouldProcess('Cert:\CurrentUser\My', "Create code-signing certificate '$subject' (RSA 3072, 3 years)")) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 `
        -NotAfter (Get-Date).AddYears(3) -CertStoreLocation Cert:\CurrentUser\My
}

$pfxDir = Split-Path -Parent $PfxPath
if ($PSCmdlet.ShouldProcess($PfxPath, 'Export password-protected PFX (git-ignored)')) {
    if (Test-Path -LiteralPath $PfxPath) { throw "Refusing to overwrite existing file: $PfxPath" }
    New-Item -ItemType Directory -Force -Path $pfxDir | Out-Null
    $password = Read-Host -AsSecureString -Prompt 'Choose a password for the PFX'
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $password | Out-Null
}

if ($PSCmdlet.ShouldProcess('CurrentUser\Root and CurrentUser\TrustedPublisher', 'Trust the public certificate (this user only)')) {
    $cer = Join-Path $pfxDir 'everyapp-signing.cer'
    try {
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $cer -Force -ErrorAction SilentlyContinue
    }
}

$thumb = if ($cert) { $cert.Thumbprint } else { '<thumbprint>' }
Write-Host ''
Write-Host 'Done. Next steps (run these yourself; this script never uploads secrets):' -ForegroundColor Green
Write-Host ''
Write-Host "  # Local signing:"
Write-Host "  `$env:EVERYAPP_SIGN_THUMBPRINT = '$thumb'"
Write-Host ''
Write-Host '  # CI signing (GitHub repository secrets):'
Write-Host "  [Convert]::ToBase64String([IO.File]::ReadAllBytes('$PfxPath')) | gh secret set EVERYAPP_SIGN_PFX_BASE64 --repo Nysplaidame/EveryApp"
Write-Host '  gh secret set EVERYAPP_SIGN_PFX_PASSWORD --repo Nysplaidame/EveryApp   # prompts for the PFX password'
Write-Host ''
Write-Host 'Keep the PFX and its password private. Delete the PFX once the secrets are uploaded if you do not need it.'
