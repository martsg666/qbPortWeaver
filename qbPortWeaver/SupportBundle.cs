using System.IO.Compression;
using System.Text;

namespace qbPortWeaver;

/// <summary>
/// Collects the files a support request needs into one zip: the diagnostics report, a masked
/// settings snapshot, the current log and its rotated backups, the status file and the port history.
/// </summary>
/// <remarks>
/// Exists because assembling this by hand is both tedious and unreliable. The log rotates, so by the
/// time a problem is described the window that explains it may already have moved into a backup
/// file the user does not know to send, or out of the set entirely.
/// <para><b>Everything here is masked, not omitted</b>, which is the opposite of
/// <see cref="SettingsTransfer"/>. A backup leaves secrets out because a <c>***</c> placeholder
/// would restore as a literal password; a bundle keeps the key visible with its value masked,
/// because "this setting is present but I will not show you its value" is itself diagnostic.</para>
/// </remarks>
internal static class SupportBundle
{
    private const string ReportEntryName = "diagnostics-report.txt";
    // Named a snapshot, and plain text rather than JSON, so it cannot be mistaken for the restorable
    // backup that SettingsTransfer writes. Both describe the same settings, but only one of them can
    // be fed back into the app, and the difference should be visible before anyone opens the file.
    private const string SettingsEntryName = "settings-snapshot.txt";

    // The current log and every rotated backup. LogManager names them by appending a number, so one
    // pattern catches the whole set without this having to know how many it keeps. Derived from
    // AppIdentity rather than spelled out: a bundle that silently contains no logs is the one
    // failure this feature cannot afford, and a literal here would produce exactly that the day the
    // file name changes.
    private static readonly string LogFilePattern = $"{AppIdentity.LogFileName}*";

    /// <summary>Default file name offered in the save dialog, stamped to the minute so successive bundles do not collide.</summary>
    internal static string SuggestedFileName => $"qbPortWeaver-support-{DateTime.Now:yyyy-MM-dd-HHmm}.zip";

    /// <summary>
    /// Writes the bundle to <paramref name="path"/>, overwriting it. <paramref name="diagnosticsReport"/>
    /// is the plain-text report the Diagnostics dialog is currently showing, passed in rather than
    /// re-run so the bundle matches what the user is looking at.
    /// </summary>
    internal static SettingsTransfer.TransferResult Create(string path, string diagnosticsReport)
    {
        try
        {
            // Built in memory and written once: a half-finished zip left behind by a failure part way
            // through is worse than no zip, because it still opens.
            using var buffer = new MemoryStream();
            int files;
            // The archive must be disposed before the bytes are read out: ZipArchive writes its
            // central directory on dispose, so a buffer snapshot taken inside this block produces a
            // file that is missing the index and will not open.
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                files = AddText(zip, ReportEntryName, diagnosticsReport);
                files += AddText(zip, SettingsEntryName, BuildMaskedSettings());
                files += AddDataFiles(zip);
            }

            File.WriteAllBytes(path, buffer.ToArray());
            LogManager.Instance.LogMessage($"Support bundle written to {path} ({files} files)", LogLevel.Info);
            return new(true, $"Saved {files} files to:\n{path}", files);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Support bundle failed: {ex.Message}", LogLevel.Error);
            return new(false, $"The support bundle could not be saved.\n\n{ex.Message}");
        }
    }

    // Every section, not just the three the pasted report carries. The report is trimmed because it
    // goes on a clipboard and into an issue body; a zip has no such pressure, and the section that
    // explains a problem is often the one for a client the user is not currently running.
    private static string BuildMaskedSettings()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppIdentity.AppName} {AppConstants.AppVersion} settings - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Passwords, tokens and API keys are shown as *** .");
        sb.AppendLine();
        foreach (string section in RegistrySettingsManager.AllSections)
        {
            var values = RegistrySettingsManager.GetSectionSnapshot(section);
            if (values.Count == 0) continue;
            sb.AppendLine($"[{section}]");
            foreach (var (key, value) in values)
                sb.AppendLine($"  {key} = {value}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // The log set, the status file and the port history, each skipped silently when absent: a fresh
    // install has no rotated logs and no history, which is not a failure worth refusing a bundle over.
    private static int AddDataFiles(ZipArchive zip)
    {
        int added = 0;
        foreach (string file in EnumerateDataFiles())
        {
            try
            {
                added += AddFile(zip, file);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"SupportBundle.AddDataFiles: {Path.GetFileName(file)} - {ex.Message}");
            }
        }
        return added;
    }

    private static IEnumerable<string> EnumerateDataFiles()
    {
        string folder = AppFiles.AppDataFolder;
        List<string> logs;
        try
        {
            // ToList inside the try is load-bearing: OrderBy is deferred, so without it the folder
            // walk would run at the foreach below, outside this catch, and an IO or access error
            // would escape and abort the whole bundle - the opposite of treating an unreadable log
            // set as something to skip.
            logs = [.. Directory.EnumerateFiles(folder, LogFilePattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"SupportBundle.EnumerateDataFiles: {ex.Message}");
            yield break;
        }

        foreach (string log in logs)
            yield return log;

        foreach (string path in new[] { AppFiles.GetStatusFilePath(), PortHistoryManager.HistoryFilePath }.Where(File.Exists))
            yield return path;
    }

    // Copies a file on disk straight into the archive. Streamed rather than read into a string: the
    // rotated logs run to tens of megabytes each, and shared access is required because the current
    // log is open for writing by this process.
    private static int AddFile(ZipArchive zip, string path)
    {
        using var source = AppFiles.OpenSharedForCopy(path);
        var entry = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
        using var destination = entry.Open();
        source.CopyTo(destination);
        return 1;
    }

    // Returns the number of entries added, so the callers can total them without counting twice.
    private static int AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
        return 1;
    }
}
