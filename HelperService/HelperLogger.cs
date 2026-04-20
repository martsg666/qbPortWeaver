using System.Text;

namespace qbPortWeaver.HelperService;

/// <summary>
/// Writes log entries to the shared qbPortWeaver log file in the same format as the main app's
/// LogManager: "yyyy-MM-dd HH:mm:ss | LEVEL | Subsystem | message".
/// Instantiated per connection with the log file path received from the tray app via the pipe.
/// Retries briefly on sharing violations in case another process holds the file.
/// </summary>
internal sealed class HelperLogger(string logFilePath)
{
    // Must match LogManager.Subsystem.HelperService and LogManager.Subsystem.MaxLength in qbPortWeaver
    private const string SubsystemName        = "HelperService";
    private const int    SubsystemColumnWidth = 13;
    private const int    WriteMaxAttempts     = 3;
    private const int    WriteRetryDelayMs    = 50;

    public void LogInfo(string message)  => WriteLog(message, "INFO ");
    public void LogWarn(string message)  => WriteLog(message, "WARN ");
    public void LogError(string message) => WriteLog(message, "ERROR");

    private void WriteLog(string message, string paddedLevel)
    {
        string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {paddedLevel} | {SubsystemName.PadRight(SubsystemColumnWidth)} | {message}{Environment.NewLine}";
        for (int attempt = 0; attempt < WriteMaxAttempts; attempt++)
        {
            try
            {
                using var fs     = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs, Encoding.UTF8);
                writer.Write(entry);
                return;
            }
            catch (IOException) when (attempt < WriteMaxAttempts - 1)
            {
                Thread.Sleep(WriteRetryDelayMs); // intentional: WriteLog is synchronous by design; retries are rare and brief
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}
