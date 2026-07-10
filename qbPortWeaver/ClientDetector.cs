using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>
/// Best-effort detection of which supported BitTorrent client is running or installed, used by the
/// Settings dialog's Detect button to pre-fill the client selection. A running process is a stronger
/// signal than an on-disk install, so it wins when both are present; candidates are probed in a fixed
/// order (qBittorrent, Transmission, Deluge) so the result is deterministic.
/// </summary>
internal static class ClientDetector
{
    internal enum DetectionKind { Running, Installed }

    /// <summary>The detected client: which selection to apply, the process name and executable path to
    /// pre-fill (executable null when no default install was found), and how it was detected.</summary>
    internal sealed record DetectedClient(string ClientName, string ProcessName, string? ExePath, DetectionKind Kind);

    /// <summary>
    /// Returns every supported client that is currently running or installed in its default location,
    /// in canonical order (qBittorrent, Transmission, Deluge). Each client appears at most once, marked
    /// <see cref="DetectionKind.Running"/> when a matching process is live, otherwise
    /// <see cref="DetectionKind.Installed"/>. An empty list means none were found. The caller decides
    /// how to resolve multiple matches (e.g. prefer running, or prompt). Never throws.
    /// </summary>
    internal static IReadOnlyList<DetectedClient> DetectAll()
    {
        var results = new List<DetectedClient>();
        foreach (var c in ClientRegistry.All)
        {
            string? defaultExe = ResolveDefaultExe(c);

            if (TryFindRunningProcess(c.ProcessNames, out string? matched, out string? runningExe))
            {
                // A running client knows its own install location, so prefer the live process's real
                // executable path. Fall back to the default install path only when the real path could
                // not be read (access denied / bitness mismatch) but a default install exists.
                string? exe = runningExe ?? defaultExe;
                results.Add(new DetectedClient(c.Name, matched ?? c.ProcessNames[0], exe, DetectionKind.Running));
            }
            else if (defaultExe is not null)
            {
                results.Add(new DetectedClient(c.Name, c.ProcessNames[0], defaultExe, DetectionKind.Installed));
            }
        }
        return results;
    }

    // Returns the candidate's default install path, checking both the 64-bit and 32-bit Program Files
    // roots (a client such as Deluge may ship a 32-bit build under "Program Files (x86)"), or null if
    // the executable is not at either default location.
    private static string? ResolveDefaultExe(ClientRegistry.ClientInfo c)
    {
        foreach (var root in (ReadOnlySpan<Environment.SpecialFolder>)[Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86])
        {
            string baseDir = Environment.GetFolderPath(root);
            if (string.IsNullOrEmpty(baseDir)) continue;
            string path = Path.Combine(baseDir, c.DefaultExeFolder, c.DefaultExeFile);
            if (SafeFileExists(path)) return path;
        }
        return null;
    }

    // Finds the first running process matching any of the candidate names. Reports the matched
    // process name and, best-effort, the live process's executable path (null when it cannot be read).
    private static bool TryFindRunningProcess(string[] processNames, out string? matchedProcessName, out string? exePath)
    {
        foreach (string name in processNames)
        {
            Process[] procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0)
                {
                    matchedProcessName = name;
                    exePath = TryGetProcessExePath(procs[0]);
                    return true;
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        matchedProcessName = null;
        exePath = null;
        return false;
    }

    private static string? TryGetProcessExePath(Process process)
    {
        try
        {
            // MainModule.FileName is the authoritative install path of the live process. It can throw
            // Win32Exception for a process at a higher integrity level or different bitness, or
            // InvalidOperationException if the process exited between enumeration and this read.
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogManager.Instance.LogDebug($"ClientDetector.TryGetProcessExePath: {process.ProcessName} - {ex.Message}");
            return null;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogManager.Instance.LogDebug($"ClientDetector.SafeFileExists: {path} - {ex.Message}");
            return false;
        }
    }
}
