using System.Diagnostics;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>The address and token the Nicotine+ bridge plugin published for this session.</summary>
/// <param name="Url">Base URL of the plugin's local API, with no trailing slash.</param>
/// <param name="Token">Bearer token the plugin expects.</param>
/// <param name="FilePath">The connection file this came from, for diagnostics.</param>
internal sealed record NicotinePluginHandshake(string Url, string Token, string FilePath);

/// <summary>
/// Finds the Nicotine+ bridge plugin's connection file, so the user does not have to copy an
/// address and token by hand.
/// <para>The plugin writes the file on every start; qbPortWeaver reads it when the Settings
/// dialog asks, when a request fails and the endpoint may have moved, and when diagnostics run.
/// Every method is best-effort and returns <see langword="null"/> rather than throwing - a
/// missing file is the normal state when the plugin is not installed.</para>
/// </summary>
internal static class NicotinePluginDiscovery
{
    /// <summary>Folder name the plugin is installed under, inside Nicotine+'s plugins folder.</summary>
    internal const string PluginFolderName = "qbpw_nicotine_bridge";

    /// <summary>Identifier the plugin reports, used to confirm a connection file is really ours.</summary>
    internal const string PluginAppId = "qbpw-nicotine-bridge";

    /// <summary>
    /// Highest connection-file schema this build understands, matching <c>handshake.SCHEMA</c> in the
    /// plugin. Read for the same reason <see cref="qbPortWeaver.Shared.HelperProtocol.Version"/> is read
    /// off every helper response: the two halves upgrade independently, so a file this build cannot
    /// interpret has to be refused rather than misread field by field.
    /// <para>Bump this only when the file's meaning changes, and say what changed here. A plugin
    /// writing a <b>higher</b> schema is newer than this app - its file is rejected, and the Settings
    /// plugin line reports the bridge as not connected rather than silently acting on fields that may
    /// have moved. A file with <b>no</b> schema predates the field entirely; those are accepted,
    /// because the format it describes is exactly schema 1.</para>
    /// </summary>
    internal const int MaxSupportedSchema = 1;

    // State key for LogStateChange: a bind_host pointing off this machine persists until the user
    // changes the plugin setting, and the connection file is re-read on every failed request and on
    // every Settings poll, so this reports on the transition rather than once per read.
    private const string NonLoopbackHostStateKey = "nicotine.nonLoopbackHost";

