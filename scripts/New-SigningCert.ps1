<#
.SYNOPSIS
  Creates the self-signed EveryApp code-signing certificate for this PC.
.DESCRIPTION
  Order of work (nothing is changed until every precondition has passed):
  1. Preflight: Windows only, output path free, no existing cert (unless -Rotate), password chosen twice.
  2. Create a 3072-bit RSA code-signing certificate in Cert:\CurrentUser\My (valid 3 years).
  3. Export a password-protected PFX (AES256) to a git-ignored path (default: signing\everyapp-signing.pfx).
  4. Trust the PUBLIC certificate for this user (CurrentUser\Root and CurrentUser\TrustedPublisher).
  5. Only then, with -Rotate: remove the OLD certificate(s) from all three stores, by thumbprint.
  6. Print the "gh secret set" commands for you to run. This script never uploads secrets itself.
  If any step fails or is declined, the new certificate is rolled back and the old one is left untouched.

  Windows shows its own confirmation when a certificate is added to the Root store.
  Use -WhatIf to see every change without making any.
.PARAMETER Rotate
  Replace an existing EveryApp signing certificate. Update the thumbprint you use locally and re-upload the secrets afterwards.
.PARAMETER PfxPath
  Where to write the PFX. Must stay git-ignored (signing\ and *.pfx are). An existing file is never overwritten.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [switch]$Rotate,
    [string]$PfxPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'signing\everyapp-signing.pfx')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'This script needs Windows (it uses the Windows certificate stores).'
}

$subject = 'CN=EveryApp (self-signed)'
$storeNames = 'My', 'Root', 'TrustedPublisher'

# ---- 1. Preflight: no changes below this line until it all passes ----
$old = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject })
if ($old.Count -gt 0 -and -not $Rotate) {
    throw "A certificate with subject '$subject' already exists ($($old[0].Thumbprint)). Re-run with -Rotate to replace it."
}
if (Test-Path -LiteralPath $PfxPath) {
    throw "Refusing to overwrite existing file: $PfxPath. Move or delete it first."
}

function ConvertTo-PlainText([securestring]$s) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

$password = $null
if (-not $WhatIfPreference) {
    $p1 = Read-Host -AsSecureString -Prompt 'Choose a password for the PFX'
    $p2 = Read-Host -AsSecureString -Prompt 'Repeat the password'
    $t1 = ConvertTo-PlainText $p1
    $t2 = ConvertTo-PlainText $p2
    if ([string]::IsNullOrEmpty($t1)) { throw 'The password must not be empty.' }
    if ($t1 -cne $t2) { throw 'The two passwords do not match. Nothing was changed.' }
    $password = $p1
}

# Returns $true to proceed. -WhatIf lists every step (returns $false without stopping); a real "No" stops everything.
function Confirm-Step([string]$Target, [string]$Action) {
    $go = $PSCmdlet.ShouldProcess($Target, $Action)
    if (-not $go -and -not $WhatIfPreference) { throw "Cancelled at: $Action. Rolling back." }
    return $go
}

$cert = $null
$exported = $false
$trusted = $false
try {
    # ---- 2. Create ----
    if (Confirm-Step 'Cert:\CurrentUser\My' "Create code-signing certificate '$subject' (RSA 3072, 3 years)") {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 `
            -NotAfter (Get-Date).AddYears(3) -CertStoreLocation Cert:\CurrentUser\My
    }

    # ---- 3. Export ----
    $pfxDir = Split-Path -Parent $PfxPath
    if (Confirm-Step $PfxPath 'Export password-protected PFX (AES256, git-ignored)') {
        New-Item -ItemType Directory -Force -Path $pfxDir | Out-Null
        Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $password -CryptoAlgorithmOption AES256_SHA256 | Out-Null
        $exported = $true
    }

    # ---- 4. Trust the public certificate (this user only) ----
    if (Confirm-Step 'CurrentUser\Root and CurrentUser\TrustedPublisher' 'Trust the public certificate (this user only)') {
        $cer = Join-Path $pfxDir ('everyapp-' + [guid]::NewGuid().ToString('N') + '.cer')
        try {
            Export-Certificate -Cert $cert -FilePath $cer | Out-Null
            $trusted = $true
            Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
            Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
        }
        finally {
            Remove-Item -LiteralPath $cer -Force -ErrorAction SilentlyContinue
        }
    }

    # ---- 5. Remove the old certificate(s) last, by thumbprint ----
    foreach ($o in $old) {
        foreach ($name in $storeNames) {
            $path = "Cert:\CurrentUser\$name\$($o.Thumbprint)"
            if (Test-Path -LiteralPath $path) {
                if (Confirm-Step $path 'Remove OLD EveryApp signing certificate (rotation)') {
                    Remove-Item -Path $path -DeleteKey:($name -eq 'My') -Force
                }
            }
        }
    }
}
catch {
    # Roll back only what this run added; the old certificate is untouched at this point.
    if ($cert) {
        foreach ($name in $storeNames) {
            $path = "Cert:\CurrentUser\$name\$($cert.Thumbprint)"
            if (Test-Path -LiteralPath $path) { Remove-Item -Path $path -DeleteKey:($name -eq 'My') -Force -ErrorAction SilentlyContinue }
        }
    }
    if ($exported) { Remove-Item -LiteralPath $PfxPath -Force -ErrorAction SilentlyContinue }
    throw
}

# ---- 6. Next steps ----
$thumb = if ($cert) { $cert.Thumbprint } else { '<thumbprint>' }
Write-Host ''
Write-Host 'Done. Next steps (run these yourself; this script never uploads secrets):' -ForegroundColor Green
Write-Host ''
Write-Host '  # Local signing:'
Write-Host "  `$env:EVERYAPP_SIGN_THUMBPRINT = '$thumb'"
Write-Host ''
Write-Host '  # CI signing: secrets belong to the "release" environment only (run inside the repo folder):'
Write-Host "  [Convert]::ToBase64String([IO.File]::ReadAllBytes('$PfxPath')) | gh secret set EVERYAPP_SIGN_PFX_BASE64 --env release"
Write-Host '  gh secret set EVERYAPP_SIGN_PFX_PASSWORD --env release   # prompts for the PFX password'
Write-Host ''
Write-Host 'Keep the PFX and its password private. Delete the PFX once the secrets are uploaded if you do not need it.'
