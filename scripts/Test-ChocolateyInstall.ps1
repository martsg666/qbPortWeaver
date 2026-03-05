<#
.SYNOPSIS
    Installs the locally built qbPortWeaver Chocolatey package for testing.

.DESCRIPTION
    Installs from the local choco/ source directory, bypassing the checksum
    verification. This is intentional: the local build produces an MSI with a
    different checksum than the one published to GitHub, so the checksum in the
    .nupkg will never match during local testing.

    Use this script to test the install/uninstall logic after running
    Build-ChocolateyPackage.ps1. Do not use --ignore-checksums in production.

.EXAMPLE
    .\scripts\Test-ChocolateyInstall.ps1
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$chocoSrc = Join-Path $repoRoot 'choco'

choco install qbportweaver --source $chocoSrc -y --ignore-checksums