    /// <summary>Whether <paramref name="host"/> names this machine, so the plain-HTTP handshake stays local.</summary>
    /// <remarks>Deliberately mirrors the plugin's own <c>_is_loopback_host</c> (<c>bridge/server.py</c>),
    /// which strips brackets and lower-cases before comparing. Matching exactly instead meant the two
    /// halves of one contract disagreed: <c>LOCALHOST</c>, and the correctly-bracketed IPv6 form
    /// <c>[::1]</c>, are loopback to the plugin but read as remote here - which now produces a warning
    /// telling the user their token crosses the network when it never leaves the machine.</remarks>
    private static bool IsLoopbackHost(string host)
    {
        string bare = host.Trim('[', ']');
        return bare.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Renders <paramref name="host"/> for use in a URL, bracketing an IPv6 literal.</summary>
    /// <remarks>A host containing a colon can only be an IPv6 address - neither a host name nor an
    /// IPv4 address may contain one - and RFC 3986 requires those to be bracketed in an authority.
    /// Without this, the accepted <c>::1</c> composes <c>http://::1:38472</c>, which does not parse,
    /// so a form the code deliberately allows could never actually be reached.</remarks>
    private static string FormatHostForUrl(string host) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

    /// <summary>File whose presence marks the plugin as installed. Nicotine+ requires it, and it
    /// carries the version, so it is the marker both the installer and this class key on.</summary>
    internal const string PluginMarkerFileName = "PLUGININFO";

    private const string PrimaryFileName = "nicotine-bridge.json";
    private const string SecondaryFileName = "qbportweaver-bridge.json";
    private const string NicotineDataFolderName = "nicotine";
    private const string PortableFolderName = "portable";
    private const string PortableDataFolderName = "data";
    private const string PluginsFolderName = "plugins";

    /// <summary>
    /// Resolves Nicotine+'s data folder, or <see langword="null"/> if it cannot be found.
    /// <para>Checks for a portable layout beside the executable before the roaming default,
    /// matching the order Nicotine+ itself uses. A data folder chosen with Nicotine+'s
    /// <c>-c</c>/<c>--user-data</c> options cannot be discovered from outside the process;
    /// those users enter the address and token manually.</para>
    /// </summary>
    internal static string? ResolveDataFolder(string? exePathHint)
    {
        string? exeFolder = SafeGetDirectory(exePathHint);
        if (exeFolder is not null)
        {
            string portable = SafeCombine(exeFolder, PortableFolderName, PortableDataFolderName);
            if (SafeDirectoryExists(portable)) return portable;
        }

        string roaming = SafeCombine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), NicotineDataFolderName);
        return SafeDirectoryExists(roaming) ? roaming : null;
    }

    /// <summary>Resolves the folder the bridge plugin is installed into, or <see langword="null"/>.</summary>
    internal static string? ResolvePluginFolder(string? exePathHint)
    {
        string? dataFolder = ResolveDataFolder(exePathHint);
        return dataFolder is null ? null : CombinePluginFolder(dataFolder);
    }

    /// <summary>Combines an already-resolved Nicotine+ data folder with the plugin's install location.</summary>
    /// <remarks>Single source for the layout, so the installer and this class can never disagree
    /// about where the plugin lives.</remarks>
    internal static string CombinePluginFolder(string dataFolder) =>
        SafeCombine(dataFolder, PluginsFolderName, PluginFolderName);

    /// <summary>Returns <see langword="true"/> if the bridge plugin is installed.</summary>
    /// <remarks>Keyed on the same marker file <see cref="NicotinePluginInstaller"/> uses to report
    /// installation state, so a half-written folder cannot make the log and the diagnostics report
    /// disagree about whether the plugin exists.</remarks>
    internal static bool IsPluginInstalled(string? exePathHint)
    {
        string? folder = ResolvePluginFolder(exePathHint);
        return folder is not null && SafeFileExists(SafeCombine(folder, PluginMarkerFileName));
    }

    /// <summary>
    /// Reads the connection file the running plugin published, or <see langword="null"/> when the
    /// plugin is not installed, not enabled, or Nicotine+ has not run since it was enabled.
    /// </summary>
    internal static NicotinePluginHandshake? TryRead(string? exePathHint)
    {
        foreach (string path in CandidatePaths(exePathHint))
        {
            var handshake = TryReadFile(path);
            if (handshake is not null) return handshake;
        }
        return null;
    }

    /// <summary>The connection files to look at, most authoritative first.</summary>
    internal static IEnumerable<string> CandidatePaths(string? exePathHint)
    {
        // qbPortWeaver's own data folder is a fixed path regardless of how Nicotine+ was
        // installed, so it is checked before the copy in the Nicotine+ data folder.
        yield return SafeCombine(AppFiles.AppDataFolder, PrimaryFileName);

        string? dataFolder = ResolveDataFolder(exePathHint);
        if (dataFolder is not null) yield return SafeCombine(dataFolder, SecondaryFileName);
    }

    // Whether this file's layout is one this build can interpret. Checked before any other field is
    // read: past that point every read assumes schema 1's meaning, and a newer plugin could have
    // changed what a field holds rather than only adding to it.
    //
    // Three cases, deliberately kept apart. Absent is a plugin from before the field existed, which
    // wrote exactly this layout, so it is accepted. A value that will not read as an integer is
    // rejected rather than treated as absent: whatever wrote it knew about the field, so we cannot
    // claim it predates one, and a gate that exists to refuse what it cannot interpret must not be
    // bypassed by input it cannot parse. Higher than we support is rejected on its own terms. The
    // port check in TryReadFile draws the same line.
    //
    // Split out of TryReadFile rather than inlined: the nested parse-then-compare pushed that method
    // past the cognitive-complexity gate, and this is the one part of it that answers a question of
    // its own rather than extracting a field.
    private static bool HasSupportedSchema(JsonElement root, string path)
    {
        if (!root.TryGetProperty("schema", out var schemaElement)) return true;

        if (!schemaElement.TryGetInt32(out int schema))
        {
            LogManager.Instance.LogDebug(
                $"NicotinePluginDiscovery.HasSupportedSchema: {path} has an unreadable connection-file schema - ignoring it");
            return false;
        }

        if (schema > MaxSupportedSchema)
        {
            LogManager.Instance.LogDebug(
                $"NicotinePluginDiscovery.HasSupportedSchema: {path} uses connection-file schema {schema}, " +
                $"newer than the {MaxSupportedSchema} this build understands - ignoring it. Update qbPortWeaver.");
            return false;
        }

        return true;
    }

    private static NicotinePluginHandshake? TryReadFile(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return null;

            // FileShare.ReadWrite because the plugin may be rewriting the file; the write is a
            // rename over the top, so a reader either sees the old file or the new one.
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetStringOrNull("app") != PluginAppId)
            {
                LogManager.Instance.LogDebug($"NicotinePluginDiscovery.TryReadFile: {path} is not a qbPortWeaver bridge file");
                return null;
            }

            if (!HasSupportedSchema(root, path)) return null;

            if (!root.TryGetProperty("port", out var portElement) ||
                !portElement.TryGetInt32(out int port) ||
                port is < AppConstants.MinPortNumber or > AppConstants.MaxPortNumber)
            {
                LogManager.Instance.LogDebug($"NicotinePluginDiscovery.TryReadFile: {path} has no usable port");
                return null;
            }

            string token = root.GetStringOrNull("token") ?? string.Empty;
            if (token.Length == 0)
            {
                LogManager.Instance.LogDebug($"NicotinePluginDiscovery.TryReadFile: {path} has no token");
                return null;
            }

            // A stale file left by a crash could name a port some unrelated process has since
            // taken. This liveness check is the first guard; the second is the bearer token, which
            // an unrelated listener cannot honour, so it answers with something other than a valid
            // bridge response and the request fails cleanly.
            if (root.TryGetProperty("pid", out var pidElement) && pidElement.TryGetInt32(out int pid) &&
                !IsProcessAlive(pid))
            {
                LogManager.Instance.LogDebug($"NicotinePluginDiscovery.TryReadFile: {path} was left by process {pid}, which is gone");
                return null;
            }

            string host = root.GetStringOrNull("host") ?? "127.0.0.1";

            // Loopback is the plugin's default, and what the S5332 suppression below rests on: plain
            // HTTP is acceptable because the traffic cannot leave this machine. It is a default, not
            // an invariant - the plugin exposes a bind_host setting documented for "qbPortWeaver runs
            // on another machine", so a non-loopback file is a configuration the user was invited to
            // create, not evidence of tampering.
            //
            // It is therefore reported rather than refused. Refusing bought no safety: the same remote
            // address can be typed into Settings by hand, reaching an identical setup, so discarding
            // the file only blocked the convenient path to a configuration the app still permits - and
            // said so at Debug, where nobody would see it. A Warn names the real cost instead.
            if (!IsLoopbackHost(host))
            {
                LogManager.Instance.LogStateChange(NonLoopbackHostStateKey,
                    $"The Nicotine+ bridge is published on '{host}', which is not this machine. The access " +
                    "token and every request to it cross the network unencrypted - use this only on a " +
                    "network you trust, or set the plugin's Address back to 127.0.0.1.",
                    LogLevel.Warn);
            }
            else
            {
                LogManager.Instance.ClearLogState(NonLoopbackHostStateKey);
            }

            return new NicotinePluginHandshake($"http://{FormatHostForUrl(host)}:{port}", token, path); // NOSONAR S5332 - loopback by default (warned above when not); TLS is meaningless for a local-only handshake
        }
        catch (Exception ex)
        {
            // Path.Combine throws on invalid characters, JsonDocument on malformed content, and
            // the file may vanish between the check and the read. None of it is actionable.
            LogManager.Instance.LogDebug($"NicotinePluginDiscovery.TryReadFile: {path}: {ex.Message}");
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? SafeGetDirectory(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static string SafeCombine(params string[] parts)
    {
        try
        {
            return Path.Combine(parts);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return path.Length > 0 && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return path.Length > 0 && File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
