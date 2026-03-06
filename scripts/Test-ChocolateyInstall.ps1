<#
.SYNOPSIS
    Installs the locally built qbPortWeaver Chocolatey package for testing.

.DESCRIPTION
    Repackages the choco/ source files with a local file:// URL pointing to the
    locally built MSI, then installs via Chocolatey with checksum verification
    bypassed. Chocolatey's checksum verification is designed for remote downloads
    and is unreliable with file:// URLs; integrity is not a concern here since the
    MSI was just built locally. This avoids the need for a published GitHub release.

    Run Build-LocalMsi.ps1 or Build-ChocolateyPackage.ps1 first to produce the MSI.

.EXAMPLE
    .\scripts\Test-ChocolateyInstall.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$chocoSrc   = Join-Path $repoRoot 'choco'
$csprojPath = Join-Path $repoRoot 'qbPortWeaver.csproj'
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "qbPortWeaver-choco-test-$([System.Guid]::NewGuid().ToString('N'))"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"   -ForegroundColor Green }

# ---------------------------------------------------------------------------
# Step 1: Resolve version and locate MSI
# ---------------------------------------------------------------------------
Write-Step 'Resolving version and locating MSI...'

$match = Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>'
if (-not $match) { throw "Could not find <Version> in qbPortWeaver.csproj." }
$version = $match.Matches[0].Groups[1].Value

$setupMsi = Join-Path $repoRoot "installer\qbPortWeaver_${version}_Setup.msi"
if (-not (Test-Path $setupMsi)) {
    throw "MSI not found: $setupMsi`nRun Build-LocalMsi.ps1 or Build-ChocolateyPackage.ps1 first."
}

$localUrl = 'file:///' + $setupMsi.Replace('\', '/')
$checksum = (Get-FileHash -Path $setupMsi -Algorithm SHA256).Hash.ToUpper()

Write-Ok "Version : $version"
Write-Ok "MSI     : $setupMsi"
Write-Ok "URL     : $localUrl"

# ---------------------------------------------------------------------------
# Step 2: Stage, stamp placeholders with local URL, pack, and install
# ---------------------------------------------------------------------------
Write-Step 'Staging and stamping Chocolatey package files...'

Copy-Item -Recurse -Path $chocoSrc -Destination $stagingDir
try {
    $nuspecPath  = Join-Path $stagingDir 'qbPortWeaver.nuspec'
    $installPath = Join-Path $stagingDir 'tools\chocolateyInstall.ps1'
    $verifyPath  = Join-Path $stagingDir 'tools\VERIFICATION.txt'

    (Get-Content $nuspecPath  -Encoding utf8) -replace 'TEMPLATE_VERSION',  $version   | Set-Content $nuspecPath  -Encoding utf8
    (Get-Content $installPath -Encoding utf8) -replace 'TEMPLATE_URL',      $localUrl `
                               -replace 'TEMPLATE_CHECKSUM', $checksum                 | Set-Content $installPath -Encoding utf8
    (Get-Content $verifyPath  -Encoding utf8) -replace 'TEMPLATE_VERSION',  $version `
                               -replace 'TEMPLATE_URL',      $localUrl `
                               -replace 'TEMPLATE_CHECKSUM', $checksum                 | Set-Content $verifyPath  -Encoding utf8

    # Verify no TEMPLATE_ placeholders survived the substitution
    $unreplaced = $false
    foreach ($f in @($nuspecPath, $installPath, $verifyPath)) {
        if (Select-String -Path $f -Pattern 'TEMPLATE_' -Quiet) {
            Write-Host "    Unreplaced TEMPLATE_ placeholder in: $f" -ForegroundColor Red
            $unreplaced = $true
        }
    }
    if ($unreplaced) { throw 'Unreplaced TEMPLATE_ placeholders found — stamping failed.' }

    Write-Step 'Installing community validation extension and packing...'
    choco install chocolatey-community-validation.extension -y --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install chocolatey-community-validation.extension.' }

    choco pack $nuspecPath --output-directory $stagingDir
    if ($LASTEXITCODE -ne 0) { throw 'choco pack failed.' }

    Write-Step 'Installing Chocolatey package...'
    # --ignore-checksums: Chocolatey's checksum verification is unreliable with
    # file:// URLs. Integrity is not a concern here — the MSI was built locally.
    choco install qbportweaver --source $stagingDir -y --ignore-checksums
    if ($LASTEXITCODE -ne 0) { throw 'choco install failed.' }

    Write-Host "`nDone." -ForegroundColor Green
} finally {
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
}
