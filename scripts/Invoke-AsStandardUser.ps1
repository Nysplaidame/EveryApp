<#
.SYNOPSIS
  CI only: runs one program as a temporary standard (non-administrator) local user and returns its exit code.
.DESCRIPTION
  GitHub's Windows runner account is an administrator, and Ferry refuses to copy with administrator rights.
  build.ps1 calls this script on CI so the Ferry CLI tests run with a real, non-elevated token. Nothing in
  Ferry is changed or bypassed: the program simply runs as a different, ordinary account.

  Order of work:
  1. Refuse to run outside a GitHub-hosted Actions runner (self-hosted ones are not discarded), or when not
     running as administrator.
  2. Create a local user with a random password. The password is masked in the log (::add-mask::) and
     never printed, written to disk or passed on a command line.
  3. Give that user Modify rights on -WritableFolder only (the test output folder).
  4. Check, as that user, that the token is not elevated (no High integrity level), then run the program
     with the user's own profile and environment. Its output is printed line by line as it runs; after
     -TimeoutMinutes it is stopped (clean-up still runs) and the script fails.
  5. Always (finally): remove the folder permission, delete the user and its profile.
  The runner is discarded after the job, so nothing outlives it even if clean-up fails.
  Needs PowerShell 7 (CI's pwsh): it uses .NET APIs that Windows PowerShell 5.1 does not have.
.PARAMETER FilePath
  The program to run.
.PARAMETER ArgumentList
  Its arguments.
.PARAMETER WritableFolder
  The one folder the user may change. It is also the working directory.
.PARAMETER TimeoutMinutes
  The program is stopped (and the script fails) after this long.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [Parameter(Mandatory)][string]$WritableFolder,
    [ValidateRange(1, 15)][int]$TimeoutMinutes = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:CI -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    # Self-hosted runners also set GITHUB_ACTIONS but are not discarded after the job.
    throw 'Refusing to create a local user outside a GitHub-hosted (discarded after the job) runner.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script needs administrator rights to create the temporary user.'
}

$FilePath = (Resolve-Path -LiteralPath $FilePath).Path
$WritableFolder = (Resolve-Path -LiteralPath $WritableFolder).Path

function New-RandomText {
    param([int]$Bytes)
    return [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes))
}

# Runs one program as the temporary user; returns its exit code and output.
function Invoke-AsUser {
    param([string]$Program, [string[]]$Arguments, [string]$UserName, [securestring]$Password, [string]$WorkingDirectory, [int]$TimeoutMs, [switch]$Stream)

    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UserName = $UserName
    $start.Domain = '.'
    $start.Password = $Password
    # The environment is left untouched on purpose: the child then gets the user's own profile environment
    # (TEMP, USERPROFILE, PATH), not the runner account's folders, which it cannot write.
    $start.LoadUserProfile = $true
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true

    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $errorText = $process.StandardError.ReadToEndAsync()
        $output = [Text.StringBuilder]::new()
        $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
        $timedOut = $false
        # Read the output line by line against the deadline, so a hang still shows progress (with -Stream)
        # and the timeout fires while this script, and its clean-up, still run.
        $pending = $process.StandardOutput.ReadLineAsync()
        while ($true) {
            $remaining = ($deadline - [DateTime]::UtcNow).TotalMilliseconds
            if ($remaining -le 0) { $timedOut = $true; break }
            if (-not $pending.Wait([int][Math]::Min(1000, $remaining))) { continue }
            $line = $pending.Result
            if ($null -eq $line) { break }
            [void]$output.AppendLine($line)
            if ($Stream) { Write-Host $line }
            $pending = $process.StandardOutput.ReadLineAsync()
        }

        if ($timedOut) {
            $process.Kill($true)
            [void]$process.WaitForExit(30000)
            if (-not $Stream -and $output.Length -gt 0) { Write-Host $output.ToString() }
            if ($errorText.Wait(5000) -and $errorText.Result) { Write-Host $errorText.Result }
            throw "$([IO.Path]::GetFileName($Program)) did not finish within $($TimeoutMs / 60000) minutes and was stopped."
        }

        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.ToString(); Error = $errorText.Result }
    }
    finally {
        $process.Dispose()
    }
}

