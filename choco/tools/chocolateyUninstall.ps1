Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  softwareName   = 'qbPortWeaver*'
  fileType       = 'msi'
  silentArgs     = ''  # overridden below with ProductCode + /qn /norestart
  validExitCodes = @(0, 3010, 1641)
}

$key = Get-UninstallRegistryKey -SoftwareName $packageArgs['softwareName']
# StrictMode makes .Count on $null throw, and Get-UninstallRegistryKey returns $null when nothing
# matches - so count null-safely here, or the "already uninstalled" branch below can never run.
$matchCount = if ($null -eq $key) { 0 } else { @($key).Count }

if ($matchCount -eq 1) {
  $key | ForEach-Object {
    # PSChildName is the ProductCode GUID. It must go in silentArgs (unquoted),
    # not in 'file' - Chocolatey quotes the 'file' parameter, which msiexec rejects.
    $packageArgs['silentArgs'] = "$($_.PSChildName) /qn /norestart"
    $packageArgs['file']       = ''
    Uninstall-ChocolateyPackage @packageArgs
  }
} elseif ($matchCount -eq 0) {
  Write-Warning "$($packageArgs['packageName']) has already been uninstalled by other means."
} else {
  Write-Warning "$matchCount matching uninstall entries found - skipping to avoid accidental removal."
  Write-Warning "Matches: $(($key | Select-Object -ExpandProperty DisplayName) -join ', ')"
}

# Optionally remove user settings from the registry (disabled by default - uncomment if desired):
# $regPath = 'HKCU:\Software\qbPortWeaver'
# if (Test-Path $regPath) {
#   Remove-Item -Recurse -Force $regPath
#   Write-Host "Removed user settings at $regPath"
# }
