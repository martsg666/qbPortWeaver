namespace qbPortWeaver
{
    /// <summary>
    /// Renames TV episode files to Plex naming convention:
    /// TV Shows/Show Name (Year)/Season XX/Show Name (Year) - SXXEXX.ext  -with folder creation
    /// TV Shows/Show Name (Year) - SXXEXX.ext                              -without folder creation
    /// </summary>
    public sealed class TvShowRenamer
    {
        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;

        // Caches show lookups to avoid redundant TMDB API calls within one scan cycle
        private readonly Dictionary<string, TvShowInfo> _showCache = new(StringComparer.OrdinalIgnoreCase);

        public TvShowRenamer(TmdbClient tmdb, bool dryRun, bool createFolders)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
        }

        /// <summary>
        /// Scans a TV shows folder and returns rename proposals without modifying any files.
        /// Only items whose current name differs from the Plex-compliant target are included.
        /// </summary>
        public async Task<List<RenameProposal>> ScanTvShowsFolderAsync(string tvShowsRoot)
        {
            var proposals = new List<RenameProposal>();
            if (!Directory.Exists(tvShowsRoot))
                return proposals;

            foreach (var file in Directory.GetFiles(tvShowsRoot).Where(FileNameParser.IsVideoEpisode))
                await ScanEpisodeFileAsync(tvShowsRoot, file, proposals).ConfigureAwait(false);

            foreach (var dir in Directory.GetDirectories(tvShowsRoot))
            {
                await ScanSubfolderAsync(tvShowsRoot, dir, proposals).ConfigureAwait(false);
            }

            return proposals;
        }

        public async Task ProcessTvShowsFolderAsync(string tvShowsRoot)
        {
            if (!Directory.Exists(tvShowsRoot))
            {
                LogManager.Instance.LogMessage($"[MediaManager] TV folder not found: {tvShowsRoot}", LogLevel.Error);
                return;
            }

            LogManager.Instance.LogMessage($"[MediaManager] Scanning TV folder: {tvShowsRoot}", LogLevel.Info);

            foreach (var file in Directory.GetFiles(tvShowsRoot).Where(FileNameParser.IsVideoEpisode))
                await ProcessEpisodeFileAsync(tvShowsRoot, file).ConfigureAwait(false);

            foreach (var dir in Directory.GetDirectories(tvShowsRoot))
            {
                await ProcessSubfolderAsync(tvShowsRoot, dir).ConfigureAwait(false);
            }
        }

        private async Task ScanSubfolderAsync(string tvShowsRoot, string dirPath, List<RenameProposal> proposals)
        {
            foreach (var file in Directory.GetFiles(dirPath).Where(FileNameParser.IsVideoEpisode))
                await ScanEpisodeFileAsync(tvShowsRoot, file, proposals).ConfigureAwait(false);

            foreach (var subDir in Directory.GetDirectories(dirPath))
            {
                await ScanSubfolderAsync(tvShowsRoot, subDir, proposals).ConfigureAwait(false);
            }
        }

        private async Task ScanEpisodeFileAsync(string tvShowsRoot, string filePath, List<RenameProposal> proposals)
        {
            var fileName = Path.GetFileName(filePath);
            if (!_createFolders && FileNameParser.IsPlexFormatted(fileName)) return;

            var episodeInfo = FileNameParser.ParseTvEpisode(fileName);
            if (episodeInfo == null) return;

            bool isConfident = true;
            if (!_showCache.TryGetValue(episodeInfo.ShowName, out var showInfo))
            {
                (showInfo, isConfident) = await LookupTvShowAsync(episodeInfo.ShowName).ConfigureAwait(false);
                if (showInfo != null)
                    _showCache[episodeInfo.ShowName] = showInfo;
            }
            if (showInfo == null)
            {
                proposals.Add(new RenameProposal("TV", filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var ext             = Path.GetExtension(filePath);
            var showFolderName  = FileNameParser.SanitizeFileName($"{showInfo.Title} ({showInfo.Year})");
            var episodeFileName = $"{showFolderName} - S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}{ext}";

            string proposedPath = _createFolders
                ? Path.Combine(tvShowsRoot, showFolderName, $"Season {episodeInfo.Season:D2}", episodeFileName)
                : Path.Combine(tvShowsRoot, episodeFileName);

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new RenameProposal("TV", filePath, proposedPath, isConfident));
        }

        private async Task ProcessSubfolderAsync(string tvShowsRoot, string dirPath)
        {
            foreach (var file in Directory.GetFiles(dirPath).Where(FileNameParser.IsVideoEpisode))
                await ProcessEpisodeFileAsync(tvShowsRoot, file).ConfigureAwait(false);

            foreach (var subDir in Directory.GetDirectories(dirPath))
            {
                await ProcessSubfolderAsync(tvShowsRoot, subDir).ConfigureAwait(false);
            }
        }

        private async Task ProcessEpisodeFileAsync(string tvShowsRoot, string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            // Skip TMDB lookup entirely if the file is already Plex-formatted (and we're not reorganising into folders)
            if (!_createFolders && FileNameParser.IsPlexFormatted(fileName))
            {
                LogManager.Instance.LogDebug($"[MediaManager] TvShowRenamer.ProcessEpisodeFile: already Plex-formatted, skipping '{fileName}'");
                return;
            }

            var episodeInfo = FileNameParser.ParseTvEpisode(fileName);

            if (episodeInfo == null)
            {
                LogManager.Instance.LogMessage($"[MediaManager] Skipped '{fileName}' - not a recognised episode", LogLevel.Info);
                return;
            }

            LogManager.Instance.LogMessage($"[MediaManager] Processing '{fileName}'", LogLevel.Info);
            LogManager.Instance.LogDebug($"[MediaManager] TvShowRenamer.ProcessEpisodeFile: parsed show='{episodeInfo.ShowName}' S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}");

            bool isConfident = true;
            if (!_showCache.TryGetValue(episodeInfo.ShowName, out var showInfo))
            {
                (showInfo, isConfident) = await LookupTvShowAsync(episodeInfo.ShowName).ConfigureAwait(false);
                if (showInfo != null)
                    _showCache[episodeInfo.ShowName] = showInfo;
            }

            if (showInfo == null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"[MediaManager] Skipping '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn);
                return;
            }

            var ext             = Path.GetExtension(filePath);
            var showFolderName  = FileNameParser.SanitizeFileName($"{showInfo.Title} ({showInfo.Year})");
            var episodeFileName = $"{showFolderName} - S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}{ext}";

            MoveEpisodeFile(tvShowsRoot, filePath, episodeInfo, showFolderName, episodeFileName);
        }

        // Moves or renames the episode file to its Plex-compliant target path, creating season folders if needed.
        private void MoveEpisodeFile(string tvShowsRoot, string filePath, TvEpisodeInfo episodeInfo, string showFolderName, string episodeFileName)
        {
            if (_createFolders)
            {
                var seasonFolder = $"Season {episodeInfo.Season:D2}";
                var targetDir    = Path.Combine(tvShowsRoot, showFolderName, seasonFolder);
                var targetPath   = Path.Combine(targetDir, episodeFileName);

                LogManager.Instance.LogMessage($"[MediaManager] Renaming to {showFolderName}/{seasonFolder}/{episodeFileName}", LogLevel.Info);

                if (!_dryRun && !string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(targetDir);
                    File.Move(filePath, targetPath);
                    LogManager.Instance.LogDebug("[MediaManager] TvShowRenamer.ProcessEpisodeFile: renamed");
                }
            }
            else
            {
                var targetPath = Path.Combine(tvShowsRoot, episodeFileName);

                LogManager.Instance.LogMessage($"[MediaManager] Renaming to {episodeFileName}", LogLevel.Info);

                if (!_dryRun && !string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(filePath, targetPath);
                    LogManager.Instance.LogDebug("[MediaManager] TvShowRenamer.ProcessEpisodeFile: renamed");
                }
            }
        }

        private async Task<(TvShowInfo? Info, bool IsConfident)> LookupTvShowAsync(string name)
        {
            try
            {
                var info = await _tmdb.SearchTvShowAsync(name).ConfigureAwait(false);
                if (info == null)
                {
                    LogManager.Instance.LogMessage($"[MediaManager] No TMDB match found for show '{name}'", LogLevel.Warn);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"[MediaManager] TvShowRenamer.LookupTvShow: matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]");
                return (info, true);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"[MediaManager] TMDB TV show lookup failed: {ex.Message}", LogLevel.Error);
                return (null, false);
            }
        }
    }
}
