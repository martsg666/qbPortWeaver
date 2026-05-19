using System.Text;
using qbPortWeaver.Shared;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Writes log entries to the shared qbPortWeaver log file in the same format as the main app's
/// LogManager: "yyyy-MM-dd HH:mm:ss | LEVEL | Subsystem | message".
/// Instantiated per connection with the log file path received from the tray app via the pipe.
/// Retries briefly on sharing violations in case another process holds the file.
/// </summary>
internal sealed class HelperLogger(string logFilePath)
{
    private const string SubsystemName = LoggingConstants.HelperServiceSubsystem;
    private const int WriteMaxAttempts = 3;
    private const int WriteRetryDelayMs = 50;

    // Cumulative counts returned to the tray app via the pipe response so it can raise log alerts.
    public int WarnCount { get; private set; }
    public int ErrorCount { get; private set; }

    public void LogInfo(string message) => WriteLog(message, LoggingConstants.LevelInfoLabel);
    public void LogWarn(string message) { if (WriteLog(message, LoggingConstants.LevelWarnLabel)) WarnCount++; }
    public void LogError(string message) { if (WriteLog(message, LoggingConstants.LevelErrorLabel)) ErrorCount++; }

    // Returns true if the entry was successfully written to the file. Callers increment WarnCount /
    // ErrorCount only on success so the tray badge never advertises an entry the user cannot find.
    private bool WriteLog(string message, string paddedLevel)
    {
        string entry = LoggingConstants.FormatLogEntry(DateTime.Now, paddedLevel, SubsystemName, message);
        for (int attempt = 0; attempt < WriteMaxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs, Encoding.UTF8);
                writer.Write(entry);
                return true;
            }
            catch (DirectoryNotFoundException) when (attempt < WriteMaxAttempts - 1)
            {
                // Edge case: AppData subfolder does not yet exist (helper runs before the tray app has
                // created it on a fresh install). Create the directory and let the loop retry.
                // CreateDirectory is idempotent so no per-instance flag is needed.
                try
                {
                    string? dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                }
                catch { return false; } // directory creation also failed; log entry is lost
            }
            catch (IOException) when (attempt < WriteMaxAttempts - 1)
            {
                Thread.Sleep(WriteRetryDelayMs); // intentional: WriteLog is synchronous by design; retries are rare and brief
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }
}
