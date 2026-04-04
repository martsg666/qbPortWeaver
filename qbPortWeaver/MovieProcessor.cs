namespace qbPortWeaver
{
    /// <summary>
    /// Processes movie files and folders, applying Plex naming conventions and importing them into the library:
    /// Library/Title (Year)/Title (Year).ext  - with folder creation
    /// Library/Title (Year).ext               - without folder creation
    /// Files are transferred via hardlink, copy, or move depending on the configured import mode.
    /// </summary>
    public sealed class MovieProcessor
    {
        private const string MediaTypeMovie = "Movie";

        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;
        private readonly string _libraryPath;
        private readonly ImportMode _importMode;

        /// <summary>Creates a movie processor that imports files into the specified library folder.</summary>
        /// <param name="tmdb">TMDB client for movie metadata lookups.</param>
        /// <param name="dryRun">When true, logs what would happen without importing any files.</param>
        /// <param name="createFolders">When true, imports files into Plex-recommended subfolders.</param>
        /// <param name="libraryPath">Target library folder for imported movies.</param>
        /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
        public MovieProcessor(TmdbClient tmdb, bool dryRun, bool createFolders, string libraryPath, ImportMode importMode = ImportMode.Hardlink)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
            _libraryPath   = libraryPath;
            _importMode    = importMode;
        }

        /// <summary>
        /// Scans pre-classified movie files and returns import proposals without modifying any files.
        /// Only items not yet present in the library are included.
        /// </summary>
        public async Task<List<MediaProposal>> ScanMoviesAsync(string[] movieFiles, Action? onItemProcessed = null)
        {
            var proposals = new List<MediaProposal>();

            var (selfDescribing, folderDependent) = ClassifyVideoFiles(movieFiles);

            foreach (var (file, title, year) in selfDescribing)
            {
                await AddMovieScanProposalAsync(file, title, year, proposals).ConfigureAwait(false);
                onItemProcessed?.Invoke();
            }

            foreach (var group in folderDependent.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
            {
                await ScanFolderDependentFilesAsync(group.Key, group.ToList(), proposals).ConfigureAwait(false);
                foreach (var _ in group) onItemProcessed?.Invoke();
            }

            return proposals;
        }

        /// <summary>Processes pre-classified movie files, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanMoviesAsync"/> to preview and review those first.</summary>
        public async Task ProcessMoviesAsync(string sourceFolder, string[] movieFiles)
        {
            var (selfDescribing, folderDependent) = ClassifyVideoFiles(movieFiles);

            foreach (var (file, title, year) in selfDescribing)
                await ProcessSingleMovieFileAsync(sourceFolder, file, title, year).ConfigureAwait(false);

            foreach (var group in folderDependent.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
                await ProcessFolderDependentFilesAsync(sourceFolder, group.Key, group.ToList()).ConfigureAwait(false);
        }

        // Splits video files into self-describing (filename has a parseable movie title) and folder-dependent
        // (e.g. cd1.mkv, part2.mkv - no useful title, need the parent folder name for TMDB lookup).
        // Self-describing files get individual TMDB lookups; folder-dependent files share a single
        // folder-name-based lookup (multi-part scenario). TV episodes don't need this split because
        // their S##E## pattern always makes them individually identifiable.
        private static (List<(string File, string Title, int? Year)> SelfDescribing, List<string> FolderDependent)
            ClassifyVideoFiles(string[] videoFiles)
        {
            var selfDescribing  = new List<(string File, string Title, int? Year)>();
            var folderDependent = new List<string>();

            foreach (var file in videoFiles)
            {
                var (title, year) = FileNameParser.ParseMovie(Path.GetFileName(file));
                if (!string.IsNullOrWhiteSpace(title))
                    selfDescribing.Add((file, title, year));
                else
                    folderDependent.Add(file);
            }

            return (selfDescribing, folderDependent);
        }

        // Proposal builder for self-describing movie files
        private async Task AddMovieScanProposalAsync(string filePath, string title, int? year, List<MediaProposal> proposals)
        {
            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info is null)
            {
                proposals.Add(new MediaProposal(MediaTypeMovie, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var proposedPath = BuildStandaloneMoviePath(filePath, info);

            if (FileImporter.IsDuplicateFile(filePath, proposedPath)) return;

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new MediaProposal(MediaTypeMovie, filePath, proposedPath, isConfident));
        }

        // Shared opening logic for folder-dependent scan and process methods: validates the file list,
        // parses the folder name, and performs the TMDB lookup. Returns null if the folder should be skipped.
        private async Task<(MovieInfo? Info, bool IsConfident)?> ResolveFolderMovieAsync(string dirPath, List<string> folderDependent)
        {
            if (folderDependent.Count == 0) return null;

            var dirName = Path.GetFileName(dirPath);
            var (title, year) = FileNameParser.ParseMovie(dirName);

            if (string.IsNullOrWhiteSpace(title))
            {
                LogManager.Instance.LogDebug($"MovieProcessor.ResolveFolderMovieAsync: Skipped folder '{dirName}' - could not parse title", Subsystem.MediaManager);
                return null;
            }

            return await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
        }

        // Scans folder-dependent files (no parseable title) using the parent folder name for TMDB lookup
        private async Task ScanFolderDependentFilesAsync(string dirPath, List<string> folderDependent, List<MediaProposal> proposals)
        {
            var resolved = await ResolveFolderMovieAsync(dirPath, folderDependent).ConfigureAwait(false);
            if (resolved is null) return;

            var (info, isConfident) = resolved.Value;
            if (info is null)
            {
                foreach (var file in folderDependent)
                    proposals.Add(new MediaProposal(MediaTypeMovie, file, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            foreach (var file in folderDependent)
            {
                var proposedPath = BuildFolderMoviePath(file, info);

                if (FileImporter.IsDuplicateFile(file, proposedPath)) continue;

                if (!string.Equals(file, proposedPath, StringComparison.OrdinalIgnoreCase))
                    proposals.Add(new MediaProposal(MediaTypeMovie, file, proposedPath, isConfident));
            }
        }

        // Processes a single self-describing movie file
        private async Task ProcessSingleMovieFileAsync(string sourceFolder, string filePath, string title, int? year)
        {
            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info is null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped '{Path.GetFileName(filePath)}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetPath = BuildStandaloneMoviePath(filePath, info);
            MediaManagerService.ImportFileWithLog(filePath, targetPath, sourceFolder, _dryRun, _importMode);

            MediaManagerService.ImportCompanionFiles(sourceFolder, filePath, targetPath, _dryRun, _importMode);
        }

        // Processes folder-dependent files using the parent folder name for TMDB lookup
        private async Task ProcessFolderDependentFilesAsync(string sourceFolder, string dirPath, List<string> folderDependent)
        {
            var resolved = await ResolveFolderMovieAsync(dirPath, folderDependent).ConfigureAwait(false);
            if (resolved is null) return;

            var (info, isConfident) = resolved.Value;
            if (info is null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped folder '{Path.GetFileName(dirPath)}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            foreach (var file in folderDependent)
                MediaManagerService.ImportFileWithLog(file, BuildFolderMoviePath(file, info), sourceFolder, _dryRun, _importMode);

            var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
            ImportFolderCompanionFiles(sourceFolder, dirPath, Path.GetFileNameWithoutExtension(folderDependent[0]), plexFolderName);
        }

        // Builds the library target path for a standalone movie file
        private string BuildStandaloneMoviePath(string filePath, MovieInfo info)
        {
            var ext      = Path.GetExtension(filePath);
            var plexName = FileNameParser.FormatPlexName(info.Title, info.Year);
            return _createFolders
                ? Path.Combine(_libraryPath, plexName, $"{plexName}{ext}")
                : Path.Combine(_libraryPath, $"{plexName}{ext}");
        }

        // Builds the library target path for a movie file inside a folder (supports multi-part suffixes).
        // Always places files in a subfolder regardless of _createFolders: multi-part files must be grouped
        // together under one folder to avoid name collisions in a flat library root.
        private string BuildFolderMoviePath(string filePath, MovieInfo info)
        {
            var ext            = Path.GetExtension(filePath);
            var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
            var partSuffix     = ExtractPartSuffix(Path.GetFileName(filePath));
            var newFileName    = partSuffix is not null
                ? $"{plexFolderName} - {partSuffix}{ext}"
                : $"{plexFolderName}{ext}";
            return Path.Combine(_libraryPath, plexFolderName, newFileName);
        }

        // Imports subtitle files from a movie folder, renaming them to match the Plex folder name
        private void ImportFolderCompanionFiles(string sourceFolder, string sourceDir, string firstVideoBase, string plexFolderName)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(sourceDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Skipped companion files in '{Path.GetFileName(sourceDir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            foreach (var file in files.Where(FileNameParser.IsSubtitleFile))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.StartsWith(firstVideoBase, StringComparison.OrdinalIgnoreCase)) continue;

                var suffix     = fileName[firstVideoBase.Length..];
                var targetPath = Path.Combine(_libraryPath, plexFolderName, plexFolderName + suffix);
                MediaManagerService.ImportFileWithLog(file, targetPath, sourceFolder, _dryRun, _importMode);
            }
        }

        // Returns a cached movie lookup or performs a new TMDB search and caches the result
        private async Task<(MovieInfo? Info, bool IsConfident)> GetOrLookupMovieAsync(string title, int? year)
        {
            var cacheKey = $"{title}|{year}";
            if (TmdbCacheManager.TryGetMovie(cacheKey, out var cached))
                return cached;

            var result = await LookupMovieAsync(title, year).ConfigureAwait(false);
            TmdbCacheManager.TryAddMovie(cacheKey, result);
            return result;
        }

        private async Task<(MovieInfo? Info, bool IsConfident)> LookupMovieAsync(string title, int? year)
        {
            try
            {
                bool isConfident = true;

                var info = await _tmdb.SearchMovieAsync(title, year).ConfigureAwait(false);

                // Without a year in the filename we cannot corroborate the match by year alone.
                // Require an exact title match and a meaningful vote count to stay confident.
                if (info is not null && !year.HasValue)
                    isConfident = FileNameParser.IsStrongNoYearMatch(title, info.Title, info.VoteCount);

                // Retry without year: parsed year may not match TMDB's release year
                if (info is null && year.HasValue)
                {
                    info = await _tmdb.SearchMovieAsync(title).ConfigureAwait(false);
                    if (info is not null) isConfident = false;
                }

                (info, isConfident) = await TryFallbackLookupsAsync(title, year, info, isConfident).ConfigureAwait(false);

                if (info is null)
                {
                    LogManager.Instance.LogMessage($"No TMDB match found for movie '{title}'", LogLevel.Warn, Subsystem.MediaManager);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"MovieProcessor.LookupMovieAsync: Matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]", Subsystem.MediaManager);
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"Failed to look up TMDB movie: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return (null, false);
            }
        }

        // Applies two fallback TMDB lookup strategies to reduce unmatched results.
        // After-dash strategy: only runs when info is null (no match from initial lookups).
        // Trailing-number strategy: runs when info is null OR info.Year is null.
        private async Task<(MovieInfo? Info, bool IsConfident)> TryFallbackLookupsAsync(
            string title, int? year, MovieInfo? info, bool isConfident)
        {
            // Try the part after " - " (e.g. "Series 1 - The Subtitle")
            var afterDash = info is null ? FileNameParser.ExtractAfterDash(title) : null;

            if (afterDash is not null)
            {
                LogManager.Instance.LogDebug($"MovieProcessor.TryFallbackLookupsAsync: Retrying with '{afterDash}'", Subsystem.MediaManager);
                info = await _tmdb.SearchMovieAsync(afterDash, year).ConfigureAwait(false);
                if (info is not null) isConfident = false;
            }

            // Try without trailing number (e.g. "Title 1" -> "Title")
            // Also runs when info is not null but lacks a year: a year-less TMDB result is low-quality
            // (ambiguous match), so we attempt a stripped title in case TMDB returns a better-quality
            // result that includes a year. If found, it replaces the existing match with isConfident=false
            // so the user can review it in the Media Manager before the file is imported.
            if (info is null || (info.Year is null && title.Length > 2))
            {
                var altInfo = await TryWithoutTrailingNumberAsync(title, year).ConfigureAwait(false);
                if (altInfo is not null)
                {
                    info        = altInfo;
                    isConfident = false;
                }
            }

            return (info, isConfident);
        }

        // Strips a single trailing digit preceded by a space (e.g. "Title 2" -> "Title")
        // and searches TMDB. Returns the match only if it includes a year (quality gate).
        private async Task<MovieInfo?> TryWithoutTrailingNumberAsync(string title, int? year)
        {
            var withoutNum = FileNameParser.StripTrailingNumber(title);
            if (withoutNum is null) return null;

            LogManager.Instance.LogDebug($"MovieProcessor.TryWithoutTrailingNumberAsync: Retrying without trailing number '{withoutNum}'", Subsystem.MediaManager);
            var altInfo = await _tmdb.SearchMovieAsync(withoutNum, year).ConfigureAwait(false);
            return altInfo?.Year is not null ? altInfo : null;
        }

        // Detects multi-part suffixes such as "cd1", "pt2", "disc3" and returns the normalised token.
        // Requires a word boundary before the pattern to avoid matching mid-word (e.g. "Arcade2").
        private static string? ExtractPartSuffix(string fileName)
        {
            var name     = Path.GetFileNameWithoutExtension(fileName);
            string[] patterns = ["cd", "disc", "disk", "dvd", "part", "pt"];

            foreach (var pattern in patterns)
            {
                var idx = name.LastIndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                // Ensure the match is at a word boundary, not embedded in a longer word
                if (idx > 0 && char.IsLetter(name[idx - 1])) continue;

                var after = name[(idx + pattern.Length)..].Trim();
                if (after.Length > 0 && char.IsDigit(after[0]))
                {
                    int numEnd = 0;
                    while (numEnd < after.Length && char.IsDigit(after[numEnd])) numEnd++;
                    return $"{pattern}{after[..numEnd]}";
                }
            }

            return null;
        }
    }
}
