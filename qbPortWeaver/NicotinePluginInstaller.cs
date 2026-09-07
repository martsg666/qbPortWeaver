using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace qbPortWeaver;

/// <summary>What the bridge plugin's installation currently looks like.</summary>
internal enum NicotinePluginState
{
    /// <summary>Nicotine+'s data folder could not be found, so nothing can be determined.</summary>
    DataFolderMissing,
    /// <summary>The plugin is not present.</summary>
    NotInstalled,
    /// <summary>The installed plugin's files differ from the ones this build carries. Difference
    /// rather than age: the plugin's version is pinned to the app version, so comparing versions
    /// could only ever notice an update when the app version moved.</summary>
    Outdated,
    /// <summary>The plugin is present but Nicotine+ has not been told to load it.</summary>
    NotEnabled,
    /// <summary>Enabled, but it has not published connection details - Nicotine+ has not run since.</summary>
    NotRunning,
    /// <summary>Installed, enabled, and reachable.</summary>
    Ready
}

/// <summary>The plugin's installation state, plus what to say about it.</summary>
/// <remarks><paramref name="Summary"/> is shown verbatim on the Settings label beside the install
/// button, so it stays plain prose like every other user-facing status value in the app: no
/// "Plugin:" or other Class.Member-style prefix, which is the Debug-log convention. The button and
/// the group box already supply the context. Keep it under about 40 characters - the label is
/// AutoSize and would otherwise overflow the group box.</remarks>
internal sealed record NicotinePluginStatus(
    NicotinePluginState State,
    string Summary,
    string? PluginFolder,
    string? InstalledVersion,
    NicotinePluginHandshake? Handshake);

/// <summary>The outcome of an install or enable attempt.</summary>
internal sealed record NicotinePluginActionResult(bool Success, string Message);

/// <summary>
/// Installs the Nicotine+ bridge plugin on the user's behalf, and reports what state it is in.
/// <para>The plugin ships as embedded resources rather than loose files, so a single self-contained
/// executable carries everything the installer needs - no extra installer components, and the same
/// behaviour from a development build, the MSI, and the Chocolatey package.</para>
/// </summary>
internal static class NicotinePluginInstaller
{
    private const string ResourcePrefix = "qbPortWeaver.Plugins.Nicotine.qbpw_nicotine_bridge.";
    private const string PluginInfoFileName = NicotinePluginDiscovery.PluginMarkerFileName;
    private const string PluginsSection = "plugins";
    private const string EnabledKey = "enabled";
    private const string EnableKey = "enable";
    private const string ConfigFolderName = "config";
    private const string ConfigFileName = "config";
    private const string ConfigBackupFileName = "config.qbpw-backup";
    private const string ConfigFallbackFileName = "config.old";

    /// <summary>Version of the plugin bundled with this build of qbPortWeaver.</summary>
    internal static string BundledVersion => _bundledVersion.Value;

    private static readonly Lazy<string> _bundledVersion = new(() =>
        ReadVersion(ReadResourceText(ResourcePrefix + PluginInfoFileName)) ?? string.Empty);

    // ------------------------------------------------------------------- status

    /// <summary>Works out what state the plugin is in, from files alone - no network calls.</summary>
    internal static NicotinePluginStatus GetStatus(string? exePathHint)
    {
        string? dataFolder = NicotinePluginDiscovery.ResolveDataFolder(exePathHint);
        if (dataFolder is null)
        {
            return new NicotinePluginStatus(NicotinePluginState.DataFolderMissing,
                "Nicotine+'s data folder was not found", null, null, null);
        }

        string pluginFolder = NicotinePluginDiscovery.CombinePluginFolder(dataFolder);
        string? installedVersion = ReadInstalledVersion(pluginFolder);

        if (installedVersion is null)
        {
            return new NicotinePluginStatus(NicotinePluginState.NotInstalled,
                "Not installed", pluginFolder, null, null);
        }

        if (IsInstalledPluginStale(pluginFolder))
        {
            return new NicotinePluginStatus(NicotinePluginState.Outdated,
                BuildOutdatedDetail(installedVersion),
                pluginFolder, installedVersion, null);
        }

        var handshake = NicotinePluginDiscovery.TryRead(exePathHint);
        if (handshake is not null)
        {
            return new NicotinePluginStatus(NicotinePluginState.Ready,
                $"Ready on {handshake.Url}", pluginFolder, installedVersion, handshake);
        }

        // No connection details. Reading Nicotine+'s config tells us whether that is because it
        // was never enabled or because Nicotine+ has not run since - two different fixes.
        bool? enabled = IsEnabledInConfig(dataFolder);
        if (enabled == false)
        {
            return new NicotinePluginStatus(NicotinePluginState.NotEnabled,
                "Installed, not enabled in Nicotine+", pluginFolder, installedVersion, null);
        }

        return new NicotinePluginStatus(NicotinePluginState.NotRunning,
            "Enabled - start Nicotine+ to connect", pluginFolder, installedVersion, null);
    }

