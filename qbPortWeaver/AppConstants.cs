namespace qbPortWeaver;

/// <summary>Application-wide constants: version, timing, port range, UI limits and shared HTTP timeouts.
/// Behaviour lives in the focused classes beside it (<see cref="AppFiles"/>, <see cref="ServiceLookup"/>,
/// <see cref="ProcessControl"/>, <see cref="ThemeColors"/>, <see cref="UiHelpers"/>, <see cref="TextFormat"/>).</summary>
public static class AppConstants
{
    // Application metadata
    public static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // Timing
    public const int DefaultUpdateIntervalSeconds = 180;
    public const int MinUpdateIntervalSeconds = 10;
    // 24 hours. An upper bound is required, not cosmetic: both delay paths in MainForm compute
    // updateInterval * MillisecondsPerSecond, which overflows int above ~24.8 days and yields a
    // negative delay. Task.Delay then throws, the error path repeats the same arithmetic and throws
    // again, and the main loop breaks permanently - the app sits in the tray syncing nothing.
    // Must match nudUpdateInterval.Maximum in SettingsForm.Designer.cs, which cannot reference this
    // constant: designer-generated code serialises literals only, and every form must stay openable
    // in the VS designer. The duplication is unavoidable, so it is signposted from this side.
    public const int MaxUpdateIntervalSeconds = 86400;
    public const int ManualSyncWaitSeconds = 10;
    public const int MillisecondsPerSecond = 1000;
    public const int AutoUpdateCheckIntervalMs = 12 * 60 * 60 * MillisecondsPerSecond;
    // Debounce window for network-change triggered re-syncs. NetworkAddressChanged fires in
    // bursts when an adapter comes up, so a single reconnect is coalesced into one wake.
    public const int ResyncDebounceMs = 2500;

    // Ports
    // Usable TCP/UDP listening port range. A provider reporting anything outside it is reporting
    // "no port" - 0 in particular is not a port but an instruction to most clients to pick one at
    // random, which would silently undo the forwarding this app maintains.
    public const int MinPortNumber = 1;
    public const int MaxPortNumber = 65535;

    /// <summary>Returns <see langword="true"/> when <paramref name="port"/> is a usable listening port.</summary>
    /// <remarks>Shared by every consumer of a provider-reported port - the sync loop and the
    /// diagnostics report - so the two can never disagree about what counts as usable.
    /// <para><b>A deliberate exception to this file's constants-only rule</b>, and the only one. That
    /// rule exists to stop <i>behaviour with dependencies</i> accumulating here - file I/O, service
    /// lookup, process control, theming - which is what the 2.6.7 split moved out. This is a pure
    /// predicate over the two constants declared directly above it, with no dependencies at all, and
    /// separating the range from its single interpretation is how two consumers come to disagree about
    /// it. Do not move it out to satisfy the letter of the rule.</para></remarks>
    public static bool IsUsablePort(int port) => port is >= MinPortNumber and <= MaxPortNumber;

    // UI
    public const int MaxTooltipLength = 127; // NotifyIcon.Text max in modern Windows / .NET (the historic 63-char limit was pre-Windows 2000)
    // Passed as the timeout to NotifyIcon.ShowBalloonTip. Modern Windows ignores this value -
    // the OS controls the display duration (and routes Win11 toasts through Action Center) - so
    // it is only a nominal hint; changing it does not alter how long a notification stays up.
    public const int BalloonTipDurationMs = 750;

    // HTTP - shared timeout used by all outbound HTTP clients
    public const int HttpTimeoutSeconds = 10;

    // Upper bound for a user-initiated client test (Settings connection test or Status-panel port
    // reachability check). Above HttpTimeoutSeconds to allow for the auth handshake or the external
    // port-check round trip. Shared so both test paths time out consistently.
    public const int ClientTestTimeoutSeconds = 20;

    // Upper bound for a full diagnostics run. It chains several probes (VPN port, client auth, port
    // reachability), each already individually bounded, so the total needs more headroom than a single test.
    public const int DiagnosticsTimeoutSeconds = 60;

    // GitHub - only the owner is a literal; all URLs are derived
    public const string GitHubRepoOwner = "martsg666";
    public static readonly string GitHubRepoUrl = $"https://github.com/{GitHubRepoOwner}/{AppIdentity.AppName}";
}
