namespace qbPortWeaver
{
    /// <summary>Orchestrates media file renaming on each sync cycle when the Media Manager feature is enabled.</summary>
    public static class MediaManagerService
    {
        /// <summary>
        /// Runs one media scan cycle. Returns immediately if the feature is disabled or the TMDB API key is not configured.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled))
                return;

            var apiKey = RegistrySettingsManager.GetEncryptedValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}TMDB API key not configured - skipping scan", LogLevel.Warn);
                return;
            }

            bool dryRun             = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
            bool createFolders      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);
            bool deleteEmptyFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders);

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Scan started (dryRun={dryRun}, createFolders={createFolders}, deleteEmptyFolders={deleteEmptyFolders})", LogLevel.Info);

            var tmdb         = new TmdbClient(apiKey);
            var movieRenamer = new MovieRenamer(tmdb, dryRun, createFolders);
            var tvShowRenamer = new TvShowRenamer(tmdb, dryRun, createFolders);

            foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaMovieFolders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await movieRenamer.ProcessMoviesFolderAsync(folder).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping movie folder '{folder}': {ex.Message}", LogLevel.Warn);
                }
            }

            foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaTvShowFolders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await tvShowRenamer.ProcessTvShowsFolderAsync(folder).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping TV folder '{folder}': {ex.Message}", LogLevel.Warn);
                }
            }

            if (deleteEmptyFolders)
            {
                foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaMovieFolders))
                    CleanupEmptyFolders(folder, dryRun);
                foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaTvShowFolders))
                    CleanupEmptyFolders(folder, dryRun);
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Scan complete", LogLevel.Info);
        }

        /// <summary>
        /// Returns rename proposals for all configured folders without modifying any files.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="apiKey">TMDB API key used to look up movie and TV show metadata.</param>
        /// <param name="createFolders">When true, proposals include moving files into Plex-recommended subfolders.</param>
        /// <param name="movieFolders">Root folders to scan for movie files.</param>
        /// <param name="tvShowFolders">Root folders to scan for TV episode files.</param>
        /// <param name="cancellationToken">Token to cancel the scan between folders.</param>
        public static async Task<List<RenameProposal>> ScanAsync(string apiKey, bool createFolders, string[] movieFolders, string[] tvShowFolders, CancellationToken cancellationToken)
        {
            var proposals = new List<RenameProposal>();

            var tmdb = new TmdbClient(apiKey);
            // dryRun is irrelevant for scan - scan methods never touch files
            var movieRenamer  = new MovieRenamer(tmdb, dryRun: true, createFolders);
            var tvShowRenamer = new TvShowRenamer(tmdb, dryRun: true, createFolders);

            foreach (var folder in movieFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    proposals.AddRange(await movieRenamer.ScanMoviesFolderAsync(folder).ConfigureAwait(false));
                }
                catch (IOException ex)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping movie folder '{folder}': {ex.Message}", LogLevel.Warn);
                }
            }

            foreach (var folder in tvShowFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    proposals.AddRange(await tvShowRenamer.ScanTvShowsFolderAsync(folder).ConfigureAwait(false));
                }
                catch (IOException ex)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping TV folder '{folder}': {ex.Message}", LogLevel.Warn);
                }
            }

            return proposals;
        }

        /// <summary>
        /// Applies a set of rename proposals, respecting any user edits made to the proposed paths in the UI grid.
        /// Proposals are typically produced by <see cref="ScanAsync"/> but may have been modified by the user before calling this method.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="proposals">The rename proposals to apply. Each proposal's <see cref="RenameProposal.ProposedPath"/> is used as the rename target.</param>
        /// <param name="cancellationToken">Token to cancel the operation between renames.</param>
        public static Task ApplyProposalsAsync(IEnumerable<RenameProposal> proposals, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                foreach (var proposal in proposals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LogManager.Instance.LogMessage(
                        $"{AppConstants.MediaManagerLogPrefix}Renaming '{proposal.OriginalPath}' -> '{proposal.ProposedPath}'",
                        LogLevel.Info);
                    try
                    {
                        MoveFile(proposal.OriginalPath, proposal.ProposedPath);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Instance.LogMessage(
                            $"{AppConstants.MediaManagerLogPrefix}Failed to rename '{Path.GetFileName(proposal.OriginalPath)}': {ex.Message}",
                            LogLevel.Error);
                    }
                }
            }, cancellationToken);

        /// <summary>
        /// Deletes subdirectories of <paramref name="rootFolder"/> that are empty or contain only <c>.nfo</c> files.
        /// Walks bottom-up so nested empty folders are cleaned in a single pass. The root folder itself is never deleted.
        /// </summary>
        public static void CleanupEmptyFolders(string rootFolder, bool dryRun)
        {
            if (!Directory.Exists(rootFolder)) return;

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(rootFolder, "*", SearchOption.AllDirectories);
            }
            catch (IOException ex)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping folder cleanup for '{rootFolder}': {ex.Message}", LogLevel.Warn);
                return;
            }

            int deleted = 0;
            foreach (var dir in directories.OrderByDescending(d => d.Length))
            {
                if (!Directory.Exists(dir)) continue;

                var (removable, hasOnlyNfo) = IsRemovableFolder(dir);
                if (!removable) continue;

                string reason = hasOnlyNfo ? "nfo-only" : "empty";
                if (dryRun)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Would delete {reason} folder: '{Path.GetFileName(dir)}'", LogLevel.Info);
                }
                else
                {
                    DeleteFolder(dir, hasOnlyNfo);
                }
                deleted++;
            }

            if (deleted > 0)
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MediaManagerService.CleanupEmptyFolders: {deleted} folder(s) {(dryRun ? "would be deleted" : "deleted")} under '{rootFolder}'");
        }

        // Returns (true, hasOnlyNfo) when the folder is empty or contains only .nfo files and has no subdirectories
        private static (bool Removable, bool HasOnlyNfo) IsRemovableFolder(string dir)
        {
            var files = Directory.GetFiles(dir);
            if (Directory.GetDirectories(dir).Length > 0) return (false, false);
            if (files.Length == 0) return (true, false);
            bool allNfo = files.All(f => Path.GetExtension(f).Equals(".nfo", StringComparison.OrdinalIgnoreCase));
            return (allNfo, allNfo);
        }

        // Deletes .nfo files (if any) then removes the directory
        private static void DeleteFolder(string dir, bool hasNfoFiles)
        {
            try
            {
                if (hasNfoFiles)
                {
                    foreach (var nfo in Directory.GetFiles(dir))
                        File.Delete(nfo);
                }
                Directory.Delete(dir);
                string reason = hasNfoFiles ? "nfo-only" : "empty";
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Deleted {reason} folder: '{Path.GetFileName(dir)}'", LogLevel.Info);
            }
            catch (IOException ex)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Failed to delete folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn);
            }
        }

        private static string[] GetFolders(string key)
        {
            var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
            if (string.IsNullOrWhiteSpace(value))
                return [];
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Moves a file to targetPath, creating the target directory if needed. Logs a warning and skips the move if the target already exists.
        internal static void MoveFile(string sourcePath, string targetPath)
        {
            var targetName = Path.GetFileName(targetPath);
            if (File.Exists(targetPath))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipped rename - target already exists: '{targetName}'", LogLevel.Warn);
                return;
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Move(sourcePath, targetPath);
            LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MediaManagerService.MoveFile: Renamed OK '{targetName}'");
        }
    }
}
