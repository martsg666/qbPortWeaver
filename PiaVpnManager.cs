using Microsoft.Win32;
using System.Diagnostics;

namespace qbPortWeaver
{
    // Manages PIA (Private Internet Access) VPN operations
    public sealed class PiaVpnManager : IVpnManager
    {
        private const string PiaUninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string PiaDisplayName           = "Private Internet Access";
        private const string PiactlFilename           = "piactl.exe";
        private const int    ProcessTimeoutMs         = 5000;

        public string ProviderName => RegistrySettingsManager.VpnProviderPia;

        public bool IsVpnConnected()
        {
            try
            {
                string? output = RunPiactl("get connectionstate");
                if (output == null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.IsVpnConnected: piactl returned no output");
                    return false;
                }

                bool isConnected = output.Equals("Connected", StringComparison.OrdinalIgnoreCase);

                LogManager.Instance.LogDebug(isConnected
                    ? "PiaVpnManager.IsVpnConnected: PIA VPN is connected"
                    : $"PiaVpnManager.IsVpnConnected: PIA VPN is not connected (state: {output})");

                return isConnected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.IsVpnConnected: {ex.Message}");
                return false;
            }
        }

        public int? GetVpnPort()
        {
            try
            {
                string? output = RunPiactl("get portforward");
                if (output == null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.GetVpnPort: piactl returned no output");
                    return null;
                }

                if (int.TryParse(output, out int port) && port > 0)
                {
                    LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPort: Found port {port}");
                    return port;
                }

                LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPort: Could not parse port from piactl output: {output}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPort: {ex.Message}");
                return null;
            }
        }

        // Runs a piactl command and returns the trimmed stdout output
        private static string? RunPiactl(string arguments)
        {
            try
            {
                string? piactlPath = GetPiactlPath();
                if (piactlPath == null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Failed to resolve piactl path");
                    return null;
                }

                var startInfo = new ProcessStartInfo(piactlPath, arguments)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Failed to start piactl process");
                    return null;
                }

                // Read asynchronously so WaitForExit timeout still applies even if output is large
                var outputTask = process.StandardOutput.ReadToEndAsync();

                if (!process.WaitForExit(ProcessTimeoutMs))
                {
                    process.Kill();
                    LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: piactl timed out and was killed");
                    return null;
                }

                // Process has exited, stdout is closed — the async read is complete
                string output = outputTask.GetAwaiter().GetResult().Trim();

                LogManager.Instance.LogDebug($"PiaVpnManager.RunPiactl: '{arguments}' returned: {output}");
                return output;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.RunPiactl: Failed to run '{arguments}': {ex.Message}");
                return null;
            }
        }

        // Resolves the piactl.exe path from PIA's install location in the registry
        private static string? GetPiactlPath()
        {
            try
            {
                using var uninstallKey = Registry.LocalMachine.OpenSubKey(PiaUninstallRegistryPath);
                if (uninstallKey == null)
                {
                    LogManager.Instance.LogDebug("PiaVpnManager.GetPiactlPath: Failed to open Uninstall registry key");
                    return null;
                }

                foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey == null)
                        continue;

                    string? displayName = subKey.GetValue("DisplayName") as string;
                    if (displayName == null || !displayName.Equals(PiaDisplayName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? installLocation = subKey.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(installLocation))
                    {
                        LogManager.Instance.LogDebug("PiaVpnManager.GetPiactlPath: PIA found in registry but InstallLocation is empty");
                        return null;
                    }

                    string piactlPath = Path.Combine(installLocation, PiactlFilename);
                    if (!File.Exists(piactlPath))
                    {
                        LogManager.Instance.LogDebug($"PiaVpnManager.GetPiactlPath: piactl not found at: {piactlPath}");
                        return null;
                    }

                    LogManager.Instance.LogDebug($"PiaVpnManager.GetPiactlPath: Found piactl at: {piactlPath}");
                    return piactlPath;
                }

                LogManager.Instance.LogDebug("PiaVpnManager.GetPiactlPath: PIA not found in registry");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PiaVpnManager.GetPiactlPath: {ex.Message}");
                return null;
            }
        }
    }
}
