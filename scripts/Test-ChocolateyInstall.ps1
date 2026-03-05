<#
.SYNOPSIS
    Installs the locally built qbPortWeaver Chocolatey package for testing.

.DESCRIPTION
    Repackages the choco/ source files with a local file:// URL pointing to the
    locally built MSI, then installs via Chocolatey with checksum bypassed.
    This avoids the need for a published GitHub release.

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

# Resolve version from csproj
$match = Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>'
if (-not $match) { throw "Could not find <Version> in qbPortWeaver.csproj." }
$version = $match.Matches[0].Groups[1].Value

# Locate the locally built MSI
$setupMsi = Join-Path $repoRoot "installer\qbPortWeaver_${version}_Setup.msi"
if (-not (Test-Path $setupMsi)) {
    throw "MSI not found: $setupMsi`nRun Build-LocalMsi.ps1 or Build-ChocolateyPackage.ps1 first."
}

$localUrl = 'file:///' + $setupMsi.Replace('\', '/')
$checksum = (Get-FileHash -Path $setupMsi -Algorithm SHA256).Hash.ToUpper()

Write-Host "Version : $version"  -ForegroundColor Cyan
Write-Host "MSI     : $setupMsi" -ForegroundColor Cyan
Write-Host "URL     : $localUrl" -ForegroundColor Cyan

# Stage, stamp placeholders with local URL, pack, and install
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

    choco pack $nuspecPath --output-directory $stagingDir
    if ($LASTEXITCODE -ne 0) { throw 'choco pack failed.' }

    choco install qbportweaver --source $stagingDir -y --ignore-checksums
    if ($LASTEXITCODE -ne 0) { throw 'choco install failed.' }
} finally {
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
}
