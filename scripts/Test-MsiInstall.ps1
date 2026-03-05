<#
.SYNOPSIS
    Installs the locally built MSI installer directly for testing.

.DESCRIPTION
    Installs the MSI produced by Build-LocalMsi.ps1 or Build-ChocolateyPackage.ps1
    using msiexec, bypassing Chocolatey's download mechanism. Use this to test
    the binary built from the current branch rather than the published GitHub release.

.EXAMPLE
    .\scripts\Test-MsiInstall.ps1
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$csprojPath = Join-Path $repoRoot 'qbPortWeaver.csproj'

$match = Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>'
if (-not $match) {
    Write-Error "Could not find <Version> in qbPortWeaver.csproj."
    exit 1
}
$version = $match.Matches[0].Groups[1].Value
$msi = Join-Path $repoRoot "installer\qbPortWeaver_${version}_Setup.msi"

if (-not (Test-Path $msi)) {
    Write-Error "MSI not found: $msi`nRun Build-LocalMsi.ps1 or Build-ChocolateyPackage.ps1 first."
    exit 1
}

Write-Host "Installing: $msi" -ForegroundColor Cyan
# msiexec is a GUI-subsystem application — use Start-Process to reliably capture the exit code
$p = Start-Process msiexec -ArgumentList "/i `"$msi`" /qn /norestart" -Wait -PassThru
if ($p.ExitCode -notin @(0, 3010, 1641)) {
    Write-Error "msiexec failed with exit code $($p.ExitCode)"
    exit $p.ExitCode
}
Write-Host "Done." -ForegroundColor Green
