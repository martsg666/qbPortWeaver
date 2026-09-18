using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>
/// Writes the settings tree to a backup file and reads it back. Covers every settings section plus
/// the app-level values above them, with the non-transferable keys left out in both directions (see
/// <see cref="RegistrySettingsManager.IsTransferableKey"/>).
/// </summary>
/// <remarks>
/// <para>JSON rather than anything delimited: several stored values are folder paths, and a
/// separated format would need either escaping or a separator nobody can type, which is the trap the
/// folder-list separator already had to be migrated out of once.</para>
/// <para><b>No validation of imported values.</b> They are written back as the strings they were.
/// The readers already tolerate anything - <see cref="RegistrySettingsManager.GetInt"/> and
/// <see cref="RegistrySettingsManager.GetBool"/> fall back to the registered default when a stored
/// value will not parse, and <c>SettingsForm.LoadSettings</c> clamps every spinner - because the
/// registry is user-editable and always could contain junk. A second validation layer here would
/// duplicate a contract that already holds, and would have to be kept in step with it.</para>
/// </remarks>
internal static class SettingsTransfer
{
    // Identifies the file as ours before anything is written to the registry from it.
    private const string FileMarker = "qbPortWeaver";

    // Bumped only when the shape changes in a way an older build cannot read. Deliberately separate
    // from the app version, which is recorded alongside purely so a human opening the file can see
    // where it came from: gating imports on the app version would reject files that are perfectly
    // readable just because the writer was a later release.
    private const int CurrentSchema = 1;

    private const string KeyApp = "app";
    private const string KeyAppVersion = "appVersion";
    private const string KeySchema = "schema";
    private const string KeyExportedAt = "exportedAt";
    private const string KeySections = "sections";
    private const string KeyApplication = "application";

    // Indented and minimally escaped, because this file is meant to be opened and read. The default
    // encoder escapes every character outside a conservative set, so a plus sign and a double quote
    // each come out as a six-character backslash-u sequence: a Nicotine+ path and a quoted
    // post-update command turn into something still valid but unreadable for anyone checking what
    // their backup contains, and a trap for anyone editing one by hand. Those sequences are
    // described here rather than written out, because a literal one in this comment does not
    // survive to the file on disk. "Unsafe" names the HTML-injection risk of embedding output in a
    // web page, which is not what this is: a local file read by this app and by a text editor.
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Outcome of an export or import, for the caller to report to the user.</summary>
    /// <param name="Success">False when nothing was written, in which case <paramref name="Message"/> says why.</param>
    /// <param name="Message">A complete sentence suitable for a dialog.</param>
    /// <param name="Applied">Values written. Zero for a failed operation.</param>
    /// <param name="Ignored">Entries not restored: keys this build does not recognise, keys it
    /// refuses to import, and writes that failed. Import only.</param>
    internal sealed record TransferResult(bool Success, string Message, int Applied = 0, int Ignored = 0);

    /// <summary>Default file name offered in the save dialog, dated so successive backups do not collide.</summary>
    internal static string SuggestedFileName => $"qbPortWeaver-settings-{DateTime.Now:yyyy-MM-dd}.json";

    /// <summary>Writes the current settings to <paramref name="path"/>, overwriting it.</summary>
    internal static TransferResult Export(string path)
    {
        try
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            int applied = 0;
            foreach (string section in RegistrySettingsManager.AllSections)
            {
                var values = RegistrySettingsManager.GetSectionForBackup(section);
                if (values.Count == 0) continue;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in values) map[key] = value;
                sections[section] = map;
                applied += map.Count;
            }

