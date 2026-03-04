using Microsoft.Win32;
using System.Diagnostics;

namespace qbPortWeaver
{
    // Manages PIA (Private Internet Access) VPN operations
    public sealed class PIAVPNManager : IVPNManager
    {
        private const string PIA_UNINSTALL_REGISTRY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string PIA_DISPLAY_NAME            = "Private Internet Access";
        private const string PIACTL_FILENAME              = "piactl.exe";
        private const int    PROCESS_TIMEOUT_MS           = 5000;

        public string ProviderName => "PIA";

        // Checks if PIA VPN is connected via piactl
        public bool IsVPNConnected()
        {
            try
            {
                string? output = RunPiactl("get connectionstate");
                if (output == null)
                {
                    LogManager.Instance.LogDebug("PIAVPNManager.IsVPNConnected: piactl returned no output");
                    return false;
                }

                bool isConnected = output.Equals("Connected", StringComparison.OrdinalIgnoreCase);

                LogManager.Instance.LogDebug(isConnected
                    ? "PIAVPNManager.IsVPNConnected: PIA VPN is connected"
                    : $"PIAVPNManager.IsVPNConnected: PIA VPN is not connected (state: {output})");

                return isConnected;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PIAVPNManager.IsVPNConnected: {ex.Message}");
                return false;
            }
        }

        // Gets the forwarded port from PIA via piactl
        public int? GetVPNPort()
        {
            try
            {
                string? output = RunPiactl("get portforward");
                if (output == null)
                {
                    LogManager.Instance.LogDebug("PIAVPNManager.GetVPNPort: piactl returned no output");
                    return null;
                }

                if (int.TryParse(output, out int port) && port > 0)
                {
                    LogManager.Instance.LogDebug($"PIAVPNManager.GetVPNPort: Found port {port}");
                    return port;
                }

                LogManager.Instance.LogDebug($"PIAVPNManager.GetVPNPort: Could not parse port from piactl output: {output}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PIAVPNManager.GetVPNPort: {ex.Message}");
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
                    LogManager.Instance.LogDebug("PIAVPNManager.RunPiactl: Could not resolve piactl path");
                    return null;
                }

                var psi = new ProcessStartInfo(piactlPath, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    LogManager.Instance.LogDebug("PIAVPNManager.RunPiactl: Failed to start piactl process");
                    return null;
                }

                // Read asynchronously so WaitForExit timeout still applies even if output is large
                var outputTask = process.StandardOutput.ReadToEndAsync();

                if (!process.WaitForExit(PROCESS_TIMEOUT_MS))
                {
                    process.Kill();
                    LogManager.Instance.LogDebug("PIAVPNManager.RunPiactl: piactl timed out and was killed");
                    return null;
                }

                // Process has exited, stdout is closed — the async read is complete
                string output = outputTask.GetAwaiter().GetResult().Trim();

                LogManager.Instance.LogDebug($"PIAVPNManager.RunPiactl: '{arguments}' returned: {output}");
                return output;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PIAVPNManager.RunPiactl: Failed to run '{arguments}': {ex.Message}");
                return null;
            }
        }

        // Resolves the piactl.exe path from PIA's install location in the registry
        private static string? GetPiactlPath()
        {
            try
            {
                using var uninstallKey = Registry.LocalMachine.OpenSubKey(PIA_UNINSTALL_REGISTRY_PATH);
                if (uninstallKey == null)
                {
                    LogManager.Instance.LogDebug("PIAVPNManager.GetPiactlPath: Could not open Uninstall registry key");
                    return null;
                }

                foreach (string subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey == null)
                        continue;

                    string? displayName = subKey.GetValue("DisplayName") as string;
                    if (displayName == null || !displayName.Equals(PIA_DISPLAY_NAME, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? installLocation = subKey.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(installLocation))
                    {
                        LogManager.Instance.LogDebug("PIAVPNManager.GetPiactlPath: PIA found in registry but InstallLocation is empty");
                        return null;
                    }

                    string piactlPath = Path.Combine(installLocation, PIACTL_FILENAME);
                    if (!File.Exists(piactlPath))
                    {
                        LogManager.Instance.LogDebug($"PIAVPNManager.GetPiactlPath: piactl not found at: {piactlPath}");
                        return null;
                    }

                    LogManager.Instance.LogDebug($"PIAVPNManager.GetPiactlPath: Found piactl at: {piactlPath}");
                    return piactlPath;
                }

                LogManager.Instance.LogDebug("PIAVPNManager.GetPiactlPath: PIA not found in registry");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"PIAVPNManager.GetPiactlPath: {ex.Message}");
                return null;
            }
        }
    }
}
