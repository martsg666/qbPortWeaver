using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>
/// Detects PIA (Private Internet Access) connectivity and reads the forwarded port via <c>piactl</c>.
/// Each sync cycle spawns two piactl subprocesses - one for IsVpnConnected, one for GetVpnPortAsync -
/// and a third (get vpnip, for GetForwardAddressAsync) on the cycles that run the exit-address check.
/// This is accepted design: piactl is the authoritative source and has no library API.
/// A stalled PIA client causes up to two sequential 5s timeouts per cycle, three on those cycles.
/// </summary>
public sealed class PiaVpnManager : IVpnManager
{
    internal static readonly VpnRegistryConfig Config = new(
        RegistrySettingsManager.KeyPiaServiceSearchTerm,
        RegistrySettingsManager.KeyPiaClientProcessName,
        RegistrySettingsManager.KeyPiaAdapterName,
        "PiaVpnManager.GetClientExePath");

    private const int ProcessTimeoutMs = 5000;

    // Cached path for piactl, in PIA's service folder.
    private static readonly ServiceExeCache _piactlPath = new();

    // piactl reports one of four states in place of a port number. Two of them describe a condition no
    // reconnect can change, and those are the ones auto-recovery must not act on:
    //   Inactive    - PIA is disconnected, or port forwarding is switched off in its settings
    //   Unavailable - the connected server region does not offer port forwarding
    // The other two stay ordinary failed cycles, and so still count toward the recovery threshold:
    //   Attempting  - still connecting and assigning; the state a normal cycle waits through
    //   Failed      - PIA tried to assign a port and did not succeed, which reconnecting may yet clear
    // Unparseable output is not classified at all and is likewise treated as an ordinary failure.
    private static readonly HashSet<string> DurableUnavailableStates =
        new(StringComparer.OrdinalIgnoreCase) { "Inactive", "Unavailable" };

    // Set by every GetVpnPortCore call, so it always describes the port read that just happened.
    // volatile: written on a thread-pool thread (Task.Run) and read by the sync loop.
    private volatile bool _portForwardingUnavailable;

    /// <inheritdoc />
    public string ProviderName => RegistrySettingsManager.VpnProviderPia;

    /// <inheritdoc />
    public bool PortForwardingUnavailable => _portForwardingUnavailable;

    // The forward address GetForwardAddressAsync settled on, once per instance (so once per cycle), like
    // ProtonVpnManager: only the exit-address check reads it, and each read costs a piactl run.
    private bool _forwardResolved;
    private System.Net.IPAddress? _forwardAddress;

    /// <inheritdoc />
    // The VPN address PIA reports for the connection (piactl get vpnip). PIA's gateway does not answer
    // NAT-PMP and its WireGuard connections leave no route naming the server, so this is the one address
    // PIA itself gives. In testing it always equalled the STUN exit address, and the forward was reachable
    // on it. Anything but an IPv4 address (PIA prints "Unknown" while it has none) is no forward address.
    public async Task<System.Net.IPAddress?> GetForwardAddressAsync(CancellationToken cancellationToken = default)
    {
        if (_forwardResolved) return _forwardAddress;
        string? output = await Task.Run(() => RunPiactl("get vpnip"), cancellationToken).ConfigureAwait(false);
        System.Net.IPAddress? address =
            System.Net.IPAddress.TryParse(output, out var parsed)
            && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && !parsed.Equals(System.Net.IPAddress.Any)
                ? parsed
                : null;
        LogManager.Instance.LogDebug($"PiaVpnManager.GetForwardAddressAsync: forward address {address?.ToString() ?? "none"} from piactl");
        _forwardAddress = address;
        _forwardResolved = true;
        return address;
    }

    /// <inheritdoc />
    public System.Net.IPAddress? TunnelAddress => ActiveAdapterName is { } adapter ? VpnAdapter.GetIPv4Address(adapter) : null;

    /// <inheritdoc />
    public bool IsVpnConnected()
    {
        try
        {
            string? output = RunPiactl("get connectionstate");
            if (output is null)
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
            return LogManager.LogDebugFalse($"PiaVpnManager.IsVpnConnected: {ex.Message}");
        }
    }

    /// <inheritdoc />
    // cancellationToken only prevents scheduling if cancelled before the task starts; once GetVpnPortCore runs,
    // cancellation cannot interrupt the in-progress piactl subprocess (bounded by ProcessTimeoutMs).
    public Task<int?> GetVpnPortAsync(CancellationToken cancellationToken = default) => Task.Run(GetVpnPortCore, cancellationToken);

