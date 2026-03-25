namespace qbPortWeaver
{
    /// <summary>
    /// Processes TV episode files, applying Plex naming conventions and importing them into the library:
    /// Library/Show Name (Year)/Season XX/Show Name (Year) - SXXEXX.ext  - with folder creation
    /// Library/Show Name (Year) - SXXEXX.ext                              - without folder creation
    /// Files are transferred via hardlink, copy, or move depending on the configured import mode.
    /// </summary>
    public sealed class TvShowProcessor
    {
        private const string MediaTypeTvShow  = "TV Show";

        private const int    MaxSubfolderDepth = 10; // TV Shows/Show (Year)/Season XX = depth 2; 10 is a safe ceiling

        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;
        private readonly string _libraryPath;
        private readonly ImportMode _importMode;

        // Caches show lookups (including confidence) to avoid redundant TMDB API calls across scan cycles.
        // Key includes year to distinguish same-titled shows (e.g. "Battlestar Galactica|1978" vs "Battlestar Galactica|2003").
        // ConcurrentDictionary: sync cycle and UI scan can overlap.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TvShowInfo? Info, bool IsConfident)> _showCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates a TV show processor that imports episodes into the specified library folder.</summary>
        /// <param name="tmdb">TMDB client for TV show metadata lookups.</param>
        /// <param name="dryRun">When true, logs what would happen without importing any files.</param>
        /// <param name="createFolders">When true, imports files into Plex-recommended season subfolders.</param>
        /// <param name="libraryPath">Target library folder for imported TV shows.</param>
        /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
        public TvShowProcessor(TmdbClient tmdb, bool dryRun, bool createFolders, string libraryPath, ImportMode importMode = ImportMode.Hardlink)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
            _libraryPath   = libraryPath;
            _importMode    = importMode;
        }

        /// <summary>
        /// Scans pre-classified TV episode files and directories and returns import proposals without modifying any files.
        /// Only items not yet present in the library are included.
        /// </summary>
        public async Task<List<MediaProposal>> ScanTvShowsAsync(string[] tvFiles, string[] tvDirs)
        {
            var proposals = new List<MediaProposal>();

            foreach (var file in tvFiles)
                await ScanEpisodeFileAsync(file, proposals).ConfigureAwait(false);

            foreach (var dir in tvDirs)
            {
                try
                {
                    await ScanTvShowFolderAsync(dir, proposals).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped TV folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            return proposals;
        }

        /// <summary>Processes pre-classified TV episode files and directories, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanTvShowsAsync"/> to preview and review those first.</summary>
        public async Task ProcessTvShowsAsync(string sourceFolder, string[] tvFiles, string[] tvDirs)
        {
            foreach (var file in tvFiles)
                await ProcessEpisodeFileAsync(sourceFolder, file).ConfigureAwait(false);

            foreach (var dir in tvDirs)
            {
                try
                {
                    await ProcessTvShowFolderAsync(sourceFolder, dir).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped TV folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }
        }

        private async Task ScanEpisodeFileAsync(string filePath, List<MediaProposal> proposals)
        {
            if (FileImporter.IsAlreadyInLibrary(filePath)) return;

            var fileName = Path.GetFileName(filePath);

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
            if (episodeInfo is null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.ScanEpisodeFileAsync: Skipped '{fileName}' - not a recognised episode", Subsystem.MediaManager);
                return;
            }

            var (showInfo, isConfident) = await GetOrLookupShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);
            if (showInfo is null)
            {
                proposals.Add(new MediaProposal(MediaTypeTvShow, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var proposedPath = BuildEpisodePath(filePath, showInfo, episodeInfo);

            if (FileImporter.IsDuplicateFile(filePath, proposedPath)) return;

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new MediaProposal(MediaTypeTvShow, filePath, proposedPath, isConfident));
        }

        private async Task ScanTvShowFolderAsync(string dirPath, List<MediaProposal> proposals, int depth = 0)
        {
            var files = MediaManagerService.GetFolderFiles(dirPath, depth, MaxSubfolderDepth, "TV");
            if (files is null) return;

            var episodeFiles = files.Where(FileNameParser.IsVideoTvShowEpisode).ToList();

            if (episodeFiles.Count > 0 && !episodeFiles.All(FileImporter.IsAlreadyInLibrary))
            {
                foreach (var file in episodeFiles)
                    await ScanEpisodeFileAsync(file, proposals).ConfigureAwait(false);
            }

            foreach (var subDir in Directory.GetDirectories(dirPath))
                await ScanTvShowFolderAsync(subDir, proposals, depth + 1).ConfigureAwait(false);
        }

        private async Task ProcessEpisodeFileAsync(string sourceFolder, string filePath)
        {
            if (FileImporter.IsAlreadyInLibrary(filePath)) return;

            var fileName = Path.GetFileName(filePath);

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);

            if (episodeInfo is null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.ProcessEpisodeFileAsync: Skipped '{fileName}' - not a recognised episode", Subsystem.MediaManager);
                return;
            }

            var (showInfo, isConfident) = await GetOrLookupShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);

            if (showInfo is null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetPath = BuildEpisodePath(filePath, showInfo, episodeInfo);
            MediaManagerService.ImportFileWithLog(filePath, targetPath, sourceFolder, _dryRun, _importMode);

            ImportCompanionFiles(sourceFolder, filePath, targetPath);
        }

        private async Task ProcessTvShowFolderAsync(string sourceFolder, string dirPath, int depth = 0)
        {
            var files = MediaManagerService.GetFolderFiles(dirPath, depth, MaxSubfolderDepth, "TV");
            if (files is null) return;

            var episodeFiles = files.Where(FileNameParser.IsVideoTvShowEpisode).ToList();

            if (episodeFiles.Count > 0 && !episodeFiles.All(FileImporter.IsAlreadyInLibrary))
            {
                foreach (var file in episodeFiles)
                    await ProcessEpisodeFileAsync(sourceFolder, file).ConfigureAwait(false);
            }

            foreach (var subDir in Directory.GetDirectories(dirPath))
                await ProcessTvShowFolderAsync(sourceFolder, subDir, depth + 1).ConfigureAwait(false);
        }

        // Builds the library target path for an episode file
        private string BuildEpisodePath(string filePath, TvShowInfo showInfo, TvShowEpisodeInfo episodeInfo)
        {
            var ext             = Path.GetExtension(filePath);
            var showFolderName  = FileNameParser.FormatPlexName(showInfo.Title, showInfo.Year);
            var episodeFileName = $"{showFolderName} - S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}{ext}";

            return _createFolders
                ? Path.Combine(_libraryPath, showFolderName, $"Season {episodeInfo.Season:D2}", episodeFileName)
                : Path.Combine(_libraryPath, episodeFileName);
        }

        private void ImportCompanionFiles(string sourceFolder, string videoPath, string targetVideoPath) =>
            MediaManagerService.ImportCompanionFiles(sourceFolder, videoPath, targetVideoPath, _dryRun, _importMode);

        // Returns a cached show lookup or performs a new TMDB search and caches the result
        private async Task<(TvShowInfo? Info, bool IsConfident)> GetOrLookupShowAsync(string showName, int? year)
        {
            var cacheKey = $"{showName}|{year}";
            if (_showCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var result = await LookupTvShowAsync(showName, year).ConfigureAwait(false);
            _showCache.TryAdd(cacheKey, result);
            return result;
        }

        private async Task<(TvShowInfo? Info, bool IsConfident)> LookupTvShowAsync(string title, int? year)
        {
            try
            {
                bool isConfident = true;

                var info = await _tmdb.SearchTvShowAsync(title, year).ConfigureAwait(false);

                // Retry without year: parsed year may be the season year rather than TMDB's first-air year
                if (info is null && year.HasValue)
                {
                    info = await _tmdb.SearchTvShowAsync(title).ConfigureAwait(false);
                    if (info is not null) isConfident = false;
                }

                if (info is null)
                {
                    LogManager.Instance.LogMessage($"No TMDB match found for show '{title}'", LogLevel.Warn, Subsystem.MediaManager);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"TvShowProcessor.LookupTvShowAsync: Matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]", Subsystem.MediaManager);
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"Failed to look up TMDB TV show: {ex.Message}", LogLevel.Error, Subsystem.MediaManager);
                return (null, false);
            }
        }
    }
}