    /// <summary>
    /// Whether the supplied connection settings point somewhere other than where the plugin actually
    /// published itself. Meaningful only in <see cref="NicotinePluginState.Ready"/>, the one state
    /// that carries a handshake; every other state returns <see langword="false"/> because there is
    /// nothing to compare against.
    /// </summary>
    /// <remarks>
    /// This is not a fault - <c>NicotineClient</c> rediscovers the connection file and recovers - but
    /// it costs a failed request every cycle until it is corrected, so both surfaces flag it.
    /// It happens on its own: the plugin falls back to a free port when its configured one is busy,
    /// which moves the published address while the stored settings stay where they were.
    /// <para>Lives here so the two callers cannot drift apart. They compare against different
    /// sources by design - <c>DiagnosticsService</c> against the saved registry values, and
    /// <c>SettingsForm</c> against the values currently in the form, so clicking the refresh button
    /// clears the warning immediately rather than only after a save - but the rule for what counts
    /// as a mismatch has to be one rule.</para>
    /// <para>The URL comparison ignores a trailing slash and case (both name the same endpoint);
    /// the token is compared exactly, since it is an opaque credential.</para>
    /// </remarks>
    internal static bool ConnectionSettingsDiffer(NicotinePluginStatus status, string url, string token) =>
        status.Handshake is { } handshake &&
        (!string.Equals(url.TrimEnd('/'), handshake.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(token, handshake.Token, StringComparison.Ordinal));

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Writes the bundled plugin into Nicotine+'s plugin folder, overwriting any earlier copy of
    /// each bundled file.
    /// <para>Safe while Nicotine+ is running: the plugin's own settings live in Nicotine+'s config,
    /// not in this folder, so nothing the user configured is lost.</para>
    /// <para>Files an older layout had that this build no longer ships are left in place rather
    /// than pruned - an orphaned module is inert because nothing imports it, and deleting from a
    /// folder a running Nicotine+ may hold open is the riskier of the two failure modes.</para>
    /// </summary>
    internal static NicotinePluginActionResult InstallFiles(string? exePathHint)
    {
        string? dataFolder = NicotinePluginDiscovery.ResolveDataFolder(exePathHint);
        if (dataFolder is null)
        {
            return new NicotinePluginActionResult(false,
                "Nicotine+'s data folder was not found.\n\n" +
                "Start Nicotine+ once so it creates its data folder, or set the Executable path " +
                "above if this is a portable installation.");
        }

        string pluginFolder = NicotinePluginDiscovery.CombinePluginFolder(dataFolder);

        try
        {
            var resources = GetPluginResourceNames();
            if (resources.Count == 0)
            {
                return new NicotinePluginActionResult(false,
                    "This build of qbPortWeaver does not contain the Nicotine+ plugin.");
            }

            Directory.CreateDirectory(pluginFolder);

            foreach (string resource in resources)
            {
                string relativePath = ResourceNameToRelativePath(resource[ResourcePrefix.Length..]);
                string target = Path.Combine(pluginFolder, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Embedded resource {resource} could not be opened");
                using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
            }

            LogManager.Instance.LogMessage(
                $"Installed the Nicotine+ bridge plugin ({resources.Count} files) to {pluginFolder}", LogLevel.Info);

            return new NicotinePluginActionResult(true, pluginFolder);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to install the Nicotine+ bridge plugin: {ex.Message}", LogLevel.Error);
            return new NicotinePluginActionResult(false, $"Could not write to {pluginFolder}.\n\n{ex.Message}");
        }
    }

    // ------------------------------------------------------------------- enable

    /// <summary>
    /// Adds the plugin to the list Nicotine+ loads at startup.
    /// <para>Only safe while Nicotine+ is stopped: a running Nicotine+ rewrites its whole config
    /// from memory when it exits, which would discard this edit. Callers must confirm the process
    /// is gone; this method checks again immediately before writing.</para>
    /// <para>Edits exactly the two keys it needs and leaves every other byte alone. If the config
    /// does not parse the way it expects, it changes nothing and says so - a malformed plugin list
    /// makes Nicotine+ reset it, which would silently disable everything else the user runs.</para>
    /// </summary>
    internal static NicotinePluginActionResult TryEnable(string? exePathHint, Func<bool> isNicotineRunning)
    {
        if (isNicotineRunning())
        {
            return new NicotinePluginActionResult(false,
                "Nicotine+ is running. Close it first, or enable the plugin from " +
                "Preferences → Plugins inside Nicotine+.");
        }

        string? dataFolder = NicotinePluginDiscovery.ResolveDataFolder(exePathHint);
        if (dataFolder is null)
            return new NicotinePluginActionResult(false, "Nicotine+'s data folder was not found.");

        string configPath = Path.Combine(dataFolder, ConfigFolderName, ConfigFileName);
        string fallbackPath = Path.Combine(dataFolder, ConfigFolderName, ConfigFallbackFileName);

        try
        {
            string sourcePath = configPath;
            if (!File.Exists(sourcePath))
            {
                // Nicotine+ replaces its config by moving the old one aside and writing a fresh
                // one, so a missing config with a .old beside it means we caught it mid-write.
                if (!File.Exists(fallbackPath))
                {
                    return new NicotinePluginActionResult(false,
                        "Nicotine+'s configuration file was not found. Start Nicotine+ once, then try again.");
                }
                sourcePath = fallbackPath;
            }

            string[] lines = AppFiles.ReadAllLinesShared(sourcePath);

            if (!TryRewriteEnabledPlugins(lines, out string[] updated, out string reason))
            {
                LogManager.Instance.LogMessage(
                    $"Not enabling the Nicotine+ bridge plugin automatically: {reason}", LogLevel.Warn);
                return new NicotinePluginActionResult(false,
                    $"Nicotine+'s configuration could not be updated safely ({reason}).\n\n" +
                    "Nothing was changed. Enable the plugin from Preferences → Plugins inside Nicotine+ instead.");
            }

            if (isNicotineRunning())
            {
                return new NicotinePluginActionResult(false,
                    "Nicotine+ started while this was in progress, so nothing was changed. " +
                    "Close it and try again.");
            }

            string backupPath = Path.Combine(dataFolder, ConfigFolderName, ConfigBackupFileName);
            File.Copy(sourcePath, backupPath, overwrite: true);

            // The only atomic write outside our own data folder, so the startup sweep cannot reach
            // it. Clear leftovers here instead, while Nicotine+ is known to be closed.
            AppFiles.SweepOrphanedTempFiles(Path.GetDirectoryName(configPath) ?? string.Empty);
            AppFiles.WriteAtomic(configPath, updated);

            LogManager.Instance.LogMessage(
                $"Enabled the Nicotine+ bridge plugin in {configPath} (previous copy saved as {ConfigBackupFileName})",
                LogLevel.Info);

            return new NicotinePluginActionResult(true, configPath);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to enable the Nicotine+ bridge plugin: {ex.Message}", LogLevel.Error);
            return new NicotinePluginActionResult(false,
                $"Could not update Nicotine+'s configuration.\n\n{ex.Message}\n\n" +
                "Enable the plugin from Preferences → Plugins inside Nicotine+ instead.");
        }
    }

    /// <summary>
    /// Whether Nicotine+ is configured to load the plugin. <see langword="null"/> when the config
    /// cannot be read or understood, which is not the same as a definite no.
    /// </summary>
    private static bool? IsEnabledInConfig(string dataFolder)
    {
        try
        {
            string configPath = Path.Combine(dataFolder, ConfigFolderName, ConfigFileName);
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(dataFolder, ConfigFolderName, ConfigFallbackFileName);
                if (!File.Exists(configPath)) return null;
            }

            foreach (var (section, key, value) in ReadSettings(AppFiles.ReadAllLinesShared(configPath)))
            {
                if (section != PluginsSection || key != EnabledKey) continue;
                if (!TryParsePythonStringList(value, out var names)) return null;
                return names.Contains(NicotinePluginDiscovery.PluginFolderName, StringComparer.Ordinal);
            }

            return false;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"NicotinePluginInstaller.IsEnabledInConfig: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------- config edit

    // Rewrites only the [plugins] "enabled" list and, if it is off, the "enable" master switch.
    // Returns false without touching anything if the file is not shaped the way Nicotine+ writes
    // it - guessing here would cost the user every plugin they have.
    private static bool TryRewriteEnabledPlugins(string[] lines, out string[] updated, out string reason)
    {
        updated = [];
        reason = string.Empty;

        var (sawPluginsSection, enabledLine, enableLine, sectionEndLine) = ScanPluginsSection(lines);
        if (!sawPluginsSection)
        {
            reason = "it has no [plugins] section";
            return false;
        }

        var result = new List<string>(lines);

        if (enabledLine >= 0)
        {
            string value = lines[enabledLine][(lines[enabledLine].IndexOf('=') + 1)..].Trim();
            if (!TryParsePythonStringList(value, out var names))
            {
                reason = "its plugin list is not in the expected format";
                return false;
            }

            if (names.Contains(NicotinePluginDiscovery.PluginFolderName, StringComparer.Ordinal))
            {
                // Already listed; the master switch may still need turning on, and EnsureMasterSwitch
                // emits the file either way so the caller's flow is uniform.
                return EnsureMasterSwitch(result, enableLine, sectionEndLine, ref updated, ref reason);
            }

            names.Add(NicotinePluginDiscovery.PluginFolderName);
            result[enabledLine] = $"{EnabledKey} = {RenderPythonStringList(names)}";
        }
        else
        {
            // The section exists but has no list yet - insert one at its end.
            result.Insert(sectionEndLine,
                $"{EnabledKey} = {RenderPythonStringList([NicotinePluginDiscovery.PluginFolderName])}");
            if (enableLine >= sectionEndLine) enableLine++;
            sectionEndLine++;
        }

        return EnsureMasterSwitch(result, enableLine, sectionEndLine, ref updated, ref reason);
    }

    // Scans the config for the [plugins] section: returns whether it exists, the line indices of its
    // "enabled" and "enable" keys (-1 if absent), and the line where the section ends (exclusive).
    private static (bool Found, int EnabledLine, int EnableLine, int SectionEndLine) ScanPluginsSection(string[] lines)
    {
        int enabledLine = -1;
        int enableLine = -1;
        int sectionEndLine = -1;
        bool inPluginsSection = false;
        bool sawPluginsSection = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inPluginsSection) sectionEndLine = i;
                inPluginsSection = trimmed[1..^1].Trim().Equals(PluginsSection, StringComparison.OrdinalIgnoreCase);
                sawPluginsSection |= inPluginsSection;
                continue;
            }

            if (inPluginsSection)
                RecordKeyLine(trimmed, i, ref enabledLine, ref enableLine);
        }

        if (inPluginsSection) sectionEndLine = lines.Length;
        if (sectionEndLine < 0) sectionEndLine = lines.Length;

        return (sawPluginsSection, enabledLine, enableLine, sectionEndLine);
    }