    /// <inheritdoc />
    public string? GetRecoveryTarget() => ProviderName;

    /// <inheritdoc />
    public string GetRecoveryAction() => HelperProtocol.ActionRestart;

    /// <inheritdoc />
    public bool IsAdapterMatch(string interfaceName) => Config.MatchesAdapterName(interfaceName);

    /// <inheritdoc />
    // The first up adapter carrying PIA's name, read live: PIA uses "PIA OpenVPN WinTUN Adapter" for OpenVPN
    // and "wgpia0" for WireGuard, so a protocol switch changes it. The configured name is read once, not
    // per adapter.
    public string? ActiveAdapterName
    {
        get
        {
            string name = Config.GetAdapterName();
            return VpnAdapter.GetActiveInterfaces().Find(nic => VpnRegistryConfig.AdapterNamesMatch(name, nic.Name))?.Name;
        }
    }

    private int? GetVpnPortCore()
    {
        try
        {
            string? output = RunPiactl("get portforward");
            if (output is null)
            {
                // Not classifiable: piactl told us nothing, which is a transient read failure rather
                // than a statement about port forwarding. Clearing the flag keeps the durable state
                // from outliving the evidence for it.
                _portForwardingUnavailable = false;
                LogManager.Instance.LogDebug("PiaVpnManager.GetVpnPortCore: piactl returned no output");
                return null;
            }

            if (int.TryParse(output, out int port) && port > 0)
            {
                _portForwardingUnavailable = false;
                LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPortCore: Found port {port}");
                return port;
            }

            _portForwardingUnavailable = DurableUnavailableStates.Contains(output);
            LogManager.Instance.LogDebug(
                $"PiaVpnManager.GetVpnPortCore: No port from piactl (state: {output}, " +
                $"port forwarding unavailable: {_portForwardingUnavailable})");
            return null;
        }
        catch (Exception ex)
        {
            _portForwardingUnavailable = false;
            LogManager.Instance.LogDebug($"PiaVpnManager.GetVpnPortCore: {ex.Message}");
            return null;
        }
    }

    // Runs a piactl command and returns the trimmed stdout output
    private static string? RunPiactl(string arguments)
    {
        try
        {
            string? piactlPath = GetPiactlPath();
            if (piactlPath is null)
            {
                LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Failed to resolve piactl path");
                return null;
            }

            var startInfo = ProcessHelpers.CreateHiddenStartInfo(piactlPath, arguments);
            startInfo.RedirectStandardOutput = true;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Failed to start piactl process");
                return null;
            }

            // Drain stdout concurrently with WaitForExit to prevent a pipe-buffer deadlock.
            // If ReadToEnd() runs after WaitForExit() the process can stall waiting for the
            // reader to consume its output while we wait for it to exit - each side blocking the other.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(ProcessTimeoutMs))
            {
                // Cleanup only - no new process follows, so KillProcess's retry wait is not needed here.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // InvalidOperationException: already exited between timeout and Kill().
                    // Win32Exception: access denied or process protected.
                }
                LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: piactl timed out and was killed");
                return null;
            }

            // Process has exited and closed the pipe; ReadToEndAsync should complete immediately.
            // Guard with a timeout so a stalled drain cannot block the thread-pool thread indefinitely.
            if (!stdoutTask.Wait(ProcessTimeoutMs))
            {
                LogManager.Instance.LogDebug("PiaVpnManager.RunPiactl: Stdout drain timed out after process exit");
                return null;
            }

            // Reviewed and kept, so it does not get re-flagged as a blocking-async smell: this method is
            // synchronous by design, both waits above are bounded, and the Wait returning true means the
            // task is already complete - so this cannot block. GetAwaiter().GetResult() rather than
            // .Result on purpose: .Result would wrap a fault in AggregateException, and the catch below
            // logs ex.Message, which would then read "One or more errors occurred" instead of the cause.
            // Callers that must not block the UI thread dispatch this via Task.Run (see the DIAG-1 fix).
            string output = stdoutTask.GetAwaiter().GetResult().Trim();

            LogManager.Instance.LogDebug($"PiaVpnManager.RunPiactl: '{arguments}' returned: {output}");
            return output;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"PiaVpnManager.RunPiactl: Failed to run '{arguments}': {ex.Message}");
            return null;
        }
    }

    private static string GetPiactlProcessName() => RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyPiactlProcessName);

    private static string? GetPiactlPath() =>
        _piactlPath.Get(GetPiactlProcessName() + ".exe", Config.FindServiceName, "PiaVpnManager.GetPiactlPath");
}
