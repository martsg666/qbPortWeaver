using System.Diagnostics.CodeAnalysis;

namespace qbPortWeaver;

/// <summary>
/// Processes movie files and folders, applying Plex naming conventions and importing them into the library:
/// Library/Title (Year)/Title (Year).ext  - with folder creation
/// Library/Title (Year).ext               - without folder creation
/// Library/Title (Year)/Title (Year) - cd1.ext - each part of a multi-part movie, always in a folder
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
    public async Task<List<MediaProposal>> ScanMoviesAsync(string[] movieFiles, Action? onItemProcessed = null, CancellationToken cancellationToken = default)
    {
        var proposals = new List<MediaProposal>();

        var (selfDescribing, folderDependent) = ClassifyVideoFiles(movieFiles);

        foreach (var (file, title, year, partSuffix) in selfDescribing)
        {
            await ScanMovieFileAsync(file, title, year, partSuffix, proposals, cancellationToken).ConfigureAwait(false);
            onItemProcessed?.Invoke();
        }

        foreach (var group in folderDependent.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
        {
            await ScanFolderDependentFilesAsync(group.Key, group.ToList(), proposals, cancellationToken).ConfigureAwait(false);
            foreach (var _ in group) onItemProcessed?.Invoke();
        }

        return proposals;
    }

    /// <summary>Processes pre-classified movie files, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanMoviesAsync"/> to preview and review those first.</summary>
    public async Task ProcessMoviesAsync(string[] movieFiles, CancellationToken cancellationToken = default)
    {
        var (selfDescribing, folderDependent) = ClassifyVideoFiles(movieFiles);

        foreach (var (file, title, year, partSuffix) in selfDescribing)
            await ProcessMovieFileAsync(file, title, year, partSuffix, cancellationToken).ConfigureAwait(false);

        foreach (var group in folderDependent.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
            await ProcessFolderDependentFilesAsync(group.Key, group.ToList(), cancellationToken).ConfigureAwait(false);
    }

    // Splits video files into self-describing (filename has a parseable movie title) and folder-dependent
    // (e.g. cd1.mkv, part2.mkv - no useful title, need the parent folder name for TMDB lookup).
    // Self-describing files get individual TMDB lookups; folder-dependent files share a single
    // folder-name-based lookup (multi-part scenario). TV episodes don't need this split because
    // their S##E## pattern always makes them individually identifiable.
    private static (List<(string File, string Title, int? Year, string? PartSuffix)> SelfDescribing, List<string> FolderDependent)
        ClassifyVideoFiles(string[] videoFiles)
    {
        var selfDescribing = new List<(string File, string Title, int? Year)>();
        var folderDependent = new List<string>();

        foreach (var file in videoFiles)
        {
            var fileName = Path.GetFileName(file);
            var (title, year) = FileNameParser.ParseTitleYear(fileName);
            if (!string.IsNullOrWhiteSpace(title) && !IsBarePartName(fileName, title, year))
                selfDescribing.Add((file, title, year));
            else
                folderDependent.Add(file);
        }

        return (AssignPartSuffixes(selfDescribing), folderDependent);
    }

    // A file named only by its part ("cd1.mkv", "Part 2.mkv", "Disc 1.mkv") parses as a movie titled
    // "cd1" and so on; it belongs to the folder-name lookup instead, which keeps the part suffix. Only
    // when the parsed title IS the part token and there is no year, so "Title Part 2" and "Title 2"
    // stay ordinary titles. Uses the same ExtractPartSuffix the folder path names the parts with, so
    // a name it cannot number stays where it is rather than collide there.
    private static bool IsBarePartName(string fileName, string title, int? year)
    {
        if (year is not null) return false;
        var suffix = ExtractPartSuffix(fileName);
        var compactTitle = new string(title.Where(char.IsLetterOrDigit).ToArray());
        return suffix is not null && string.Equals(suffix, compactTitle, StringComparison.OrdinalIgnoreCase);
    }

    // Parts of one movie whose names each carry the title ("Title.Year.cd1", "Title.Year.cd2") would all
    // map to one standalone target, so only the first would ever import. They are recognised only on
    // unambiguous evidence - two or more files in one folder with the same title and year, each with a
    // different part suffix - so a lone title such as "Title Part 2 (Year)" is never taken for a part.
    // The evidence is the folder on disk, not only this list: it holds just the files ready and not yet
    // imported, so parts finishing in different cycles would each look alone. Every other file gets a
    // null suffix and keeps the standalone layout.
    private static List<(string File, string Title, int? Year, string? PartSuffix)> AssignPartSuffixes(
        List<(string File, string Title, int? Year)> files)
    {
        var result = new List<(string File, string Title, int? Year, string? PartSuffix)>(files.Count);
        var groups = files.GroupBy(f => (
            Folder: (Path.GetDirectoryName(f.File) ?? string.Empty).ToUpperInvariant(),
            Title: f.Title.ToUpperInvariant(),
            f.Year));

        foreach (var group in groups)
        {
            var members = group.ToList();
            var suffixes = members.Select(f => ExtractPartSuffix(Path.GetFileName(f.File))).ToList();
            bool isMultiPart = suffixes.TrueForAll(s => s is not null) && IsMultiPartGroup(members[0], members.Select(f => f.File));

            for (int i = 0; i < members.Count; i++)
                result.Add((members[i].File, members[i].Title, members[i].Year, isMultiPart ? suffixes[i] : null));
        }
        return result;
    }

    // Applies the multi-part rule to the group's files plus every video file beside them with the same
    // title and year, imported or still being written. A folder that cannot be listed leaves the group
    // to decide alone.
    private static bool IsMultiPartGroup((string File, string Title, int? Year) first, IEnumerable<string> groupFiles)
    {
        var files = new HashSet<string>(groupFiles, StringComparer.OrdinalIgnoreCase);
        var folder = Path.GetDirectoryName(first.File);
        if (!string.IsNullOrEmpty(folder))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    if (!FileNameParser.IsVideoFile(path)) continue;
                    var fileName = Path.GetFileName(path);
                    var (title, year) = FileNameParser.ParseTitleYear(fileName);
                    if (year == first.Year && string.Equals(title, first.Title, StringComparison.OrdinalIgnoreCase)
                        && !IsBarePartName(fileName, title, year))
                        files.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogDebug($"MovieProcessor.IsMultiPartGroup: Could not list '{folder}': {ex.Message}", Subsystem.MediaManager);
            }
        }

        var suffixes = files.Select(f => ExtractPartSuffix(Path.GetFileName(f))).ToList();
        return suffixes.Count > 1 &&
               suffixes.TrueForAll(s => s is not null) &&
               suffixes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == suffixes.Count;
    }

    // Shared opening logic for folder-dependent scan and process methods: validates the file list,
    // parses the folder name, and performs the TMDB lookup. Returns null if the folder should be skipped.
    private async Task<(MovieInfo? Info, bool IsConfident)?> ResolveFolderMovieAsync(string dirPath, List<string> folderDependent, CancellationToken cancellationToken)
    {
        if (folderDependent.Count == 0) return null;

        var dirName = Path.GetFileName(dirPath);
        var (title, year) = FileNameParser.ParseTitleYear(dirName);

        if (string.IsNullOrWhiteSpace(title))
        {
            LogManager.Instance.LogDebug($"MovieProcessor.ResolveFolderMovieAsync: Skipped folder '{dirName}' - could not parse title", Subsystem.MediaManager);
            return null;
        }

        return await GetOrLookupMovieAsync(title, year, cancellationToken).ConfigureAwait(false);
    }

    // Scans a single self-describing movie file and adds proposals for unmatched or new items
    private async Task ScanMovieFileAsync(string filePath, string title, int? year, string? partSuffix, List<MediaProposal> proposals, CancellationToken cancellationToken)
    {
        var (info, isConfident) = await GetOrLookupMovieAsync(title, year, cancellationToken).ConfigureAwait(false);
        var proposedPath = info is not null ? BuildSelfDescribingMoviePath(filePath, info, partSuffix) : string.Empty;
        AddMovieScanProposal(filePath, info, isConfident, proposedPath, proposals);
    }

    // Scans folder-dependent files (no parseable title) using the parent folder name for TMDB lookup
    private async Task ScanFolderDependentFilesAsync(string dirPath, List<string> folderDependent, List<MediaProposal> proposals, CancellationToken cancellationToken)
    {
        var resolved = await ResolveFolderMovieAsync(dirPath, folderDependent, cancellationToken).ConfigureAwait(false);
        if (resolved is null) return;
        var (info, isConfident) = resolved.Value;

        foreach (var file in folderDependent)
        {
            var proposedPath = info is not null ? BuildFolderMoviePath(file, info) : string.Empty;
            AddMovieScanProposal(file, info, isConfident, proposedPath, proposals);
        }
    }

    // Imports a single self-describing movie file into the library
    private async Task ProcessMovieFileAsync(string filePath, string title, int? year, string? partSuffix, CancellationToken cancellationToken)
    {
        var (info, isConfident) = await GetOrLookupMovieAsync(title, year, cancellationToken).ConfigureAwait(false);
        if (!ShouldImportMatch(info, isConfident, $"'{Path.GetFileName(filePath)}'")) return;

        var targetPath = BuildSelfDescribingMoviePath(filePath, info, partSuffix);
        // Standalone files use the shared companion importer (subtitle matched by filename prefix), and
        // only once the video is in place - a skipped or failed video leaves its subtitles where they are.
        if (MediaManagerService.ImportFile(filePath, targetPath, dryRun, importMode))
            MediaManagerService.ImportCompanionFiles(filePath, targetPath, dryRun, importMode);
    }

    // Processes folder-dependent files using the parent folder name for TMDB lookup
    private async Task ProcessFolderDependentFilesAsync(string dirPath, List<string> folderDependent, CancellationToken cancellationToken)
    {
        var resolved = await ResolveFolderMovieAsync(dirPath, folderDependent, cancellationToken).ConfigureAwait(false);
        if (resolved is null) return;
        var (info, isConfident) = resolved.Value;
        if (!ShouldImportMatch(info, isConfident, $"folder '{Path.GetFileName(dirPath)}'")) return;

        // ImportFile does the import and reports whether the part was placed; ToList runs it once per file.
        var placed = folderDependent
            .Where(file => MediaManagerService.ImportFile(file, BuildFolderMoviePath(file, info), dryRun, importMode))
            .ToList();

        var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
        // Folder-dependent files use a separate companion importer that pairs subtitles by part suffix
        // (e.g. cd1.srt -> Title (Year) - cd1.srt) rather than by filename prefix. Only the parts that
        // were placed bring their subtitles.
        if (placed.Count > 0)
            ImportFolderCompanionFiles(dirPath, placed, plexFolderName);
    }

    // Shared per-file scan proposal logic. Both standalone and folder-dependent paths call this
    // once per file with the already-built target path; movies have two path layouts (with/without
    // title subfolder, plus a multi-part suffix) so path construction stays at the call site.
    // When info is null an unmatched proposal is added. Otherwise the destination-matches check
    // and source-equals-target check are applied before adding a matched proposal.
    private static void AddMovieScanProposal(string filePath, MovieInfo? info, bool isConfident, string proposedPath, List<MediaProposal> proposals)
    {
        if (info is null)
        {
            proposals.Add(new MediaProposal(MediaProposal.TypeMovie, filePath, string.Empty, IsConfident: false, IsMatched: false));
            return;
        }

        if (MediaImporter.DestinationMatchesSource(filePath, proposedPath)) return;

        if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
            proposals.Add(new MediaProposal(MediaProposal.TypeMovie, filePath, proposedPath, isConfident,
                PosterPath: info.PosterPath, TmdbId: info.TmdbId, VoteCount: info.VoteCount, Overview: info.Overview));
    }

    // Returns true when the TMDB match is confident enough to proceed with import. Logs a Warn
    // with the supplied skip label when the match is uncertain. [NotNullWhen(true)] on info
    // lets callers use info without a null check after a true return.
    private static bool ShouldImportMatch([NotNullWhen(true)] MovieInfo? info, bool isConfident, string skipLabel)
    {
        if (info is null) return false;
        if (!isConfident)
        {
            LogManager.Instance.LogMessage(MediaManagerService.UncertainMatchSkip(skipLabel), LogLevel.Warn, Subsystem.MediaManager);
            return false;
        }
        return true;
    }

    // Builds the target for a self-describing file: the standalone layout, or - when AssignPartSuffixes
    // recognised it as one part of a multi-part movie - the same folder layout as folder-dependent parts.
    private string BuildSelfDescribingMoviePath(string filePath, MovieInfo info, string? partSuffix)
    {
        if (partSuffix is null) return BuildStandaloneMoviePath(filePath, info, libraryPath, createFolders);

        var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
        return Path.Combine(libraryPath, plexFolderName, BuildFolderMovieFileName(plexFolderName, partSuffix, Path.GetExtension(filePath)));
    }

    // Builds the library target path for a standalone movie file.
    private static string BuildStandaloneMoviePath(string filePath, MovieInfo info, string libraryPath, bool createFolders)
    {
        var ext = Path.GetExtension(filePath);
        var plexName = FileNameParser.FormatPlexName(info.Title, info.Year);
        return createFolders
            ? Path.Combine(libraryPath, plexName, $"{plexName}{ext}")
            : Path.Combine(libraryPath, $"{plexName}{ext}");
    }

    // Builds the library target path for a re-matched movie row, for MediaManagerForm to share the
    // same naming convention as the scan. A name ending in the multi-part
    // form BuildFolderMovieFileName produces ("Title (Year) - cd1.ext") keeps its part suffix and its
    // subfolder, so re-matching the parts of one movie does not give them all the same target.
    internal static string BuildRematchMoviePath(string editedName, MovieInfo info, string libraryPath, bool createFolders)
    {
        if (!TrySplitPartSuffix(Path.GetFileNameWithoutExtension(editedName), out _, out var partSuffix))
            return BuildStandaloneMoviePath(editedName, info, libraryPath, createFolders);

        var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
        return Path.Combine(libraryPath, plexFolderName, BuildFolderMovieFileName(plexFolderName, partSuffix, Path.GetExtension(editedName)));
    }

    // Splits a name in the multi-part form BuildFolderMovieFileName produces ("Title (Year) - cd1") into
    // its base and part suffix. Anything else returns false with the whole name as the base, so an
    // ordinary title such as "Title - Part 2 (Year)" is never mistaken for a part.
    internal static bool TrySplitPartSuffix(string nameWithoutExt, out string baseName, [NotNullWhen(true)] out string? partSuffix)
    {
        int dash = nameWithoutExt.LastIndexOf(" - ", StringComparison.Ordinal);
        var segment = dash < 0 ? string.Empty : nameWithoutExt[(dash + 3)..];
        partSuffix = ExtractPartSuffix(segment);
        if (partSuffix is null || !string.Equals(segment, partSuffix, StringComparison.OrdinalIgnoreCase))
        {
            baseName = nameWithoutExt;
            partSuffix = null;
            return false;
        }
        baseName = nameWithoutExt[..dash];
        return true;
    }

    // Builds the library target path for a movie file inside a folder (supports multi-part suffixes).
    // Always places files in a subfolder regardless of createFolders: multi-part files must be grouped
    // together under one folder to avoid name collisions in a flat library root.
    private string BuildFolderMoviePath(string filePath, MovieInfo info)
    {
        var ext = Path.GetExtension(filePath);
        var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
        var partSuffix = ExtractPartSuffix(Path.GetFileName(filePath));
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
    private void ImportFolderCompanionFiles(string sourceDir, List<string> videoFiles, string plexFolderName)
    {
        string[] files;
        try
        {
            files = MediaImporter.InvokeWithSmbRetry(sourceDir, () => Directory.GetFiles(sourceDir));
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
            var videoBase = Path.GetFileNameWithoutExtension(video);
            var partSuffix = ExtractPartSuffix(Path.GetFileName(video));

            foreach (var subtitle in subtitles)
            {
                var subName = Path.GetFileName(subtitle);
                if (!subName.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)) continue;
                // Require the character immediately after the base name to be '.' or end of string
                // so "Movie.mkv" does not claim "Movie 2.srt" as a companion.
                if (subName.Length > videoBase.Length && subName[videoBase.Length] != '.') continue;

                var suffix = subName[videoBase.Length..];
                var targetPath = Path.Combine(libraryPath, plexFolderName, BuildFolderMovieFileName(plexFolderName, partSuffix, suffix));
                MediaManagerService.ImportFile(subtitle, targetPath, dryRun, importMode);
            }
        }
    }

    // Returns a cached movie lookup or performs a new TMDB search, deduplicating concurrent lookups
    // for the same title so parallel source-folder scans share one TMDB API call.
    private Task<(MovieInfo? Info, bool IsConfident)> GetOrLookupMovieAsync(string title, int? year, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{title}|{year}";
        return TmdbCacheManager.GetOrComputeMovieAsync(cacheKey, async () =>
        {
            var result = await TmdbClient.LookupAsync((title, year),
                tmdb.SearchMovieCandidatesAsync,
                i => i.Year is not null, i => i.Title, i => i.VoteCount, "movie", cancellationToken).ConfigureAwait(false);
            if (result.Info is not null)
                LogManager.Instance.LogDebug($"MovieProcessor.GetOrLookupMovieAsync: Matched '{result.Info.Title}' ({result.Info.Year}) [tmdb-{result.Info.TmdbId}]", Subsystem.MediaManager);
            return result;
        });
    }

    private static readonly string[] _multiPartPatterns = ["cd", "disc", "disk", "dvd", "part", "pt"];

    // Detects multi-part suffixes such as "cd1", "pt2", "disc3" and returns the normalised token.
    // Requires a word boundary before the pattern to avoid matching mid-word (e.g. "Arcade2").
    private static string? ExtractPartSuffix(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        foreach (var pattern in _multiPartPatterns)
        {
            var idx = name.LastIndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            // Ensure the match is at a word boundary, not embedded in a longer word
            if (idx > 0 && char.IsLetterOrDigit(name[idx - 1])) continue;

            // The number may follow a separator as well as a space: "Part.1", "CD_1", "Disc-2"
            var after = name[(idx + pattern.Length)..].TrimStart(' ', '.', '_', '-');
            int numEnd = after.TakeWhile(char.IsDigit).Count();
            if (numEnd > 0)
                return $"{pattern}{after[..numEnd]}";
        }

        return null;
    }
}