    // If this in-section line assigns the "enabled" or "enable" key, records its line index.
    private static void RecordKeyLine(string trimmed, int lineIndex, ref int enabledLine, ref int enableLine)
    {
        // Nicotine+ parses with '=' as the only separator, so anything else is not a setting.
        int separator = trimmed.IndexOf('=');
        if (separator < 0) return;

        string key = trimmed[..separator].Trim();
        if (key.Equals(EnabledKey, StringComparison.OrdinalIgnoreCase)) enabledLine = lineIndex;
        else if (key.Equals(EnableKey, StringComparison.OrdinalIgnoreCase)) enableLine = lineIndex;
    }

    // Plugins do not load at all when the master switch is off, so turning one on means turning
    // the other on too.
    private static bool EnsureMasterSwitch(List<string> result, int enableLine, int sectionEndLine,
        ref string[] updated, ref string reason)
    {
        if (enableLine >= 0)
        {
            string value = result[enableLine][(result[enableLine].IndexOf('=') + 1)..].Trim();
            if (!value.Equals("True", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                reason = "its plugin master switch is not in the expected format";
                return false;
            }
            if (value.Equals("False", StringComparison.OrdinalIgnoreCase))
                result[enableLine] = $"{EnableKey} = True";
        }
        else
        {
            result.Insert(Math.Min(sectionEndLine, result.Count), $"{EnableKey} = True");
        }

        updated = [.. result];
        return true;
    }

    // ------------------------------------------------------------ python values

    // Nicotine+ stores settings as Python literals, so the plugin list is round-tripped in that
    // form. Deliberately strict: anything unexpected fails rather than being reinterpreted.
    internal static bool TryParsePythonStringList(string value, out List<string> items)
    {
        items = [];
        string text = value.Trim();

        if (text.Length < 2 || text[0] != '[' || text[^1] != ']') return false;

        string inner = text[1..^1].Trim();
        if (inner.Length == 0) return true;

        int index = 0;
        while (index < inner.Length)
        {
            SkipWhitespace(inner, ref index);
            if (index >= inner.Length) return false;

            if (!TryReadQuotedElement(inner, ref index, out string element)) return false;
            items.Add(element);

            if (!TryConsumeSeparator(inner, ref index, out bool done)) return false;
            if (done) break;
        }

        return true;
    }

    // Advances index past any run of whitespace.
    private static void SkipWhitespace(string s, ref int index)
    {
        while (index < s.Length && char.IsWhiteSpace(s[index])) index++;
    }

    // After an element: skips whitespace, then requires end-of-input or a comma. Sets done when the
    // list has ended (end reached, or a trailing comma with nothing after). Returns false on a
    // malformed separator.
    private static bool TryConsumeSeparator(string inner, ref int index, out bool done)
    {
        done = false;
        SkipWhitespace(inner, ref index);
        if (index >= inner.Length) { done = true; return true; }
        if (inner[index] != ',') return false;
        index++;
        SkipWhitespace(inner, ref index);
        if (index >= inner.Length) done = true;
        return true;
    }

    // Reads one single- or double-quoted element beginning at index (which must sit on the opening
    // quote), honouring backslash escapes, and advances index past the closing quote. Returns false
    // if the character is not a quote or the element is unterminated.
    private static bool TryReadQuotedElement(string inner, ref int index, out string element)
    {
        element = string.Empty;
        char quote = inner[index];
        if (quote is not ('\'' or '"')) return false;

        index++;
        var builder = new StringBuilder();
        while (index < inner.Length)
        {
            char current = inner[index];
            if (current == '\\' && index + 1 < inner.Length)
            {
                // Takes the escaped character literally rather than decoding the sequence, so Python's
                // '\n' would read as "n". Deliberate, and lossless for this list: the entries are
                // plugin folder names, and a Windows folder name cannot contain a newline, a tab, or a
                // backslash. Every value that can actually appear - including an apostrophe - round-trips
                // exactly through RenderPythonStringList. A real decoder would add cases that could
                // decode wrongly, in a method whose safety property is that anything it cannot read
                // cleanly makes the caller change nothing at all.
                builder.Append(inner[index + 1]);
                index += 2;
                continue;
            }
            if (current == quote) { index++; element = builder.ToString(); return true; }
            builder.Append(current);
            index++;
        }

        return false; // unterminated
    }

    internal static string RenderPythonStringList(IReadOnlyList<string> items)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append('\'')
                   .Append(items[i].Replace("\\", "\\\\", StringComparison.Ordinal)
                                   .Replace("'", "\\'", StringComparison.Ordinal))
                   .Append('\'');
        }
        return builder.Append(']').ToString();
    }

    // Yields (section, key, value) for every setting line, lower-cased section and key.
    private static IEnumerable<(string Section, string Key, string Value)> ReadSettings(string[] lines)
    {
        string section = string.Empty;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator < 0) continue;

            yield return (section, trimmed[..separator].Trim().ToLowerInvariant(), trimmed[(separator + 1)..].Trim());
        }
    }

    // --------------------------------------------------------------- resources

    private static List<string> GetPluginResourceNames() =>
        [.. Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))];

    // Resource names flatten the folder separators, so the last two dots are the file extension
    // and everything before the final remaining dot is a subfolder.
    private static string ResourceNameToRelativePath(string suffix)
    {
        int lastDot = suffix.LastIndexOf('.');
        if (lastDot <= 0) return suffix;

        string stem = suffix[..lastDot];
        string extension = suffix[lastDot..];
        return Path.Combine(stem.Split('.')) + extension;
    }

    // ------------------------------------------------------- staleness by content

    // Whether the installed plugin differs from the one this build carries.
    //
    // Content, not version, and that distinction is the whole point. PLUGININFO's version is pinned
    // to the app version, so a version comparison only ever offers an update when the *app* version
    // moves: a plugin-only fix shipped on an unchanged app version would never be offered, and an
    // upgrade between two builds carrying the same plugin version could never be told apart from one
    // carrying a real change. Comparing the files answers the question actually being asked.
    //
    // Extra files in the plugin folder are ignored on purpose - Python writes __pycache__ there, and
    // treating anything unexpected as a difference would report the plugin outdated forever.
    private static bool IsInstalledPluginStale(string pluginFolder)
    {
        try
        {
            List<string> relativePaths = [.. PluginFiles.Select(entry => entry.RelativePath)];
            if (relativePaths.Count == 0) return false; // nothing bundled: no grounds to call it stale

            // Cheap gate first. GetStatus is polled every two seconds while the Settings dialog is
            // open (SettingsForm.NicotinePluginStatusPollMs), so reading every plugin file on each
            // poll would be wasteful. Lengths and write times cost metadata only, and both the
            // installer (FileMode.Create) and any hand edit move the write time, so a change cannot
            // slip past this and leave the cached verdict standing.
            string signature = BuildInstalledSignature(pluginFolder, relativePaths);

            // Read the cache through a local: the field can be replaced by another thread between
            // the test and the return, and a local pins the instance being examined.
            StaleCache? cached = _staleCache;
            if (cached is not null && cached.Folder == pluginFolder && cached.Signature == signature)
                return cached.Stale;

            string? installed = ComputeInstalledFingerprint(pluginFolder, relativePaths);
            bool stale = installed is null || !string.Equals(installed, BundledFingerprint, StringComparison.Ordinal);

            _staleCache = new StaleCache(pluginFolder, signature, stale);
            return stale;
        }
        catch (Exception ex)
        {
            // Treated as not stale rather than stale: an unreadable file is far more likely to be a
            // transient lock than a real difference, and reporting Outdated on it would nag on every
            // poll with a reinstall that fixes nothing.
            LogManager.Instance.LogDebug($"NicotinePluginInstaller.IsInstalledPluginStale: {ex.Message}");
            return false;
        }
    }

    // Cache for the gate above: the folder a verdict was computed for, the metadata signature it was
    // computed from, and the verdict itself.
    //
    // One immutable object rather than three fields, because this is NOT single-threaded. Settings
    // polls GetStatus on the UI thread every two seconds, while DiagnosticsService reaches the same
    // method from AddNicotinePluginResult, which runs on a thread-pool continuation (RunAsync awaits
    // with ConfigureAwait(false) before it). Three separate writes could interleave and leave one
    // call's signature standing beside another call's verdict - reporting the plugin up to date after
    // it changed, or Outdated after an update. Publishing a single reference makes the three values
    // inseparable: a reader sees either the whole previous verdict or the whole new one.
    //
    // Volatile for the publication order, so a reader cannot observe the reference before the fields
    // it points at. Two threads racing may still both compute, which costs a duplicated hash and
    // nothing else - the verdict is a pure function of the files.
    private sealed record StaleCache(string Folder, string Signature, bool Stale);
    private static volatile StaleCache? _staleCache;

    /// <summary>SHA-256 over every bundled plugin file. Computed once: the resources cannot change
    /// while the process runs.</summary>
    /// <remarks>Note for this and the other Lazy fields here: Lazy caches a thrown exception
    /// permanently, so a failure in the factory would leave the plugin reported "not stale" for the
    /// life of the process, logged at Debug only. Accepted rather than worked around - the factories
    /// touch nothing but already-loaded assembly resources, and the wrong answer in that direction
    /// shows no nag, where reporting Outdated on a transient error would nag on every two-second
    /// poll with a reinstall that fixes nothing.</remarks>
    private static string BundledFingerprint => _bundledFingerprint.Value;

    private static readonly Lazy<string> _bundledFingerprint = new(ComputeBundledFingerprint);

    // Each bundled resource paired with the relative path it installs to, sorted by that path so
    // neither the manifest enumeration order nor the file system's can affect a fingerprint. The
    // resource name is carried rather than reconstructed: mapping a path back to a resource name
    // means re-flattening separators into dots, which is a second encoding to keep in step with
    // ResourceNameToRelativePath for no gain.
    //
    // Resolved once, for the same reason _bundledFingerprint is: the resources cannot change while
    // the process runs, and IsInstalledPluginStale sits on the two-second Settings poll. Rebuilding
    // it per check meant enumerating every manifest resource in the assembly - not just this
    // plugin's - and re-sorting, in front of the metadata gate that exists to keep the check cheap.
    private static List<(string RelativePath, string ResourceName)> PluginFiles => _pluginFiles.Value;

    private static readonly Lazy<List<(string RelativePath, string ResourceName)>> _pluginFiles =
        new(() => [.. GetPluginResourceNames()
            .Select(name => (RelativePath: ResourceNameToRelativePath(name[ResourcePrefix.Length..]), ResourceName: name))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)]);

    private static string ComputeBundledFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string relativePath, string resourceName) in PluginFiles)
        {
            using Stream? source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            // Length before the bytes; an embedded resource stream reports it without reading, and
            // reading it does not disturb the position AppendStream consumes from.
            AppendPath(hash, relativePath, source?.Length ?? 0);
            if (source is not null) AppendStream(hash, source);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Null when a bundled file is missing from the installation, which is a difference by itself and
    // needs no hash to establish.
    private static string? ComputeInstalledFingerprint(string pluginFolder, List<string> relativePaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string relativePath in relativePaths)
        {
            string target = Path.Combine(pluginFolder, relativePath);
            if (!File.Exists(target)) return null;

            // FileShare.ReadWrite so a running Nicotine+ holding a file open cannot fail the read.
            using var source = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            AppendPath(hash, relativePath, source.Length);
            AppendStream(hash, source);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Path, then the content length, so that neither a rename nor a shift of bytes across a file
    // boundary can collide with another layout. The length is load-bearing: the digest is a flat
    // stream of path/content pairs, and without it the only thing separating one file's bytes from
    // the next file's path is a NUL that content could itself contain - so a single file holding
    // "Y<NUL>b<NUL>Z" would hash identically to a file "Y" plus a file "b" holding "Z".
    private static void AppendPath(IncrementalHash hash, string relativePath, long contentLength)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
        hash.AppendData([0]);
        hash.AppendData(BitConverter.GetBytes(contentLength));
    }

    private static void AppendStream(IncrementalHash hash, Stream source)
    {
        byte[] buffer = new byte[8192];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    // Metadata-only signature: length and write time per file, or a marker for one that is absent.
    private static string BuildInstalledSignature(string pluginFolder, List<string> relativePaths)
    {
        var builder = new StringBuilder();
        foreach (string relativePath in relativePaths)
        {
            var info = new FileInfo(Path.Combine(pluginFolder, relativePath));
            builder.Append(relativePath).Append('|');
            if (info.Exists) builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            else builder.Append("missing");
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // Null means the plugin is not installed; an empty string means it is installed but its version
    // could not be established. GetStatus maps null to "Not installed", so the two must not be
    // conflated: NicotinePluginDiscovery.IsPluginInstalled answers the same question from this same
    // marker file's existence alone, and its remarks promise the two cannot disagree about whether
    // the plugin exists. A read that fails on a file that is plainly there would break that promise.
    private static string? ReadInstalledVersion(string pluginFolder)
    {
        string infoPath;
        try
        {
            infoPath = Path.Combine(pluginFolder, PluginInfoFileName);
        }
        catch (ArgumentException ex)
        {
            // A path that cannot even be composed cannot name an installed plugin, so absent is the
            // correct answer here rather than the unknown-version one below.
            LogManager.Instance.LogDebug($"NicotinePluginInstaller.ReadInstalledVersion: {ex.Message}");
            return null;
        }

        // Outside the try on purpose: File.Exists reports failure by returning false rather than
        // throwing, so it cannot land in the catch and be mistaken for a failed read.
        if (!File.Exists(infoPath)) return null;

        try
        {
            return ReadVersion(AppFiles.ReadAllTextShared(infoPath)) ?? string.Empty;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"NicotinePluginInstaller.ReadInstalledVersion: {ex.Message}");
            // The marker is there and only the read failed - installed, version unknown. Re-checked
            // rather than assumed, because the file can be deleted between the check above and the
            // open, and that case really is absent. Matches how IsInstalledPluginStale treats an
            // unreadable file: far more likely a transient lock than a real change.
            return File.Exists(infoPath) ? string.Empty : null;
        }
    }

    private static string? ReadVersion(string? pluginInfo)
    {
        if (string.IsNullOrEmpty(pluginInfo)) return null;

        foreach (string line in pluginInfo.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Version", StringComparison.OrdinalIgnoreCase)) continue;

            int separator = trimmed.IndexOf('=');
            if (separator < 0) continue;

            return trimmed[(separator + 1)..].Trim().Trim('"', '\'');
        }

        return null;
    }

    // The staleness verdict comes from the file contents, so the version is only ever wording here.
    // Both halves are reachable: the versions differ on any release that moved the app version, and
    // they match when a plugin-only change ships on an unchanged one - the case a version gate missed
    // entirely, and the reason this reads "updated files" rather than naming a number that has not moved.
    //
    // This replaced a version gate that refused to report Outdated for an unparseable installed
    // version, so as not to offer an overwrite over a prerelease or a future version format. Content
    // comparison drops that guard deliberately: any difference is a difference, and Outdated only
    // surfaces a button the user chooses to press - it never overwrites anything on its own.
    private static string BuildOutdatedDetail(string installedVersion)
    {
        // The unknown-version branch omits "installed" deliberately. "Unknown version" is ten
        // characters longer than a version number, and with it the string reached 42 characters -
        // past the ~40 the Settings label needs (see NicotinePluginStatus.Summary, which is AutoSize
        // with no MaximumSize). Dropping the word costs nothing and keeps every branch inside budget.
        if (string.IsNullOrWhiteSpace(installedVersion))
            return $"Unknown version, {BundledVersion} available";

        if (string.Equals(installedVersion, BundledVersion, StringComparison.Ordinal))
            return $"{installedVersion} installed, updated files available";

        // "X available" reads as an upgrade, which is wrong when the installed plugin is the higher
        // number - after rolling the app back, or hand-installing a plugin from the repo. The remedy
        // is unchanged and still correct, so only the wording needs to differ: the plugin is pinned to
        // the app version and the two are a matched pair, which is why the connection-file schema gate
        // refuses a plugin newer than this build. Replacing it with the bundled copy is alignment, not
        // a downgrade to be avoided. Fits the ~40-character label budget noted above at 39.
        if (Version.TryParse(installedVersion, out var installed) &&
            Version.TryParse(BundledVersion, out var bundled) &&
            installed > bundled)
        {
            return $"{installedVersion} installed, this build ships {BundledVersion}";
        }

        return $"{installedVersion} installed, {BundledVersion} available";
    }

    private static string? ReadResourceText(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"NicotinePluginInstaller.ReadResourceText: {resourceName}: {ex.Message}");
            return null;
        }
    }
}
