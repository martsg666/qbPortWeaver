<#
.SYNOPSIS
    Uninstalls the locally installed qbPortWeaver Chocolatey package.

.DESCRIPTION
    Companion to Test-ChocolateyInstall.ps1. Run this after testing the install
    to cleanly remove the package via Chocolatey.

.EXAMPLE
    .\scripts\Test-ChocolateyUninstall.ps1
#>

$ErrorActionPreference = 'Stop'

choco uninstall qbportweaver -y
