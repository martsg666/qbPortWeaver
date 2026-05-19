using System.Diagnostics;
using System.IO;

namespace qbPortWeaver.Shared
{
    /// <summary>
    /// Process-launch helpers shared by the main app and the helper service so the two processes
    /// build hidden ProcessStartInfo objects identically and resolve system-utility paths the same way.
    /// </summary>
    public static class ProcessHelpers
    {
        /// <summary>
        /// Absolute path to <c>taskkill.exe</c> resolved from <c>%SystemRoot%\System32</c>.
        /// Cached once at type-init time. Used by both processes as the stage-2 kill fallback.
        /// </summary>
        public static readonly string SystemTaskkillPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");

        /// <summary>
        /// Creates a <see cref="ProcessStartInfo"/> for launching a process with no visible window
        /// and no shell intermediation. Used wherever a hidden child process is started
        /// (taskkill fallback, post-update command, piactl invocation).
        /// </summary>
        public static ProcessStartInfo CreateHiddenStartInfo(string fileName, string arguments) =>
            new(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true };
    }
}
