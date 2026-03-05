using System.Text.Json;

namespace qbPortWeaver
{
    public static class StatusManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // Write status dictionary to JSON file (atomic write via temp file)
        public static void Write(Dictionary<string, object?> status)
        {
            string filePath = AppConstants.GetStatusFilePath();
            string tempPath = filePath + ".tmp";

            try
            {
                string json = JsonSerializer.Serialize(status, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to write status file: {ex.Message}", LogLevel.Warn);
                try { File.Delete(tempPath); }
                catch (Exception) { /* Best-effort cleanup; ignore if temp file cannot be deleted. */ }
            }
        }
    }
}
