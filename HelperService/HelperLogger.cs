using System.Text;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Writes log entries to the shared qbPortWeaver log file in the same format as the main app's
/// LogManager: "yyyy-MM-dd HH:mm:ss | LEVEL | Subsystem | message".
/// Instantiated per connection with the log file path and the caller's debug-logging preference,
/// both read from the caller's own hive during pipe impersonation.
/// Retries briefly on sharing violations in case another process holds the file.
/// <para><b>Every file-system call runs as the caller, never as SYSTEM.</b> The path comes from the
/// caller's own hive and lands in a folder the caller owns, so the caller controls where it points -
/// by editing the value, or by replacing the folder with a junction. Written as SYSTEM, that would let
/// any local user create or append to files anywhere with partly chosen content. Under the caller's
/// identity Windows applies the caller's own permissions, so the write can only reach what they could
/// already write themselves.</para>
/// </summary>
/// <param name="logFilePath">Full path of the shared log file to append to.</param>
/// <param name="debugMode">The caller's debug-logging setting. When false, <see cref="LogLevel.Debug"/>
/// entries are dropped, matching <c>LogManager.LogDebug</c> in the main app - both processes write to
/// the same file, so a user with debug logging off must not see DEBUG entries from either.</param>
/// <param name="runAsCaller">Runs the supplied file operation impersonating the pipe client
/// (<c>NamedPipeServerStream.RunAsClient</c>). Throws if the client has gone, which the write path
/// treats like any other failed write.</param>
internal sealed class HelperLogger(string logFilePath, bool debugMode, Action<Action> runAsCaller)
{
    private const string SubsystemName = LoggingConstants.HelperServiceSubsystem;
    private const int WriteMaxAttempts = 3;
    private const int WriteRetryDelayMs = 50;

    // Cumulative counts returned to the tray app via the pipe response so it can raise log alerts.
    public int WarnCount { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Writes a log entry at the given level, mirroring <c>LogManager.LogMessage</c>'s call shape so
    /// both loggers are used identically. WARN and ERROR entries are counted (only on a successful
    /// write) so the helper can return the counts to the tray app via the pipe response.
    /// <see cref="LogLevel.Debug"/> entries are dropped unless the caller has debug logging enabled.
    /// </summary>
    public void LogMessage(string message, LogLevel level)
    {
        // Gate before the write, as LogManager.LogDebug does, so a suppressed entry costs nothing
        // and cannot reach the shared log file.
        if (level == LogLevel.Debug && !debugMode) return;

        string label = level switch
        {
            LogLevel.Warn => LoggingConstants.LevelWarnLabel,
            LogLevel.Error => LoggingConstants.LevelErrorLabel,
            LogLevel.Debug => LoggingConstants.LevelDebugLabel,
            _ => LoggingConstants.LevelInfoLabel,
        };
        if (!WriteLog(message, label)) return;
        if (level == LogLevel.Warn) WarnCount++;
        else if (level == LogLevel.Error) ErrorCount++;
    }

    // Returns true if the entry was successfully written to the file. Callers increment WarnCount /
    // ErrorCount only on success so the tray badge never advertises an entry the user cannot find.
    private bool WriteLog(string message, string paddedLevel)
    {
        string entry = LoggingConstants.FormatLogEntry(DateTime.Now, paddedLevel, SubsystemName, message);
        for (int attempt = 0; attempt < WriteMaxAttempts; attempt++)
        {
            try
            {
                runAsCaller(() =>
                {
                    using var fs = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(fs, Encoding.UTF8);
                    writer.Write(entry);
                });
                return true;
            }
            catch (DirectoryNotFoundException) when (attempt < WriteMaxAttempts - 1)
            {
                // Edge case: AppData subfolder does not yet exist (helper runs before the tray app has
                // created it on a fresh install). Create the directory and let the loop retry.
                // CreateDirectory is idempotent so no per-instance flag is needed. As the caller, for
                // the same reason as the write: SYSTEM must not create directories where the caller points.
                try
                {
                    string? dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir)) runAsCaller(() => Directory.CreateDirectory(dir));
                }
                catch { return false; } // directory creation also failed; log entry is lost
            }
            catch (IOException) when (attempt < WriteMaxAttempts - 1)
            {
                Thread.Sleep(WriteRetryDelayMs); // intentional: WriteLog is synchronous by design; retries are rare and brief
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            // The client disconnected or the pipe was torn down, so there is no identity left to write
            // as. Writing as SYSTEM instead is exactly what this class must not do, so the entry is lost.
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return false; }
        }
        return false;
    }
}
