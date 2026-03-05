Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  softwareName   = 'qbPortWeaver*'
  fileType       = 'msi'
  silentArgs     = ''  # overridden below with ProductCode + /qn /norestart
  validExitCodes = @(0, 3010, 1641)
}

[array]$key = Get-UninstallRegistryKey -SoftwareName $packageArgs['softwareName']

if ($key.Count -eq 1) {
  $key | ForEach-Object {
    # PSChildName is the ProductCode GUID. It must go in silentArgs (unquoted),
    # not in 'file' — Chocolatey quotes the 'file' parameter, which msiexec rejects.
    $packageArgs['silentArgs'] = "$($_.PSChildName) /qn /norestart"
    $packageArgs['file']       = ''
    Uninstall-ChocolateyPackage @packageArgs
  }
} elseif ($key.Count -eq 0) {
  Write-Warning "$($packageArgs['packageName']) has already been uninstalled by other means."
} else {
  Write-Warning "$($key.Count) matching uninstall entries found - skipping to avoid accidental removal."
  Write-Warning "Matches: $(($key | Select-Object -ExpandProperty DisplayName) -join ', ')"
}

# Optionally remove user settings from the registry (disabled by default - uncomment if desired):
# $regPath = 'HKCU:\Software\qbPortWeaver'
# if (Test-Path $regPath) {
#   Remove-Item -Recurse -Force $regPath
#   Write-Host "Removed user settings at $regPath"
# }
