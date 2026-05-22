using System.Diagnostics;

namespace qbPortWeaver.Shared;

/// <summary>
/// Process-launch helpers shared by the main app and the helper service so the two processes
/// build hidden ProcessStartInfo objects identically.
/// </summary>
public static class ProcessHelpers
{
    /// <summary>
    /// Creates a <see cref="ProcessStartInfo"/> for launching a process with no visible window
    /// and no shell intermediation. Used wherever a hidden child process is started
    /// (taskkill fallback, post-update command, piactl invocation).
    /// </summary>
    public static ProcessStartInfo CreateHiddenStartInfo(string fileName, string arguments) =>
        new(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true };
}
