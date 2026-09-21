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
    private const string LogFilePattern = $"{AppIdentity.LogFileName}*";

    /// <summary>
    /// Heading for the app-level values in a settings listing, distinguishing them from the settings
    /// sections below.
    /// </summary>
    /// <remarks>Shared with <see cref="DiagnosticsService.GetSettingsSnapshot"/> so the bundle's
    /// snapshot and the pasted report label the same group the same way. Checked against every
    /// <c>Section*</c> constant in <see cref="RegistrySettingsManager"/>: it collides with none, so it
    /// cannot be mistaken for a real section.</remarks>
    internal const string ApplicationGroupName = "application";

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

            // Atomic, like the settings backup: a plain write truncates the destination first, so an
            // IO error part way through would leave a short file at the chosen path - which still
            // opens, and would be sent to a maintainer as though it were the whole bundle. That is
            // the outcome the in-memory assembly above exists to prevent, and it would have leaked
            // straight back in here.
            AppFiles.WriteAtomic(path, buffer.ToArray());
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

        // The app-level values first, mirroring the registry tree: they sit above the sections, and
        // they are where the service search terms, adapter names, process names and the ProtonVPN log
        // path live. Those are the settings behind "ProtonVPN is not detected" and "adapter not
        // found", so a bundle that omitted them - as this did - was missing the answer to most of
        // what it gets sent for.
        var appValues = RegistrySettingsManager.GetAppSnapshot();
        if (appValues.Count > 0)
        {
            sb.AppendLine($"[{ApplicationGroupName}]");
            foreach (var (key, value) in appValues)
                sb.AppendLine($"  {key} = {value}");
            sb.AppendLine();
        }

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
                // Warn, not Debug. AddFile creates the entry before the copy that can fail, so the
                // archive keeps a short file while this counts it as absent. The truncated data is
                // still worth having; what is not acceptable is that nobody was told - at Debug it
                // was recorded nowhere at all with debug mode off, and the bundle then went to a
                // maintainer with a log that simply stops.
                //
                // The partial entry cannot be undone: ZipArchiveEntry.Delete throws
                // NotSupportedException in ZipArchiveMode.Create, and buffering the file to avoid
                // creating the entry early would defeat the streaming this method exists to do (the
                // rotated logs run to tens of megabytes each). So it is reported rather than fixed.
                LogManager.Instance.LogMessage(
                    $"Could not add '{Path.GetFileName(file)}' to the support bundle, so it may be incomplete in the archive: {ex.Message}",
                    LogLevel.Warn);
            }
        }
        return added;
    }

    private static IEnumerable<string> EnumerateDataFiles()
    {
        foreach (string log in FindLogFiles())
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
        // CreateEntry stamps the entry with the current time, which would make every rotated log
        // look as though it were written the moment the bundle was made. Whether the window that
        // explains a problem is even in the archive is the first thing anyone reading one wants to
        // know, and the filesystem already knows it.
        entry.LastWriteTime = File.GetLastWriteTime(path);
        using var destination = entry.Open();
        source.CopyTo(destination);
        return 1;
    }

    // The current log and its rotated backups, current first and oldest last (rotation numbers
    // ascend with age, so an ordinal sort puts them in that order), or an empty list when the folder
    // cannot be walked. An unreadable log set is skipped rather than refusing the bundle: the report
    // and the settings snapshot are still worth having.
    //
    // The materialisation inside the try is load-bearing, not a style choice: OrderBy is deferred, so
    // handing back the lazy query would run the folder walk at the caller's foreach, outside this
    // catch, and the guard would never fire. The return type is List rather than IEnumerable so the
    // compiler enforces that rather than this comment - returning the lazy query directly no longer
    // builds. Same reasoning, and the same shape, as NatPmpManager.GetActiveNetworkInterfaces.
    private static List<string> FindLogFiles()
    {
        try
        {
            return [.. Directory.EnumerateFiles(AppFiles.AppDataFolder, LogFilePattern)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            // Warn, not Debug, for the same reason as the per-file catch in AddDataFiles: skipping
            // is the right degradation, staying silent about it is not. This one loses the *whole*
            // log set, which is the part of a bundle anyone actually reads, and the user's only
            // other signal is a file count they have no reason to find surprising. At Debug it was
            // recorded nowhere at all with debug mode off.
            LogManager.Instance.LogMessage(
                $"Could not read the log folder, so the support bundle contains no log files: {ex.Message}",
                LogLevel.Warn);
            return [];
        }
    }

    // Returns the number of entries added, so the callers can total them without counting twice.
    //
    // AppFiles.Utf8NoBom, not Encoding.UTF8: the latter emits a byte-order mark, and the entries
    // written through here exist to be opened and pasted into an issue, where a BOM rides along
    // invisibly on a copied first line.
    //
    // Not a claim about the bundle as a whole. The log files AddFile copies in alongside these do
    // carry one: LogManager.WriteRaw writes through a StreamWriter over Encoding.UTF8, which emits
    // the preamble whenever an append stream starts at position zero, so every log file gets one at
    // creation and keeps it through rotation. That is left alone deliberately - the readers all cope
    // (AppFiles detects it, and LogViewerForm's tail offset is computed from the stream position
    // precisely so a consumed BOM cannot shift it), and rewriting the log encoding to tidy a comment
    // would change files mid-rotation for no benefit. Every other writer really is BOM-free: all the
    // AppFiles.WriteAtomic overloads either default to it or pass Utf8NoBom.
    private static int AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), AppFiles.Utf8NoBom);
        writer.Write(content);
        return 1;
    }
}
