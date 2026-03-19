using System.Text;

namespace qbPortWeaver.HelperService;

// Writes log entries to the shared qbPortWeaver log file in the same format as the main app's
// LogManager: "yyyy-MM-dd HH:mm:ss | LEVEL | message"
// Instantiated per connection with the log file path received from the tray app via the pipe.
// Retries briefly on sharing violation since the main app opens the same file with FileShare.Read.
internal sealed class HelperLogger(string logFilePath)
{
    // Log prefix for the helper service subsystem - mirrors AppConstants.MediaManagerLogPrefix in the main app
    private const string HelperServiceLogPrefix = "[HelperService] ";

    public void LogInfo(string message)  => WriteLog(message, "INFO ");
    public void LogWarn(string message)  => WriteLog(message, "WARN ");
    public void LogError(string message) => WriteLog(message, "ERROR");

    private void WriteLog(string message, string paddedLevel)
    {
        string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {paddedLevel} | {HelperServiceLogPrefix}{message}{Environment.NewLine}";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs     = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs, Encoding.UTF8);
                writer.Write(entry);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50); // intentional: WriteLog is synchronous by design; retries are rare and brief
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}
