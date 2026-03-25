namespace qbPortWeaver
{
    /// <summary>Orchestrates media file imports on each sync cycle when the Media Manager feature is enabled.</summary>
    public static class MediaManagerService
    {
        /// <summary>
        /// Runs one media import cycle. Returns immediately if the feature is disabled, the TMDB API key is not configured,
        /// or no library paths are set.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled))
                return;

            var apiKey = RegistrySettingsManager.GetEncryptedValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogManager.Instance.LogDebug("MediaManagerService.RunAsync: TMDB API key not configured - skipping scan", Subsystem.MediaManager);
                return;
            }

            string moviesLibraryPath  = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaMoviesLibraryPath);
            string tvShowsLibraryPath = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaTvShowsLibraryPath);

            if (string.IsNullOrWhiteSpace(moviesLibraryPath) && string.IsNullOrWhiteSpace(tvShowsLibraryPath))
            {
                LogManager.Instance.LogDebug("MediaManagerService.RunAsync: No library paths configured - skipping scan", Subsystem.MediaManager);
                return;
            }

            bool dryRun             = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
            bool createFolders      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);
            bool deleteEmptyFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders);
            var importMode = ParseImportMode(RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaImportMode));

            LogManager.Instance.LogMessage($"Scan started (dryRun={dryRun}, createFolders={createFolders}, deleteEmptyFolders={deleteEmptyFolders}, importMode={importMode})", LogLevel.Info, Subsystem.MediaManager);
            LogManager.Instance.LogDebug(
                $"MediaManagerService.RunAsync [media]: {RegistrySettingsManager.KeyMediaEnabled}=true, " +
                $"{RegistrySettingsManager.KeyTmdbApiKey}=***, " +
                $"{RegistrySettingsManager.KeyMediaSourceFolders}={string.Join(";", GetFolders(RegistrySettingsManager.KeyMediaSourceFolders))}, " +
                $"{RegistrySettingsManager.KeyMediaMoviesLibraryPath}={moviesLibraryPath}, " +
                $"{RegistrySettingsManager.KeyMediaTvShowsLibraryPath}={tvShowsLibraryPath}, " +
                $"{RegistrySettingsManager.KeyMediaDryRun}={dryRun}, " +
                $"{RegistrySettingsManager.KeyMediaCreateFolders}={createFolders}, " +
                $"{RegistrySettingsManager.KeyMediaDeleteEmptyFolders}={deleteEmptyFolders}, " +
                $"{RegistrySettingsManager.KeyMediaImportMode}={importMode}",
                Subsystem.MediaManager);

            FileImporter.LoadSourceCache();
            FileImporter.BuildLibraryIndex(force: false, moviesLibraryPath, tvShowsLibraryPath);

            var tmdb = new TmdbClient(apiKey);

            foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaSourceFolders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessSourceFolderAsync(folder, tmdb, dryRun, createFolders, importMode, moviesLibraryPath, tvShowsLibraryPath).ConfigureAwait(false);
            }

            if (deleteEmptyFolders)
                CleanupSourceFolders(dryRun, cancellationToken);

            FileImporter.SaveSourceCache();
            FileImporter.SaveLibraryCache();
            LogManager.Instance.LogMessage("Scan completed", LogLevel.Info, Subsystem.MediaManager);
        }

        // Classifies and processes a single source folder, running both movie and TV show processors.
        private static async Task ProcessSourceFolderAsync(
            string folder, TmdbClient tmdb, bool dryRun, bool createFolders, ImportMode importMode,
            string moviesLibraryPath, string tvShowsLibraryPath)
        {
            if (!Directory.Exists(folder))
            {
                LogManager.Instance.LogMessage($"Source folder not found: {folder}", LogLevel.Error, Subsystem.MediaManager);
                return;
            }

            LogManager.Instance.LogMessage($"Scanning source folder: {folder}", LogLevel.Info, Subsystem.MediaManager);
            var (movieFiles, movieDirs, tvFiles, tvDirs) = ClassifySourceFolder(folder);

            if (!string.IsNullOrWhiteSpace(moviesLibraryPath) && (movieFiles.Length > 0 || movieDirs.Length > 0))
            {
                var movieProcessor = new MovieProcessor(tmdb, dryRun, createFolders, moviesLibraryPath, importMode);
                try
                {
                    await movieProcessor.ProcessMoviesAsync(folder, movieFiles, movieDirs).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (movies): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            if (!string.IsNullOrWhiteSpace(tvShowsLibraryPath) && (tvFiles.Length > 0 || tvDirs.Length > 0))
            {
                var tvShowProcessor = new TvShowProcessor(tmdb, dryRun, createFolders, tvShowsLibraryPath, importMode);
                try
                {
                    await tvShowProcessor.ProcessTvShowsAsync(folder, tvFiles, tvDirs).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (TV shows): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }
        }

        // Runs folder cleanup for all configured source folders.
        private static void CleanupSourceFolders(bool dryRun, CancellationToken cancellationToken)
        {
            foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaSourceFolders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { CleanupEmptyFolders(folder, dryRun); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder cleanup for '{folder}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }
        }

        /// <summary>
        /// Returns import proposals for all configured source folders without modifying any files.
        /// Only processors whose library path is configured will produce proposals.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="apiKey">TMDB API key used to look up movie and TV show metadata.</param>
        /// <param name="createFolders">When true, proposals include Plex-recommended subfolders in the library.</param>
        /// <param name="sourceFolders">Download or seeding folders to scan for both movies and TV shows.</param>
        /// <param name="moviesLibraryPath">Library folder for movies. Empty to skip movie processing.</param>
        /// <param name="tvShowsLibraryPath">Library folder for TV shows. Empty to skip TV show processing.</param>
        /// <param name="cancellationToken">Token to cancel the scan between folders.</param>
        public static async Task<List<MediaProposal>> ScanAsync(string apiKey, bool createFolders, string[] sourceFolders,
            string moviesLibraryPath, string tvShowsLibraryPath, CancellationToken cancellationToken)
        {
            var proposals = new List<MediaProposal>();

            FileImporter.LoadSourceCache();
            FileImporter.BuildLibraryIndex(force: true, moviesLibraryPath, tvShowsLibraryPath);

            var tmdb = new TmdbClient(apiKey);

            foreach (var folder in sourceFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(folder))
                    await ScanSourceFolderAsync(folder, tmdb, createFolders, moviesLibraryPath, tvShowsLibraryPath, proposals).ConfigureAwait(false);
            }

            FileImporter.SaveSourceCache();
            FileImporter.SaveLibraryCache();
            return proposals;
        }

        // Classifies and scans a single source folder, appending proposals for both movies and TV shows.
        private static async Task ScanSourceFolderAsync(
            string folder, TmdbClient tmdb, bool createFolders,
            string moviesLibraryPath, string tvShowsLibraryPath, List<MediaProposal> proposals)
        {
            var (movieFiles, movieDirs, tvFiles, tvDirs) = ClassifySourceFolder(folder);

            if (!string.IsNullOrWhiteSpace(moviesLibraryPath) && (movieFiles.Length > 0 || movieDirs.Length > 0))
            {
                var movieProcessor = new MovieProcessor(tmdb, dryRun: true, createFolders, moviesLibraryPath);
                try
                {
                    proposals.AddRange(await movieProcessor.ScanMoviesAsync(movieFiles, movieDirs).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (movies): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            if (!string.IsNullOrWhiteSpace(tvShowsLibraryPath) && (tvFiles.Length > 0 || tvDirs.Length > 0))
            {
                var tvShowProcessor = new TvShowProcessor(tmdb, dryRun: true, createFolders, tvShowsLibraryPath);
                try
                {
                    proposals.AddRange(await tvShowProcessor.ScanTvShowsAsync(tvFiles, tvDirs).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (TV shows): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }
        }

        /// <summary>
        /// Applies a set of import proposals, transferring files from source folders into the library.
        /// Proposals are typically produced by <see cref="ScanAsync"/> but may have been modified by the user before calling this method.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="proposals">The import proposals to apply. Each proposal's <see cref="MediaProposal.ProposedPath"/> is the library target.</param>
        /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
        /// <param name="cancellationToken">Token to cancel the operation between imports.</param>
        public static Task ApplyProposalsAsync(IEnumerable<MediaProposal> proposals, ImportMode importMode,
            CancellationToken cancellationToken, IProgress<(int Current, int Total, string FileName)>? progress = null)
            => Task.Run(() =>
            {
                var list = proposals as IList<MediaProposal> ?? proposals.ToList();
                int total = list.Count;
                int current = 0;
                foreach (var proposal in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current++;
                    progress?.Report((current, total, Path.GetFileName(proposal.OriginalPath)));
                    LogManager.Instance.LogMessage(
                        $"Importing '{proposal.OriginalPath}' -> '{proposal.ProposedPath}'",
                        LogLevel.Info, Subsystem.MediaManager);
                    try
                    {
                        FileImporter.ImportFile(proposal.OriginalPath, proposal.ProposedPath, importMode);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Instance.LogMessage(
                            $"Failed to import '{Path.GetFileName(proposal.OriginalPath)}': {ex.Message}",
                            LogLevel.Error, Subsystem.MediaManager);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Skipped folder cleanup for '{rootFolder}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            int deleted = 0;
            foreach (var dir in directories.OrderByDescending(d => d.Length))
            {
                if (TryCleanupFolder(dir, dryRun))
                    deleted++;
            }

            if (deleted > 0)
                LogManager.Instance.LogDebug($"MediaManagerService.CleanupEmptyFolders: {deleted} folder(s) {(dryRun ? "would be deleted" : "deleted")} under '{rootFolder}'", Subsystem.MediaManager);
        }

        // Checks whether a single directory is removable and deletes (or dry-run logs) it. Returns true if the folder was processed.
        private static bool TryCleanupFolder(string dir, bool dryRun)
        {
            if (!Directory.Exists(dir)) return false;

            bool removable;
            bool hasOnlyNfo;
            try
            {
                (removable, hasOnlyNfo) = IsRemovableFolder(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Skipped folder check for '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return false;
            }
            if (!removable) return false;

            string reason = hasOnlyNfo ? "nfo-only" : "empty";
            if (dryRun)
                LogManager.Instance.LogMessage($"Would delete {reason} folder: '{Path.GetFileName(dir)}'", LogLevel.Info, Subsystem.MediaManager);
            else
                DeleteFolder(dir, hasOnlyNfo);

            return true;
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
                LogManager.Instance.LogMessage($"Deleted {reason} folder: '{Path.GetFileName(dir)}'", LogLevel.Info, Subsystem.MediaManager);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Failed to delete folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            }
        }

        // Enumerates a source folder once and classifies root-level items into movies vs TV shows.
        // Files: video files are split by IsTvShow/IsVideoTvShowEpisode.
        // Directories: movieDirs excludes folders with episode/season patterns in their name.
        // tvDirs intentionally includes ALL directories because many TV show folders lack episode
        // patterns in their name (e.g. "Breaking Bad Season 1"). The TV processor will recurse
        // into each and only process files that match episode patterns.
        private static (string[] MovieFiles, string[] MovieDirs, string[] TvFiles, string[] TvDirs) ClassifySourceFolder(string folder)
        {
            var files = Directory.GetFiles(folder);
            var dirs  = Directory.GetDirectories(folder);

            var movieFiles = files.Where(f => FileNameParser.IsVideoFile(f) && !FileNameParser.IsTvShow(Path.GetFileName(f))).ToArray();
            var tvFiles    = files.Where(FileNameParser.IsVideoTvShowEpisode).ToArray();
            var movieDirs  = dirs.Where(d => !FileNameParser.IsTvShow(Path.GetFileName(d))).ToArray();
            var tvDirs     = dirs.ToArray();

            return (movieFiles, movieDirs, tvFiles, tvDirs);
        }

        private static string[] GetFolders(string key)
        {
            var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
            if (string.IsNullOrWhiteSpace(value))
                return [];
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Logs the import and performs the file operation, or just logs a dry-run message. No-ops when source and target are the same path or already imported.
        internal static void ImportFileWithLog(string sourcePath, string targetPath, string sourceFolder, bool dryRun, ImportMode importMode)
        {
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

            if (FileImporter.IsDuplicateFile(sourcePath, targetPath))
                return;

            string verb = dryRun ? "Would import" : "Importing";
            LogManager.Instance.LogMessage($"{verb} '{Path.GetFileName(sourcePath)}' -> {Path.GetRelativePath(sourceFolder, targetPath)}", LogLevel.Info, Subsystem.MediaManager);

            if (!dryRun)
                FileImporter.ImportFile(sourcePath, targetPath, importMode);
        }

        // Imports companion subtitle files that share the same base name as the video file
        internal static void ImportCompanionFiles(string sourceFolder, string videoPath, string targetVideoPath, bool dryRun, ImportMode importMode)
        {
            var videoDir   = Path.GetDirectoryName(videoPath);
            var videoBase  = Path.GetFileNameWithoutExtension(videoPath);
            var targetDir  = Path.GetDirectoryName(targetVideoPath);
            var targetBase = Path.GetFileNameWithoutExtension(targetVideoPath);

            if (string.IsNullOrEmpty(videoDir) || string.IsNullOrEmpty(targetDir)) return;

            string[] files;
            try { files = Directory.GetFiles(videoDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Skipped companion files in '{Path.GetFileName(videoDir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            foreach (var file in files.Where(FileNameParser.IsSubtitleFile))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)) continue;

                var suffix     = fileName[videoBase.Length..];
                var targetPath = Path.Combine(targetDir, targetBase + suffix);
                ImportFileWithLog(file, targetPath, sourceFolder, dryRun, importMode);
            }
        }

        internal static ImportMode ParseImportMode(string value) =>
            Enum.TryParse<ImportMode>(value, ignoreCase: true, out var mode) ? mode : ImportMode.Hardlink;
    }
}
