namespace qbPortWeaver
{
    /// <summary>
    /// Processes movie files and folders, applying Plex naming conventions and importing them into the library:
    /// Library/Title (Year)/Title (Year).ext  - with folder creation
    /// Library/Title (Year).ext               - without folder creation
    /// Files are transferred via hardlink, copy, or move depending on the configured import mode.
    /// </summary>
    /// <param name="tmdb">TMDB client for movie metadata lookups.</param>
    /// <param name="dryRun">When true, logs what would happen without importing any files.</param>
    /// <param name="createFolders">When true, imports files into Plex-recommended subfolders.</param>
    /// <param name="libraryPath">Target library folder for imported movies.</param>
    /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
    public sealed class MovieProcessor(TmdbClient tmdb, bool dryRun, bool createFolders, string libraryPath, ImportMode importMode = ImportMode.Hardlink)
    {
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
                await ScanMovieFileAsync(file, title, year, proposals).ConfigureAwait(false);
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
                await ProcessMovieFileAsync(sourceFolder, file, title, year).ConfigureAwait(false);

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

        // Scans a single self-describing movie file and adds proposals for unmatched or new items
        private async Task ScanMovieFileAsync(string filePath, string title, int? year, List<MediaProposal> proposals)
        {
            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info is null)
            {
                proposals.Add(new MediaProposal(MediaProposal.TypeMovie, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var proposedPath = BuildStandaloneMoviePath(filePath, info, libraryPath, createFolders);

            if (MediaImporter.IsDuplicateFile(filePath, proposedPath)) return;

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new MediaProposal(MediaProposal.TypeMovie, filePath, proposedPath, isConfident, PosterPath: info.PosterPath, TmdbId: info.TmdbId, VoteCount: info.VoteCount, Overview: info.Overview));
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
                    proposals.Add(new MediaProposal(MediaProposal.TypeMovie, file, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            foreach (var file in folderDependent)
            {
                var proposedPath = BuildFolderMoviePath(file, info);

                if (MediaImporter.IsDuplicateFile(file, proposedPath)) continue;

                if (!string.Equals(file, proposedPath, StringComparison.OrdinalIgnoreCase))
                    proposals.Add(new MediaProposal(MediaProposal.TypeMovie, file, proposedPath, isConfident, PosterPath: info.PosterPath, TmdbId: info.TmdbId, VoteCount: info.VoteCount, Overview: info.Overview));
            }
        }

        // Imports a single self-describing movie file into the library
        private async Task ProcessMovieFileAsync(string sourceFolder, string filePath, string title, int? year)
        {
            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info is null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped '{Path.GetFileName(filePath)}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetPath = BuildStandaloneMoviePath(filePath, info, libraryPath, createFolders);
            MediaManagerService.ImportFile(filePath, targetPath, sourceFolder, dryRun, importMode);

            MediaManagerService.ImportCompanionFiles(sourceFolder, filePath, targetPath, dryRun, importMode);
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
                MediaManagerService.ImportFile(file, BuildFolderMoviePath(file, info), sourceFolder, dryRun, importMode);

            var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
            ImportFolderCompanionFiles(sourceFolder, dirPath, folderDependent, plexFolderName);
        }

        // Builds the library target path for a standalone movie file.
        // Exposed for MediaManagerForm rematch logic to share the same naming convention.
        internal static string BuildStandaloneMoviePath(string filePath, MovieInfo info, string libraryPath, bool createFolders)
        {
            var ext      = Path.GetExtension(filePath);
            var plexName = FileNameParser.FormatPlexName(info.Title, info.Year);
            return createFolders
                ? Path.Combine(libraryPath, plexName, $"{plexName}{ext}")
                : Path.Combine(libraryPath, $"{plexName}{ext}");
        }

        // Builds the library target path for a movie file inside a folder (supports multi-part suffixes).
        // Always places files in a subfolder regardless of _createFolders: multi-part files must be grouped
        // together under one folder to avoid name collisions in a flat library root.
        private string BuildFolderMoviePath(string filePath, MovieInfo info)
        {
            var ext            = Path.GetExtension(filePath);
            var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
            var partSuffix     = ExtractPartSuffix(Path.GetFileName(filePath));
            return Path.Combine(libraryPath, plexFolderName, BuildFolderMovieFileName(plexFolderName, partSuffix, ext));
        }

        // Shared naming convention for folder-movie files and their companion subtitles.
        // Produces "Title (Year) - cd1.ext" for multi-part files, "Title (Year).ext" otherwise.
        private static string BuildFolderMovieFileName(string plexFolderName, string? partSuffix, string extension) =>
            partSuffix is not null
                ? $"{plexFolderName} - {partSuffix}{extension}"
                : $"{plexFolderName}{extension}";

        // Imports subtitle files from a movie folder, renaming each to match its corresponding video.
        // Multi-part folders produce per-part subtitles (e.g. cd1.srt -> Title (Year) - cd1.srt) so each
        // subtitle lines up with the video it belongs to.
        private void ImportFolderCompanionFiles(string sourceFolder, string sourceDir, List<string> videoFiles, string plexFolderName)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(sourceDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Skipped companion files in '{sourceDir}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var subtitles = files.Where(FileNameParser.IsSubtitleFile).ToArray();
            if (subtitles.Length == 0) return;

            foreach (var video in videoFiles)
            {
                var videoBase  = Path.GetFileNameWithoutExtension(video);
                var partSuffix = ExtractPartSuffix(Path.GetFileName(video));

                foreach (var subtitle in subtitles)
                {
                    var subName = Path.GetFileName(subtitle);
                    if (!subName.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)) continue;

                    var suffix     = subName[videoBase.Length..];
                    var targetPath = Path.Combine(libraryPath, plexFolderName, BuildFolderMovieFileName(plexFolderName, partSuffix, suffix));
                    MediaManagerService.ImportFile(subtitle, targetPath, sourceFolder, dryRun, importMode);
                }
            }
        }

        // Returns a cached movie lookup or performs a new TMDB search and caches the result
        private async Task<(MovieInfo? Info, bool IsConfident)> GetOrLookupMovieAsync(string title, int? year)
        {
            var cacheKey = $"{title}|{year}";
            if (TmdbCacheManager.TryGetMovie(cacheKey, out var cached))
                return cached;

            var result = await TmdbClient.LookupAsync(title, year,
                tmdb.SearchMovieAsync, i => i.Year is not null, i => i.Title, i => i.VoteCount, "movie").ConfigureAwait(false);
            if (result.Info is not null)
                LogManager.Instance.LogDebug($"MovieProcessor.GetOrLookupMovieAsync: Matched '{result.Info.Title}' ({result.Info.Year}) [tmdb-{result.Info.TmdbId}]", Subsystem.MediaManager);
            TmdbCacheManager.TryAddMovie(cacheKey, result);
            return result;
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

                var after  = name[(idx + pattern.Length)..].Trim();
                int numEnd = after.TakeWhile(char.IsDigit).Count();
                if (numEnd > 0)
                    return $"{pattern}{after[..numEnd]}";
            }

            return null;
        }
    }
}
