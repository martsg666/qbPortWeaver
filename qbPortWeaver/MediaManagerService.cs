using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace qbPortWeaver;

/// <summary>Orchestrates media file imports on each sync cycle when the Media Manager feature is enabled.</summary>
public static class MediaManagerService
{
    internal const int MaxSubfolderDepth = 10; // passed as EnumerationOptions.MaxRecursionDepth

    /// <summary>
    /// Runs one media import cycle, moving or linking files into the configured library.
    /// Returns immediately if the feature is disabled, the TMDB API key is not configured,
    /// or no library paths are set.
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled))
            return;

        var apiKey = RegistrySettingsManager.GetTmdbApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogManager.Instance.LogMessage("TMDB API key not configured - skipping import", LogLevel.Warn, Subsystem.MediaManager);
            return;
        }

        string moviesLibraryPath = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaMoviesLibraryPath);
        string tvShowsLibraryPath = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaTvShowsLibraryPath);

        if (string.IsNullOrWhiteSpace(moviesLibraryPath) && string.IsNullOrWhiteSpace(tvShowsLibraryPath))
        {
            LogManager.Instance.LogMessage("No library paths configured - skipping import", LogLevel.Warn, Subsystem.MediaManager);
            return;
        }

        bool dryRun = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
        bool createFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);
        bool deleteEmptyFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDeleteEmptyFolders);
        var importMode = ParseImportMode(RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaImportMode));

        var sourceFolders = GetFolders(RegistrySettingsManager.KeyMediaSourceFolders);
        var importSw = Stopwatch.StartNew();
        LogManager.Instance.LogMessage($"Import started (dryRun={dryRun}, createFolders={createFolders}, deleteEmptyFolders={deleteEmptyFolders}, importMode={importMode})", LogLevel.Info, Subsystem.MediaManager);
        if (LogManager.Instance.DebugMode)
            LogManager.Instance.LogDebug(
                $"MediaManagerService.ImportAsync: {RegistrySettingsManager.KeyMediaEnabled}=true, " +
                $"{RegistrySettingsManager.KeyTmdbApiKey}=***, " +
                $"{RegistrySettingsManager.KeyMediaSourceFolders}={string.Join(";", sourceFolders)}, " +
                $"{RegistrySettingsManager.KeyMediaMoviesLibraryPath}={moviesLibraryPath}, " +
                $"{RegistrySettingsManager.KeyMediaTvShowsLibraryPath}={tvShowsLibraryPath}, " +
                $"{RegistrySettingsManager.KeyMediaDryRun}={dryRun}, " +
                $"{RegistrySettingsManager.KeyMediaCreateFolders}={createFolders}, " +
                $"{RegistrySettingsManager.KeyMediaDeleteEmptyFolders}={deleteEmptyFolders}, " +
                $"{RegistrySettingsManager.KeyMediaImportMode}={importMode}",
                Subsystem.MediaManager);

        var prep = await PrepareClassifiedSourcesAsync(
            apiKey, sourceFolders, moviesLibraryPath, tvShowsLibraryPath, allowLibraryReuse: true, cancellationToken).ConfigureAwait(false);
        if (prep is null)
        {
            LogManager.Instance.LogMessage("Import skipped this cycle - library index unavailable", LogLevel.Info, Subsystem.MediaManager);
            return;
        }
        var (tmdb, classified, total) = prep.Value;

        var ctx = new ImportContext(tmdb, dryRun, createFolders, importMode, moviesLibraryPath, tvShowsLibraryPath);

        // Check cancellation once before starting parallel work. Putting ThrowIfCancellationRequested
        // inside the Select selector would orphan tasks already started in the partial enumeration
        // when the throw propagated out of Task.WhenAll's source enumerator.
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(classified.Select(c =>
            ProcessSourceFolderAsync(c.Folder, c.Items, ctx, cancellationToken))).ConfigureAwait(false);

        if (deleteEmptyFolders)
            CleanupSourceFolders(sourceFolders, dryRun, cancellationToken);

        TmdbCacheManager.Save();
        MediaImporter.SaveSourceCache();
        MediaImporter.SaveLibraryCache();
        importSw.Stop();
        LogManager.Instance.LogMessage($"Import completed: {total} file(s) in {importSw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
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
        var scanSw = Stopwatch.StartNew();
        LogManager.Instance.LogMessage($"Scan started (createFolders={createFolders})", LogLevel.Info, Subsystem.MediaManager);

        // UI-initiated scans always rebuild the library index (allowLibraryReuse: false) to ensure fresh results.
        var prep = await PrepareClassifiedSourcesAsync(
            apiKey, sourceFolders, moviesLibraryPath, tvShowsLibraryPath, allowLibraryReuse: false, cancellationToken).ConfigureAwait(false);
        if (prep is null)
        {
            LogManager.Instance.LogMessage("Scan aborted - library index unavailable", LogLevel.Warn, Subsystem.MediaManager);
            return [];
        }
        var (tmdb, classified, total) = prep.Value;

        int current = 0;
        void OnItemProcessed()
        {
            int c = Interlocked.Increment(ref current);
            progress?.Report((c, total));
        }

        var ctx = new ImportContext(tmdb, DryRun: true, createFolders, ImportMode.Hardlink, moviesLibraryPath, tvShowsLibraryPath);

        // Check cancellation once before starting parallel work (see ImportAsync for rationale).
        cancellationToken.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(classified.Select(c =>
            ScanSourceFolderAsync(c.Folder, c.Items, ctx, OnItemProcessed, cancellationToken))).ConfigureAwait(false);
        var proposals = results.SelectMany(r => r).ToList();

        TmdbCacheManager.Save();
        MediaImporter.SaveSourceCache();
        MediaImporter.SaveLibraryCache();
        scanSw.Stop();
        LogManager.Instance.LogMessage($"Scan completed: {proposals.Count} proposal(s) in {scanSw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
        return proposals;
    }

    // Scans a single source folder, returning proposals for both movies and TV shows.
    private static async Task<List<MediaProposal>> ScanSourceFolderAsync(
        string folder,
        (string[] MovieFiles, string[] TvShowFiles, FolderClassifiedEpisode[] FolderTvFiles) items,
        ImportContext ctx,
        Action? onItemProcessed,
        CancellationToken cancellationToken)
    {
        var proposals = new List<MediaProposal>();
        if (items.MovieFiles.Length == 0 && items.TvShowFiles.Length == 0 && items.FolderTvFiles.Length == 0)
        {
            LogManager.Instance.LogDebug($"MediaManagerService.ScanSourceFolderAsync: No new files in '{folder}'", Subsystem.MediaManager);
            return proposals;
        }
        LogManager.Instance.LogMessage($"Scanning source folder: '{folder}'", LogLevel.Info, Subsystem.MediaManager);

        if (!string.IsNullOrWhiteSpace(ctx.MoviesLibraryPath) && items.MovieFiles.Length > 0)
        {
            var movieProcessor = new MovieProcessor(ctx.Tmdb, ctx.DryRun, ctx.CreateFolders, ctx.MoviesLibraryPath, ctx.ImportMode);
            proposals.AddRange(await TryRunAsync(() => movieProcessor.ScanMoviesAsync(items.MovieFiles, onItemProcessed, cancellationToken), folder).ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(ctx.TvShowsLibraryPath) && (items.TvShowFiles.Length > 0 || items.FolderTvFiles.Length > 0))
        {
            var tvShowProcessor = new TvShowProcessor(ctx.Tmdb, ctx.DryRun, ctx.CreateFolders, ctx.TvShowsLibraryPath, ctx.ImportMode);
            if (items.TvShowFiles.Length > 0)
                proposals.AddRange(await TryRunAsync(() => tvShowProcessor.ScanTvShowsAsync(items.TvShowFiles, onItemProcessed, cancellationToken), folder).ConfigureAwait(false));
            if (items.FolderTvFiles.Length > 0)
                proposals.AddRange(await TryRunAsync(() => tvShowProcessor.ScanFolderClassifiedAsync(items.FolderTvFiles, onItemProcessed, cancellationToken), folder).ConfigureAwait(false));
        }

        LogManager.Instance.LogMessage($"Scanned source folder '{folder}': {proposals.Count} proposal(s)", LogLevel.Info, Subsystem.MediaManager);
        return proposals;
    }

    // Processes a single source folder, running both movie and TV show processors.
    private static async Task ProcessSourceFolderAsync(
        string folder,
        (string[] MovieFiles, string[] TvShowFiles, FolderClassifiedEpisode[] FolderTvFiles) items,
        ImportContext ctx,
        CancellationToken cancellationToken)
    {
        if (items.MovieFiles.Length == 0 && items.TvShowFiles.Length == 0 && items.FolderTvFiles.Length == 0)
        {
            LogManager.Instance.LogDebug($"MediaManagerService.ProcessSourceFolderAsync: No new files in '{folder}'", Subsystem.MediaManager);
            return;
        }
        LogManager.Instance.LogMessage($"Processing source folder: '{folder}'", LogLevel.Info, Subsystem.MediaManager);

        if (!string.IsNullOrWhiteSpace(ctx.MoviesLibraryPath) && items.MovieFiles.Length > 0)
        {
            var movieProcessor = new MovieProcessor(ctx.Tmdb, ctx.DryRun, ctx.CreateFolders, ctx.MoviesLibraryPath, ctx.ImportMode);
            await TryRunAsync(() => movieProcessor.ProcessMoviesAsync(items.MovieFiles, cancellationToken), folder).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(ctx.TvShowsLibraryPath) && (items.TvShowFiles.Length > 0 || items.FolderTvFiles.Length > 0))
        {
            var tvShowProcessor = new TvShowProcessor(ctx.Tmdb, ctx.DryRun, ctx.CreateFolders, ctx.TvShowsLibraryPath, ctx.ImportMode);
            if (items.TvShowFiles.Length > 0)
                await TryRunAsync(() => tvShowProcessor.ProcessTvShowsAsync(items.TvShowFiles, cancellationToken), folder).ConfigureAwait(false);
            if (items.FolderTvFiles.Length > 0)
                await TryRunAsync(() => tvShowProcessor.ProcessFolderClassifiedAsync(items.FolderTvFiles, cancellationToken), folder).ConfigureAwait(false);
        }

        int totalFiles = items.MovieFiles.Length + items.TvShowFiles.Length + items.FolderTvFiles.Length;
        LogManager.Instance.LogMessage($"Processed source folder '{folder}': {totalFiles} file(s)", LogLevel.Info, Subsystem.MediaManager);
    }

    // Runs folder cleanup for all configured source folders.
    private static void CleanupSourceFolders(string[] sourceFolders, bool dryRun, CancellationToken cancellationToken)
    {
        foreach (var folder in sourceFolders)
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
                    MediaImporter.AddFileToLibrary(proposal.OriginalPath, proposal.ProposedPath, importMode);
                }
                // Deliberately broad: this is the outermost per-item boundary of a user-initiated batch.
                // AddFileToLibrary already handles the expected IO/permission cases, so anything reaching
                // here is unexpected - log it at Error and keep going so one bad file cannot strand the
                // rest of the batch. (Narrowing this would abort the remaining imports on a surprise.)
                catch (Exception ex)
                {
                    LogManager.Instance.LogMessage(
                        $"Failed to import '{Path.GetFileName(proposal.OriginalPath)}': {ex.Message}",
                        LogLevel.Error, Subsystem.MediaManager);
                }
            }
        }, cancellationToken);

    // Deletes subdirectories of rootFolder that are empty or contain only .nfo files.
    // Walks bottom-up (longest path first) so nested empty folders are cleaned in a single pass.
    // Single-pass limitation: the directory list is snapshotted once before any deletions. Folders that
    // become empty only because the import moved their last file out this cycle are in the snapshot and
    // will be caught. Folders created or emptied by external processes after the snapshot are not seen
    // until the next cycle. The root folder itself is never deleted.
    internal static void CleanupEmptyFolders(string rootFolder, bool dryRun)
    {
        if (!MediaImporter.DirectoryExistsWithSmbRetry(rootFolder)) return;

        string[] directories;
        try
        {
            directories = MediaImporter.InvokeWithSmbRetry(rootFolder, () =>
                Directory.GetDirectories(rootFolder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = MaxSubfolderDepth,
                    IgnoreInaccessible = true,
                }));
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
        if (!MediaImporter.DirectoryExistsWithSmbRetry(dir)) return false;

        bool removable;
        bool hasOnlyNfo;
        string[] files;
        try
        {
            (removable, hasOnlyNfo, files) = IsRemovableFolder(dir);
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
            DeleteFolder(dir, files);

        return true;
    }

    // Returns (Removable, HasOnlyNfo, Files) when the folder is empty or contains only .nfo files and has no subdirectories.
    // Files is returned so the caller can pass it to DeleteFolder without a second GetFiles call.
    // Both GetFiles and GetDirectories wrapped in InvokeWithSmbRetry to ride out transient SMB blips.
    private static (bool Removable, bool HasOnlyNfo, string[] Files) IsRemovableFolder(string dir)
    {
        var files = MediaImporter.InvokeWithSmbRetry(dir, () => Directory.GetFiles(dir));
        if (MediaImporter.InvokeWithSmbRetry(dir, () => Directory.GetDirectories(dir)).Length > 0) return (false, false, files);
        if (files.Length == 0) return (true, false, files);
        bool allNfo = files.All(f => Path.GetExtension(f).Equals(".nfo", StringComparison.OrdinalIgnoreCase));
        return (allNfo, allNfo, files);
    }

    // Deletes .nfo files (if any) then removes the directory. Accepts the pre-enumerated files array to avoid a second GetFiles call.
    private static void DeleteFolder(string dir, string[] files)
    {
        try
        {
            foreach (var file in files)
                File.Delete(file);
            Directory.Delete(dir);
            string reason = files.Length > 0 ? "nfo-only" : "empty";
            LogManager.Instance.LogMessage($"Deleted {reason} folder: '{dir}'", LogLevel.Info, Subsystem.MediaManager);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogManager.Instance.LogMessage($"Failed to delete folder '{dir}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
        }
    }

    // Loads caches, validates source folders, then runs the library index build and source enumeration
    // concurrently (both are directory listings on different paths). Fingerprints candidates once the
    // library index is ready. allowLibraryReuse: true for background sync cycles (reuses index up to
    // FullRebuildIntervalCycles); false for UI-initiated scans (always rebuilds for fresh results).
    // Returns null when the library index could not be built (or was incomplete) so the caller can
    // skip the cycle - acting on a partial index would falsely report library files as missing and
    // risk creating duplicate imports.
    private static async Task<(TmdbClient Tmdb, List<(string Folder, (string[] MovieFiles, string[] TvShowFiles, FolderClassifiedEpisode[] FolderTvFiles) Items)> Classified, int Total)?>
        PrepareClassifiedSourcesAsync(
            string apiKey, string[] sourceFolders, string moviesLibraryPath, string tvShowsLibraryPath,
            bool allowLibraryReuse, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            MediaImporter.LoadSourceCache();
            TmdbCacheManager.Load();
            // Evict nulls cached in-memory during the previous cycle so transient API failures are retried this cycle.
            TmdbCacheManager.EvictNullMovies();
            TmdbCacheManager.EvictNullTvShows();
        }, cancellationToken).ConfigureAwait(false);

        var tmdb = new TmdbClient(apiKey);

        var validFolders = new List<string>();
        foreach (var f in sourceFolders)
        {
            if (!MediaImporter.DirectoryExistsWithSmbRetry(f))
            {
                LogManager.Instance.LogMessage($"Source folder not accessible: '{f}'", LogLevel.Warn, Subsystem.MediaManager);
                continue;
            }
            validFolders.Add(f);
        }

        var libraryTask = MediaImporter.BuildLibraryIndexAsync(moviesLibraryPath, tvShowsLibraryPath, allowReuse: allowLibraryReuse, cancellationToken);
        var enumerated = await EnumerateSourceFoldersAsync(validFolders, cancellationToken).ConfigureAwait(false);
        bool libraryReady = await libraryTask.ConfigureAwait(false);
        if (!libraryReady)
            return null;

        var classified = await ClassifySourceFoldersAsync(enumerated, cancellationToken).ConfigureAwait(false);
        int total = classified.Sum(c => c.Items.MovieFiles.Length + c.Items.TvShowFiles.Length + c.Items.FolderTvFiles.Length);

        return (tmdb, classified, total);
    }

    // Phase 1: enumerates each source folder and returns a candidate FileInfo list.
    // Uses directory metadata (Length, LastWriteTimeUtc) populated by EnumerateFiles - no extra stat per file.
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
                    return (Folder: f, Candidates: []);
                }
            }, cancellationToken)
        )).ConfigureAwait(false)).ToList();
    }

    // Enumerates video files in a source folder that are ready for import.
    // FileInfo metadata (Length, LastWriteTimeUtc) comes from the directory listing - no extra stat per file.
    // MaxRecursionDepth = MaxSubfolderDepth preserves the existing depth cap.
    // IgnoreInaccessible = true silently skips permission-denied folders.
    // Wrapped in InvokeWithSmbRetry to ride out transient ERROR_INVALID_FUNCTION responses
    // from SMB servers during connection renegotiation / oplock breaks.
    private static List<FileInfo> EnumerateSourceFolder(string folder) =>
        MediaImporter.InvokeWithSmbRetry(folder, () =>
            new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = MaxSubfolderDepth,
                    IgnoreInaccessible = true
                })
                .Where(fi => FileNameParser.IsVideoFile(fi.FullName) && MediaImporter.IsFileReadyForImport(fi))
                .ToList());

    // Phase 2: fingerprints candidates in parallel and classifies them into movies and TV episodes.
    // Requires BuildLibraryIndex to have completed before calling - IsAlreadyInLibrary uses the index.
    // Folders are processed sequentially so that a single Parallel.ForEach (degree MediaImporter.FingerprintParallelism)
    // is active at a time - processing folders concurrently would multiply the parallelism by the folder count.
    // sourceFpToPath is shared across all folders so source duplicates spanning multiple folders are detected.
    private static Task<List<(string Folder, (string[] MovieFiles, string[] TvShowFiles, FolderClassifiedEpisode[] FolderTvFiles) Items)>> ClassifySourceFoldersAsync(
        List<(string Folder, List<FileInfo> Candidates)> enumerated, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var classifySw = Stopwatch.StartNew();
            var classified = new List<(string Folder, (string[] MovieFiles, string[] TvShowFiles, FolderClassifiedEpisode[] FolderTvFiles) Items)>(enumerated.Count);
            var sourceFpToPath = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

            foreach (var e in enumerated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    classified.Add((e.Folder, ClassifyCandidates(e.Candidates, sourceFpToPath, cancellationToken)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped source folder '{e.Folder}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                    classified.Add((e.Folder, (MovieFiles: [], TvShowFiles: [], FolderTvFiles: [])));
                }
            }

            int movieTotal = classified.Sum(c => c.Items.MovieFiles.Length);
            int tvShowTotal = classified.Sum(c => c.Items.TvShowFiles.Length);
            int folderTvTotal = classified.Sum(c => c.Items.FolderTvFiles.Length);
            classifySw.Stop();
            var (sourceCached, sourceComputed) = MediaImporter.GetSourceCacheStats();
            LogManager.Instance.LogMessage(
                $"Source files classified: {movieTotal + tvShowTotal + folderTvTotal} ({movieTotal} movies, {tvShowTotal + folderTvTotal} TV episodes) in {classifySw.ElapsedMilliseconds}ms (cached={sourceCached}, computed={sourceComputed})",
                LogLevel.Info, Subsystem.MediaManager);

            return classified;
        }, cancellationToken);
    }

    // Fingerprints candidates in parallel, filters out files already in the library, and classifies
    // the remainder into movie and TV episode file paths in a single pass.
    // Each candidate requires a 128 KB read; reads are bounded by MediaImporter.FingerprintParallelism.
    // fpToPath maps each fingerprint to the first source path that produced it (shared across folders).
    // Files already in the library are silently skipped; additional source copies of the same content are warned.
    private static (string[] MovieFiles, string[] TvShowFiles, FolderClassifiedEpisode[] FolderTvFiles) ClassifyCandidates(
        List<FileInfo> candidates, ConcurrentDictionary<string, string> fpToPath, CancellationToken cancellationToken)
    {
        var movieFiles = new ConcurrentBag<string>();
        var tvShowFiles = new ConcurrentBag<string>();
        var folderTvFiles = new ConcurrentBag<FolderClassifiedEpisode>();

        Parallel.ForEach(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = MediaImporter.FingerprintParallelism, CancellationToken = cancellationToken },
            fi =>
            {
                string fp;
                try { fp = MediaImporter.GetOrComputeSourceFingerprint(fi); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogDebug($"MediaManagerService.ClassifyCandidates: Skipped '{fi.Name}': {ex.Message}", Subsystem.MediaManager);
                    return;
                }

                if (MediaImporter.IsAlreadyInLibrary(fp)) return;

                if (!fpToPath.TryAdd(fp, fi.FullName))
                {
                    fpToPath.TryGetValue(fp, out var firstPath);
                    LogManager.Instance.LogMessage(
                        $"Source duplicate: '{fi.FullName}' has same content as '{firstPath}'",
                        LogLevel.Warn, Subsystem.MediaManager);
                    return;
                }

                if (FileNameParser.IsTvShow(fi.Name))
                {
                    if (FileNameParser.IsVideoTvShowEpisode(fi.FullName))
                        tvShowFiles.Add(fi.FullName);
                    else
                        LogManager.Instance.LogDebug($"MediaManagerService.ClassifyCandidates: Skipped '{fi.Name}' - TV show without a recognized episode", Subsystem.MediaManager);
                }
                else if (TryClassifyAsFolderTv(fi, out var ep))
                {
                    folderTvFiles.Add(ep);
                }
                else
                {
                    movieFiles.Add(fi.FullName);
                }
            });

        return (movieFiles.ToArray(), tvShowFiles.ToArray(), folderTvFiles.ToArray());
    }

    // Recognises files that live under a season-indicator folder (e.g. "Season 01", "saison 1")
    // with a numeric episode prefix in the filename. Show name comes from the grandparent folder.
    // Layered fallback for libraries that encode the show/season/episode identity in directory
    // structure rather than the filename - the parser's SxxExx detection would otherwise misclassify these as movies.
    private static bool TryClassifyAsFolderTv(FileInfo fi, [NotNullWhen(true)] out FolderClassifiedEpisode? result)
    {
        result = null;

        int? episode = FileNameParser.ParseEpisodePrefix(Path.GetFileNameWithoutExtension(fi.Name));
        if (episode is null) return false;

        string? parentFolder = fi.Directory?.Name;
        if (parentFolder is null) return false;

        int? season = FileNameParser.ParseSeasonFromFolder(parentFolder);
        if (season is null) return false;

        string? grandparent = fi.Directory?.Parent?.Name;
        if (string.IsNullOrWhiteSpace(grandparent)) return false;

        result = new FolderClassifiedEpisode(fi.FullName, grandparent, season.Value, episode.Value);
        return true;
    }

    // Runs a processor operation and swallows IO/permission errors, logging the folder that was skipped.
    private static async Task TryRunAsync(Func<Task> process, string folder)
    {
        try { await process().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LogManager.Instance.LogMessage($"Skipped source folder '{folder}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager); }
    }

    // Scan variant: returns an empty list on IO/permission errors instead of propagating.
    private static async Task<List<T>> TryRunAsync<T>(Func<Task<List<T>>> scan, string folder)
    {
        try { return await scan().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LogManager.Instance.LogMessage($"Skipped source folder '{folder}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager); return []; }
    }

    private static string[] GetFolders(string key)
    {
        var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return value.Split(RegistrySettingsManager.ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Logs and performs the file transfer, or logs a dry-run message without touching files.
    /// No-ops when source and target are the same path, or the target already exists with the same fingerprint.
    /// Warns in both dry-run and live mode when the target exists with different content.</summary>
    internal static void ImportFile(string sourcePath, string targetPath, bool dryRun, ImportMode importMode)
    {
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

        // Check applies in both live and dry-run: skip files already correctly placed so we
        // don't falsely report "Would import" for content already present in the library.
        if (MediaImporter.DestinationMatchesSource(sourcePath, targetPath))
        {
            LogManager.Instance.LogDebug($"MediaManagerService.ImportFile: Skipped '{Path.GetFileName(sourcePath)}' - target already exists", Subsystem.MediaManager);
            return;
        }

        // Warn in both scan and import: two source files resolved to the same target name.
        // Checked before dryRun branch so the conflict is visible during Scan Now, not only on live import.
        if (File.Exists(targetPath))
        {
            MediaImporter.LogDestinationConflict(sourcePath, targetPath);
            return;
        }

        string verb = dryRun ? "Would import" : "Importing";
        // Log absolute target path: Path.GetRelativePath(sourceFolder, targetPath) produces
        // confusing "..\..\E:\..." style strings when source and target live on different volumes.
        LogManager.Instance.LogMessage($"{verb} '{Path.GetFileName(sourcePath)}' -> '{targetPath}'", LogLevel.Info, Subsystem.MediaManager);

        if (!dryRun)
            MediaImporter.AddFileToLibrary(sourcePath, targetPath, importMode);
    }

    // Imports companion subtitle files that share the same base name as the video file
    internal static void ImportCompanionFiles(string videoPath, string targetVideoPath, bool dryRun, ImportMode importMode)
    {
        var videoDir = Path.GetDirectoryName(videoPath);
        var videoBase = Path.GetFileNameWithoutExtension(videoPath);
        var targetDir = Path.GetDirectoryName(targetVideoPath);
        var targetBase = Path.GetFileNameWithoutExtension(targetVideoPath);

        if (string.IsNullOrEmpty(videoDir) || string.IsNullOrEmpty(targetDir)) return;

        string[] files;
        try { files = MediaImporter.InvokeWithSmbRetry(videoDir, () => Directory.GetFiles(videoDir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogManager.Instance.LogMessage($"Skipped companion files in '{videoDir}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            return;
        }

        foreach (var file in files.Where(FileNameParser.IsSubtitleFile))
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)) continue;
            // Require the character immediately after the base name to be '.' or end of string
            // so "Movie.mkv" does not claim "Movie 2.srt" as a companion.
            if (fileName.Length > videoBase.Length && fileName[videoBase.Length] != '.') continue;

            var suffix = fileName[videoBase.Length..];
            var targetPath = Path.Combine(targetDir, targetBase + suffix);
            ImportFile(file, targetPath, dryRun, importMode);
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