$userName = 'everyappci' + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
# Fixed prefix meets the complexity rule; 32 random base64 characters carry the strength.
$plainPassword = 'Aa1!' + (New-RandomText -Bytes 24)
Write-Host "::add-mask::$plainPassword"
$password = [securestring]::new()
foreach ($character in $plainPassword.ToCharArray()) { $password.AppendChar($character) }
$password.MakeReadOnly()

$computer = [adsi]"WinNT://$env:COMPUTERNAME,computer"
$created = $false
$granted = $false
$sid = $null
$exitCode = 1
try {
    Write-Host "Creating temporary standard user '$userName'." -ForegroundColor Cyan
    $user = $computer.Create('User', $userName)
    $user.SetPassword($plainPassword)
    $user.Put('Description', 'Temporary EveryApp CI test user (deleted after the test run)')
    $user.SetInfo()
    $created = $true
    $plainPassword = $null

    $sid = ([Security.Principal.NTAccount]::new($env:COMPUTERNAME, $userName)).Translate([Security.Principal.SecurityIdentifier]).Value
    # Make the Users group membership explicit (name looked up by its well-known SID, so any display language works).
    $usersGroup = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    try { ([adsi]"WinNT://$env:COMPUTERNAME/$usersGroup,group").Add("WinNT://$env:COMPUTERNAME/$userName,user") }
    catch { Write-Host "Users group: $($_.Exception.Message)" }

    & icacls.exe $WritableFolder /grant "*${sid}:(OI)(CI)M" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not give the temporary user access to $WritableFolder (icacls exit code $LASTEXITCODE)." }
    $granted = $true

    # Prove the token really is non-elevated before trusting any test result.
    $whoami = Join-Path $env:SystemRoot 'System32\whoami.exe'
    $check = Invoke-AsUser -Program $whoami -Arguments @('/groups', '/fo', 'csv', '/nh') -UserName $userName -Password $password -WorkingDirectory $WritableFolder -TimeoutMs 120000
    if ($check.ExitCode -ne 0) { throw "whoami failed as the temporary user (exit code $($check.ExitCode)): $($check.Error)" }
    if ($check.Output -match 'S-1-16-12288' -or $check.Output -match 'S-1-16-16384' -or $check.Output -notmatch 'S-1-16-8192') {
        throw "The temporary user's token is not a standard (Medium integrity) token:`n$($check.Output)"
    }
    Write-Host 'Temporary user runs with a standard (non-administrator) token.' -ForegroundColor Green

    Write-Host "==> $([IO.Path]::GetFileName($FilePath)) $($ArgumentList -join ' ') (as $userName)" -ForegroundColor Cyan
    $run = Invoke-AsUser -Program $FilePath -Arguments $ArgumentList -UserName $userName -Password $password -WorkingDirectory $WritableFolder -TimeoutMs ($TimeoutMinutes * 60000) -Stream
    if ($run.Error) { Write-Host $run.Error }
    Write-Host "Exit code: $($run.ExitCode)"
    $exitCode = $run.ExitCode
}
finally {
    $password.Dispose()
    if ($granted) {
        & icacls.exe $WritableFolder /remove:g "*$sid" /Q | Out-Null
    }
    if ($created) {
        try { $computer.Delete('User', $userName) }
        catch { Write-Host "Could not delete the temporary user (the runner is discarded anyway): $($_.Exception.Message)" }
        if ($sid) {
            Get-CimInstance -ClassName Win32_UserProfile -Filter "SID = '$sid'" -ErrorAction SilentlyContinue |
                Remove-CimInstance -ErrorAction SilentlyContinue
        }
    }
}

exit $exitCode
