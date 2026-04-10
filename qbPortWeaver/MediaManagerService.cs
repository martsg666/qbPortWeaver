using System.Collections.Concurrent;

namespace qbPortWeaver
{
    /// <summary>Orchestrates media file imports on each sync cycle when the Media Manager feature is enabled.</summary>
    public static class MediaManagerService
    {
        internal const int MaxSubfolderDepth = 10; // passed as EnumerationOptions.MaxRecursionDepth

        /// <summary>
        /// Runs one media import cycle. Returns immediately if the feature is disabled, the TMDB API key is not configured,
        /// or no library paths are set.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public static async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled))
                return;

            var apiKey = RegistrySettingsManager.GetEncryptedValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogManager.Instance.LogMessage("TMDB API key not configured - skipping scan", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            string moviesLibraryPath  = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaMoviesLibraryPath);
            string tvShowsLibraryPath = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaTvShowsLibraryPath);

            if (string.IsNullOrWhiteSpace(moviesLibraryPath) && string.IsNullOrWhiteSpace(tvShowsLibraryPath))
            {
                LogManager.Instance.LogMessage("No library paths configured - skipping scan", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            bool dryRun             = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
            bool createFolders      = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);
            bool deleteEmptyFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders);
            var importMode = ParseImportMode(RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaImportMode));

            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            LogManager.Instance.LogMessage($"Scan started (mode=import, dryRun={dryRun}, createFolders={createFolders}, deleteEmptyFolders={deleteEmptyFolders}, importMode={importMode})", LogLevel.Info, Subsystem.MediaManager);
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

            // Fast: load source and TMDB caches into memory (in-memory no-op after first cycle)
            await Task.Run(() =>
            {
                MediaImporter.LoadSourceCache();
                TmdbCacheManager.Load();
                TmdbCacheManager.EvictNullMovies();
                TmdbCacheManager.EvictNullShows();
            }, cancellationToken).ConfigureAwait(false);

            var tmdb = new TmdbClient(apiKey);

            // Enumerate valid source folders
            var validFolders = new List<string>();
            foreach (var f in GetFolders(RegistrySettingsManager.KeyMediaSourceFolders))
            {
                if (!Directory.Exists(f)) { LogManager.Instance.LogMessage($"Source folder not found: '{f}'", LogLevel.Warn, Subsystem.MediaManager); continue; }
                validFolders.Add(f);
            }

            // Overlap: library index enumeration and source folder enumeration are both directory listings on
            // different paths and can run concurrently. Phase 2 fingerprinting waits for both to complete
            // since it requires the library index to be ready.
            int syncInterval = RegistrySettingsManager.GetInt(RegistrySettingsManager.SectionGeneral, RegistrySettingsManager.KeyUpdateIntervalSeconds);
            var libraryTask = Task.Run(() => MediaImporter.BuildLibraryIndex(moviesLibraryPath, tvShowsLibraryPath, syncInterval, cancellationToken), cancellationToken);
            var enumerated  = await EnumerateSourceFoldersAsync(validFolders, cancellationToken).ConfigureAwait(false);
            await libraryTask.ConfigureAwait(false);
            var classified  = await FingerprintSourceFoldersAsync(enumerated, cancellationToken).ConfigureAwait(false);
            int total       = classified.Sum(c => c.Items.MovieFiles.Length + c.Items.TvFiles.Length);

            var ctx = new ImportContext(tmdb, dryRun, createFolders, importMode, moviesLibraryPath, tvShowsLibraryPath);

            await Task.WhenAll(classified.Select(c =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ProcessSourceFolderAsync(c.Folder, c.Items, ctx);
            })).ConfigureAwait(false);

            if (deleteEmptyFolders && total > 0)
                CleanupSourceFolders(dryRun, cancellationToken);

            TmdbCacheManager.Save();
            MediaImporter.SaveSourceCache();
            MediaImporter.SaveLibraryCache();
            scanSw.Stop();
            LogManager.Instance.LogMessage($"Scan completed: {total} candidate file(s) in {scanSw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
        }

        // Processes a single source folder, running both movie and TV show processors.
        private static async Task ProcessSourceFolderAsync(
            string folder,
            (string[] MovieFiles, string[] TvFiles) items,
            ImportContext ctx)
        {
            if (items.MovieFiles.Length == 0 && items.TvFiles.Length == 0)
            {
                LogManager.Instance.LogDebug($"MediaManagerService.ProcessSourceFolderAsync: No new files in '{folder}'", Subsystem.MediaManager);
                return;
            }
            LogManager.Instance.LogMessage($"Processing source folder: '{folder}'", LogLevel.Info, Subsystem.MediaManager);

            if (!string.IsNullOrWhiteSpace(ctx.MoviesLibraryPath) && items.MovieFiles.Length > 0)
            {
                var movieProcessor = new MovieProcessor(ctx.Tmdb, ctx.DryRun, ctx.CreateFolders, ctx.MoviesLibraryPath, ctx.ImportMode);
                try
                {
                    await movieProcessor.ProcessMoviesAsync(folder, items.MovieFiles).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (movies): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            if (!string.IsNullOrWhiteSpace(ctx.TvShowsLibraryPath) && items.TvFiles.Length > 0)
            {
                var tvShowProcessor = new TvShowProcessor(ctx.Tmdb, ctx.DryRun, ctx.CreateFolders, ctx.TvShowsLibraryPath, ctx.ImportMode);
                try
                {
                    await tvShowProcessor.ProcessTvShowsAsync(folder, items.TvFiles).ConfigureAwait(false);
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
        /// <param name="sourceFolders">Source folders to scan for both movies and TV shows.</param>
        /// <param name="moviesLibraryPath">Library folder for movies. Empty to skip movie processing.</param>
        /// <param name="tvShowsLibraryPath">Library folder for TV shows. Empty to skip TV show processing.</param>
        /// <param name="progress">Optional progress sink; reports current and total item counts as each item is processed.</param>
        /// <param name="cancellationToken">Token to cancel the scan between folders.</param>
        public static async Task<List<MediaProposal>> ScanAsync(string apiKey, bool createFolders, string[] sourceFolders,
            string moviesLibraryPath, string tvShowsLibraryPath,
            IProgress<(int Current, int Total)>? progress = null, CancellationToken cancellationToken = default)
        {
            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            LogManager.Instance.LogMessage($"Scan started (mode=preview, createFolders={createFolders})", LogLevel.Info, Subsystem.MediaManager);

            // Fast: load source and TMDB caches into memory (in-memory no-op after first cycle)
            await Task.Run(() =>
            {
                MediaImporter.LoadSourceCache();
                TmdbCacheManager.Load();
                TmdbCacheManager.EvictNullMovies();
                TmdbCacheManager.EvictNullShows();
            }, cancellationToken).ConfigureAwait(false);

            var tmdb = new TmdbClient(apiKey);

            // Enumerate valid source folders
            var validFolders = new List<string>();
            foreach (var f in sourceFolders)
            {
                if (!Directory.Exists(f)) { LogManager.Instance.LogMessage($"Source folder not found: '{f}'", LogLevel.Warn, Subsystem.MediaManager); continue; }
                validFolders.Add(f);
            }

            // Overlap: library index enumeration and source folder enumeration are both directory listings on
            // different paths and can run concurrently. Phase 2 fingerprinting waits for both to complete
            // since it requires the library index to be ready.
            // UI-initiated scans always rebuild the library index (freshnessSeconds: 0) to ensure fresh results.
            var libraryTask = Task.Run(() => MediaImporter.BuildLibraryIndex(moviesLibraryPath, tvShowsLibraryPath, cancellationToken: cancellationToken), cancellationToken);
            var enumerated  = await EnumerateSourceFoldersAsync(validFolders, cancellationToken).ConfigureAwait(false);
            await libraryTask.ConfigureAwait(false);
            var classified  = await FingerprintSourceFoldersAsync(enumerated, cancellationToken).ConfigureAwait(false);
            int total       = classified.Sum(c => c.Items.MovieFiles.Length + c.Items.TvFiles.Length);

            int current = 0;
            void OnItemProcessed()
            {
                int c = Interlocked.Increment(ref current);
                progress?.Report((c, total));
            }

            var ctx = new ImportContext(tmdb, DryRun: true, createFolders, ImportMode.Hardlink, moviesLibraryPath, tvShowsLibraryPath);

            var results = await Task.WhenAll(classified.Select(c =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ScanSourceFolderAsync(c.Folder, c.Items, ctx, OnItemProcessed);
            })).ConfigureAwait(false);
            var proposals = results.SelectMany(r => r).ToList();

            TmdbCacheManager.Save();
            MediaImporter.SaveSourceCache();
            MediaImporter.SaveLibraryCache();
            scanSw.Stop();
            LogManager.Instance.LogMessage($"Scan completed: {proposals.Count} proposal(s) found in {scanSw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
            return proposals;
        }

        // Scans a single source folder, returning proposals for both movies and TV shows.
        private static async Task<List<MediaProposal>> ScanSourceFolderAsync(
            string folder,
            (string[] MovieFiles, string[] TvFiles) items,
            ImportContext ctx,
            Action? onItemProcessed = null)
        {
            var proposals = new List<MediaProposal>();
            if (items.MovieFiles.Length == 0 && items.TvFiles.Length == 0)
            {
                LogManager.Instance.LogDebug($"MediaManagerService.ScanSourceFolderAsync: No new files in '{folder}'", Subsystem.MediaManager);
                return proposals;
            }
            LogManager.Instance.LogMessage($"Scanning source folder: '{folder}'", LogLevel.Info, Subsystem.MediaManager);

            // Scan is always preview-only; actual import is applied separately via ApplyProposalsAsync
            if (!string.IsNullOrWhiteSpace(ctx.MoviesLibraryPath) && items.MovieFiles.Length > 0)
            {
                var movieProcessor = new MovieProcessor(ctx.Tmdb, dryRun: true, ctx.CreateFolders, ctx.MoviesLibraryPath);
                try
                {
                    proposals.AddRange(await movieProcessor.ScanMoviesAsync(items.MovieFiles, onItemProcessed).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (movies): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            if (!string.IsNullOrWhiteSpace(ctx.TvShowsLibraryPath) && items.TvFiles.Length > 0)
            {
                var tvShowProcessor = new TvShowProcessor(ctx.Tmdb, dryRun: true, ctx.CreateFolders, ctx.TvShowsLibraryPath);
                try
                {
                    proposals.AddRange(await tvShowProcessor.ScanTvShowsAsync(items.TvFiles, onItemProcessed).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped folder '{folder}' (TV shows): {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            LogManager.Instance.LogMessage($"Scanned source folder '{folder}': {proposals.Count} proposal(s)", LogLevel.Info, Subsystem.MediaManager);
            return proposals;
        }

        /// <summary>
        /// Applies a set of import proposals, transferring files from source folders into the library.
        /// Proposals are typically produced by <see cref="ScanAsync"/> but may have been modified by the user before calling this method.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="proposals">The import proposals to apply. Each proposal's <see cref="MediaProposal.ProposedPath"/> is the library target.</param>
        /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
        /// <param name="progress">Optional progress sink; reports current item count, total, and filename as each file is imported.</param>
        /// <param name="cancellationToken">Token to cancel the operation between imports.</param>
        public static Task ApplyProposalsAsync(IEnumerable<MediaProposal> proposals, ImportMode importMode,
            IProgress<(int Current, int Total, string FileName)>? progress = null, CancellationToken cancellationToken = default)
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
                        $"Importing '{Path.GetFileName(proposal.OriginalPath)}' -> '{proposal.ProposedPath}'",
                        LogLevel.Info, Subsystem.MediaManager);
                    try
                    {
                        MediaImporter.ImportFile(proposal.OriginalPath, proposal.ProposedPath, importMode);
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

            int deleted = directories.OrderByDescending(d => d.Length)
                .Count(dir => TryCleanupFolder(dir, dryRun));

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
                LogManager.Instance.LogMessage($"Skipped folder check for '{dir}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return false;
            }
            if (!removable) return false;

            string reason = hasOnlyNfo ? "nfo-only" : "empty";
            if (dryRun)
                LogManager.Instance.LogMessage($"Would delete {reason} folder: '{dir}'", LogLevel.Info, Subsystem.MediaManager);
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
                LogManager.Instance.LogMessage($"Deleted {reason} folder: '{dir}'", LogLevel.Info, Subsystem.MediaManager);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Failed to delete folder '{dir}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            }
        }

        // Phase 1: enumerates each source folder and returns a candidate FileInfo list.
        // Uses directory metadata (Length, LastWriteTimeUtc) populated by EnumerateFiles — no extra stat per file.
        // Safe to run concurrently with BuildLibraryIndex since it does not call IsAlreadyInLibrary.
        private static async Task<List<(string Folder, List<FileInfo> Candidates)>> EnumerateSourceFoldersAsync(
            List<string> validFolders, CancellationToken cancellationToken)
        {
            LogManager.Instance.LogMessage($"Enumerating source files across {validFolders.Count} folder(s)", LogLevel.Info, Subsystem.MediaManager);
            return (await Task.WhenAll(validFolders.Select(f =>
                Task.Run(() =>
                {
                    LogManager.Instance.LogMessage($"Enumerating source folder: '{f}'", LogLevel.Info, Subsystem.MediaManager);
                    try
                    {
                        return (Folder: f, Candidates: EnumerateSourceFolder(f));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogManager.Instance.LogMessage($"Skipped source folder '{f}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                        return (Folder: f, Candidates: new List<FileInfo>());
                    }
                }, cancellationToken)
            )).ConfigureAwait(false)).ToList();
        }

        // Enumerates video files in a source folder that are ready for import.
        // FileInfo metadata (Length, LastWriteTimeUtc) comes from the directory listing — no extra stat per file.
        // MaxRecursionDepth = MaxSubfolderDepth preserves the existing depth cap.
        // IgnoreInaccessible = true silently skips permission-denied folders.
        private static List<FileInfo> EnumerateSourceFolder(string folder)
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth     = MaxSubfolderDepth,
                    IgnoreInaccessible    = true
                })
                .Where(fi => FileNameParser.IsVideoFile(fi.FullName) && MediaImporter.IsFileReadyForImport(fi))
                .ToList();
        }

        // Phase 2: fingerprints candidates in parallel and classifies them into movies and TV episodes.
        // Requires BuildLibraryIndex to have completed before calling — IsAlreadyInLibrary uses the index.
        // Folders are processed sequentially so that a single Parallel.ForEach (degree MediaImporter.FingerprintParallelism)
        // is active at a time — processing folders concurrently would multiply the parallelism by the folder count.
        private static Task<List<(string Folder, (string[] MovieFiles, string[] TvFiles) Items)>> FingerprintSourceFoldersAsync(
            List<(string Folder, List<FileInfo> Candidates)> enumerated, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var classifySw = System.Diagnostics.Stopwatch.StartNew();
                var classified = new List<(string Folder, (string[] MovieFiles, string[] TvFiles) Items)>(enumerated.Count);

                foreach (var e in enumerated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        classified.Add((e.Folder, FingerprintCandidates(e.Candidates, cancellationToken)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogManager.Instance.LogMessage($"Skipped source folder '{e.Folder}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                        classified.Add((e.Folder, (MovieFiles: Array.Empty<string>(), TvFiles: Array.Empty<string>())));
                    }
                }

                int movieTotal = classified.Sum(c => c.Items.MovieFiles.Length);
                int tvTotal    = classified.Sum(c => c.Items.TvFiles.Length);
                classifySw.Stop();
                var (sourceCached, sourceComputed) = MediaImporter.GetSourceCacheStats();
                LogManager.Instance.LogMessage(
                    $"Source files classified: {movieTotal + tvTotal} ({movieTotal} movies, {tvTotal} TV episodes) in {classifySw.ElapsedMilliseconds}ms (cached={sourceCached}, computed={sourceComputed})",
                    LogLevel.Info, Subsystem.MediaManager);

                return classified;
            }, cancellationToken);
        }

        // Fingerprints candidates in parallel, filters out files already in the library, and classifies
        // the remainder into movie and TV episode file paths in a single pass.
        // Each candidate requires a 128 KB read to compute its fingerprint; reads are bounded by MediaImporter.FingerprintParallelism.
        private static (string[] MovieFiles, string[] TvFiles) FingerprintCandidates(List<FileInfo> candidates, CancellationToken cancellationToken)
        {
            var movies  = new ConcurrentBag<string>();
            var tvShows = new ConcurrentBag<string>();

            Parallel.ForEach(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = MediaImporter.FingerprintParallelism, CancellationToken = cancellationToken },
                fi =>
                {
                    if (MediaImporter.IsAlreadyInLibrary(fi)) return;
                    if (!FileNameParser.IsTvShow(fi.Name))
                        movies.Add(fi.FullName);
                    else if (FileNameParser.IsVideoTvShowEpisode(fi.FullName))
                        tvShows.Add(fi.FullName);
                });

            return (movies.ToArray(), tvShows.ToArray());
        }

        private static string[] GetFolders(string key)
        {
            var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
            if (string.IsNullOrWhiteSpace(value))
                return [];
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Logs the import and performs the file operation, or just logs a dry-run message. No-ops when source and target are the same path or the target file already exists at the destination.
        internal static void ImportFileWithLog(string sourcePath, string targetPath, string sourceFolder, bool dryRun, ImportMode importMode)
        {
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

            if (MediaImporter.IsDuplicateFile(sourcePath, targetPath))
            {
                LogManager.Instance.LogDebug($"MediaManagerService.ImportFileWithLog: Target already exists '{Path.GetFileName(sourcePath)}'", Subsystem.MediaManager);
                return;
            }

            string verb = dryRun ? "Would import" : "Importing";
            LogManager.Instance.LogMessage($"{verb} '{Path.GetFileName(sourcePath)}' -> {Path.GetRelativePath(sourceFolder, targetPath)}", LogLevel.Info, Subsystem.MediaManager);

            if (!dryRun)
                MediaImporter.ImportFile(sourcePath, targetPath, importMode);
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
                LogManager.Instance.LogMessage($"Skipped companion files in '{videoDir}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
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

        /// <summary>Clears all media manager caches from memory and disk, forcing a full re-index and TMDB re-lookup on the next scan.</summary>
        public static void ClearAllCaches()
        {
            MediaImporter.ClearAllCaches();
            TmdbCacheManager.Clear();
        }

        internal static ImportMode ParseImportMode(string value) =>
            Enum.TryParse<ImportMode>(value, ignoreCase: true, out var mode) ? mode : ImportMode.Hardlink;

        // Bundles shared import settings to avoid exceeding the parameter limit on private helpers.
        private sealed record ImportContext(
            TmdbClient Tmdb,
            bool DryRun,
            bool CreateFolders,
            ImportMode ImportMode,
            string MoviesLibraryPath,
            string TvShowsLibraryPath);
    }
}