            var appValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in RegistrySettingsManager.GetAppValuesForBackup())
            {
                appValues[key] = value;
                applied++;
            }

            var payload = new Dictionary<string, object>
            {
                [KeyApp] = FileMarker,
                [KeyAppVersion] = AppConstants.AppVersion,
                [KeySchema] = CurrentSchema,
                [KeyExportedAt] = DateTimeOffset.Now.ToString("o"),
                [KeySections] = sections,
                [KeyApplication] = appValues,
            };

            AppFiles.WriteAtomic(path, JsonSerializer.Serialize(payload, _writeOptions));
            LogManager.Instance.LogMessage($"Settings exported to {path} ({applied} values)", LogLevel.Info);
            return new(true, $"Saved {applied} settings to:\n{path}", applied);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Settings export failed: {ex.Message}", LogLevel.Error);
            return new(false, $"The settings could not be saved.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Reads <paramref name="path"/> and writes its values into the registry. Refuses the file
    /// outright, changing nothing, when it is not one of ours, was written by a newer schema, or
    /// carries a schema field this build cannot read.
    /// </summary>
    internal static TransferResult Import(string path)
    {
        if (!TryReadBackup(path, out var doc, out var failure))
            return failure;

        using (doc)
        {
            var root = doc.RootElement;
            if (Refuse(root) is { } refusal)
                return refusal;

            // Counts are carried back from each stage rather than accumulated in shared locals, so
            // neither stage can quietly leave the totals inconsistent with what it actually wrote.
            var sections = ApplySections(root);
            var application = ApplyApplicationValues(root);
            int applied = sections.Applied + application.Applied;
            int ignored = sections.Ignored + application.Ignored;

            // Nothing written is not a successful restore, and the record's own contract says so
            // ("False when nothing was written"). Two ways to get here: the file carried nothing this
            // build recognises, or every write was refused by the hive - policy or an ACL on HKCU.
            // The counts cannot tell those apart and the message does not pretend to, but the second
            // is precisely what RegistrySettingsManager.TrySetValue was introduced to surface, and
            // deciding success on the count here is the only thing that carries it to the user.
            // Returning true instead also drove SettingsForm's success branch: an Info dialog rather
            // than a warning, SettingsSaved set, and a "Settings changed" sync cycle for a restore
            // that changed nothing.
            bool success = applied > 0;
            LogManager.Instance.LogMessage(
                success
                    ? $"Settings imported from {path} ({applied} applied, {ignored} ignored)"
                    : $"Settings import from {path} applied nothing ({ignored} ignored)",
                success ? LogLevel.Info : LogLevel.Warn);
            return new(success, BuildImportMessage(applied, ignored), applied, ignored);
        }
    }

    // Reads and parses the file. On failure the caller gets a ready-made result rather than an
    // exception, because every failure here is a user-facing "that file did not work", not a fault.
    private static bool TryReadBackup(
        string path,
        [NotNullWhen(true)] out JsonDocument? doc,
        [NotNullWhen(false)] out TransferResult? failure)
    {
        try
        {
            doc = JsonDocument.Parse(AppFiles.ReadAllTextShared(path));
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Settings import failed to read {path}: {ex.Message}", LogLevel.Error);
            doc = null;
            failure = new(false, $"The file could not be read.\n\n{ex.Message}");
            return false;
        }
    }

    // Returns why the file is refused, or null to proceed. Every check runs before anything is
    // written, so a rejected file leaves the registry exactly as it was.
    private static TransferResult? Refuse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetStringOrNull(KeyApp) != FileMarker)
            return new(false, "That file is not a qbPortWeaver settings backup.");

        // Absent - or an explicit JSON null, which is the same statement - means schema 1, the only
        // shape that ever shipped without the field.
        //
        // Present but unreadable is a different answer and must not collapse into it. AsInt32OrNull
        // yields null for every kind other than Number, and for a Number that is not a whole int32,
        // so a hand-edited "schema": "2" or a later release that changes the field's type would read
        // as 1 under a plain `?? 1` and be imported key by key, past the very guard below. This is the
        // same trap JsonElementExtensions documents for TryGetInt32, one level up: there the danger is
        // a reader that throws, here it is a default that silently answers for a value it could not
        // read. A file whose format version cannot be read is one whose shape this build cannot vouch
        // for, and refusing is what the guard is for.
        bool present = root.TryGetProperty(KeySchema, out var schemaElement) &&
                       schemaElement.ValueKind != JsonValueKind.Null;
        int? schema = present ? schemaElement.AsInt32OrNull() : null;

        if (present && schema is null)
            return new(false,
                "That backup's format version could not be read, so this file cannot be imported safely.\n\n" +
                "Nothing was changed.");

        if ((schema ?? 1) > CurrentSchema)
            return new(false,
                $"That backup was written by a newer version of {AppIdentity.AppName} and cannot be read by this one.\n\n" +
                $"Update {AppIdentity.AppName}, then import it again.");

        return null;
    }

    private static (int Applied, int Ignored) ApplySections(JsonElement root)
    {
        if (!root.TryGetProperty(KeySections, out var sections) || sections.ValueKind != JsonValueKind.Object)
            return (0, 0);

        int applied = 0, ignored = 0;
        foreach (var section in sections.EnumerateObject())
        {
            // An unknown section is ignored whole rather than created: a file from a later build may
            // carry a section this one has no reader for, and writing it would leave dead keys in
            // the registry that nothing ever cleans up.
            if (!RegistrySettingsManager.AllSections.Contains(section.Name, StringComparer.OrdinalIgnoreCase) ||
                section.Value.ValueKind != JsonValueKind.Object)
            {
                ignored += CountValues(section.Value);
                continue;
            }

            var result = ApplySection(section.Name, section.Value);
            applied += result.Applied;
            ignored += result.Ignored;
        }
        return (applied, ignored);
    }

    private static (int Applied, int Ignored) ApplySection(string section, JsonElement values)
    {
        int applied = 0, total = 0;
        foreach (var entry in values.EnumerateObject())
        {
            total++;
            if (TryApplySectionValue(section, entry)) applied++;
        }
        return (applied, total - applied);
    }

    private static (int Applied, int Ignored) ApplyApplicationValues(JsonElement root)
    {
        if (!root.TryGetProperty(KeyApplication, out var appValues) || appValues.ValueKind != JsonValueKind.Object)
            return (0, 0);

        int applied = 0, total = 0;
        foreach (var entry in appValues.EnumerateObject())
        {
            total++;
            if (TryApplyApplicationValue(entry)) applied++;
        }
        return (applied, total - applied);
    }

    // Writes one section value, or reports that it was skipped. Non-transferable keys are refused
    // even when present in the file: the stored form of a password is a DPAPI blob tied to one user
    // on one machine, so a hand-edited plaintext value written here would not decrypt, and would
    // replace a credential that currently works.
    private static bool TryApplySectionValue(string section, JsonProperty entry)
    {
        string? value = entry.Value.AsStringOrNull();
        // A key this build has no reader for is skipped for the same reason an unrecognised section
        // is: writing it leaves a value in the registry that nothing consumes and nothing removes.
        // It also keeps the reported "not recognised, skipped" count honest, which it was not while
        // only whole sections were checked.
        if (value is null ||
            !RegistrySettingsManager.IsKnownSectionKey(section, entry.Name) ||
            !RegistrySettingsManager.IsTransferableKey(entry.Name))
            return false;

        // The write swallows its own failures, so the result is what decides whether this counts as
        // restored. Without it a locked or policy-restricted hive reports a full restore having
        // written nothing.
        return RegistrySettingsManager.TrySetValue(section, entry.Name, value);
    }

    // Same allow-list as the export side, so a hand-edited file cannot reintroduce installer-owned
    // or state keys that the export deliberately leaves out.
    private static bool TryApplyApplicationValue(JsonProperty entry)
    {
        string? value = entry.Value.AsStringOrNull();
        if (value is null ||
            !RegistrySettingsManager.IsBackupAppKey(entry.Name) ||
            !RegistrySettingsManager.IsTransferableKey(entry.Name))
            return false;

        return RegistrySettingsManager.TrySetAppValue(entry.Name, value);
    }

    private static int CountValues(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().Count() : 1;

    private static string BuildImportMessage(int applied, int ignored)
    {
        // Zero applied opens differently, because this text lands in a warning rather than an
        // information dialog and "Restored 0 settings." is a poor first line for one. It also states
        // the outcome the user needs, which the count alone does not: their settings are untouched.
        if (applied == 0)
        {
            string nothing = "No settings were restored, so yours are unchanged.";
            if (ignored > 0)
                nothing += $" All {ignored} entries in the file were skipped, either because this " +
                           "version does not recognise them or because they could not be written. " +
                           "The log has the detail.";
            // No secrets paragraph here, unlike the restored case below. It explains what a restore
            // did not bring across; with nothing brought across there is no such gap to explain, and
            // on a warning it would bury the one sentence that matters.
            return nothing;
        }

        string message = $"Restored {applied} settings.";
        // Covers both reasons an entry is not counted as restored - unknown to this version, or a
        // write that failed - because the user cannot act differently on the two and the log has the
        // detail either way. Claiming only the first would be untrue whenever the hive is locked.
        if (ignored > 0)
            message += $" {ignored} entries were skipped, either because this version does not " +
                       "recognise them or because they could not be written. The log has the detail.";

        // Said on every restore that landed something, not only when entries were skipped. Secrets are
        // never in the file at all, so there is nothing for the skipped count to hint at, and someone
        // restoring onto a new machine has no other way to learn that the client will not connect
        // until they re-enter them.
        return message +
            "\n\nPasswords and the TMDB API key are not included in a backup, because Windows ties them to " +
            "one user account on one machine. They have been left as they were - re-enter them if this " +
            "backup came from another PC.";
    }
}
