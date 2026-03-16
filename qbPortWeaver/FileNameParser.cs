using System.Text.RegularExpressions;

namespace qbPortWeaver
{
    public static partial class FileNameParser
    {
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".webm"
        };

        private static readonly string[] CutoffTokens =
        [
            // Resolution / quality
            "480p", "576p", "720p", "1080p", "1080i", "2160p", "4k", "fhd", "uhd",
            "hdr", "hdr10", "sdr", "dovi", "10bit", "10-bit", "3d",
            // Source
            "bluray", "blu-ray", "bdrip", "brrip", "bdremux", "remux",
            "dvdrip", "dvdscr", "hdtv", "hdrip", "hdlight", "hdcam",
            "webrip", "web-dl", "webdl", "web",
            "cam", "screener", "telecine", "vod", "imax",
            // Streaming service prefixes (appear before WEB-DL)
            "amzn", "nf", "dsnp", "dsny", "hmax", "atvp", "pcok", "pmtp", "crav", "hulu",
            // Video codec
            "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
            "av1", "vp9", "vc-1", "vc1",
            // Audio codec
            "aac", "ac3", "dts", "dts-hd", "dts-x", "dtsx", "mp3", "flac",
            "truehd", "atmos", "ddp", "ddp5", "eac3", "opus", "lpcm", "pcm",
            // Language
            "multi", "truefrench", "french", "vff", "vfi", "vf2", "vfq",
            "vost", "vostfr", "vof", "dubbed", "subbed",
            // Edition / release flags
            "proper", "repack", "extended", "unrated", "uncut", "directors", "theatrical",
            "remastered", "remaster", "criterion", "limited", "internal",
            "redux", "restored", "final", "hybrid", "mhd",
            // Misc
            "ntsc"
        ];

        /// <summary>Strips characters that are invalid in file names and collapses runs of spaces. Replaces <c>:</c> with <c> -</c> to preserve subtitle separators.</summary>
        public static string SanitizeFileName(string name)
        {
            name = name.Replace(":", " -");
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, ' ');
            name = MultiSpaceRegex().Replace(name, " ");
            return name.Trim();
        }

        /// <summary>Returns true if the file has a recognised video extension.</summary>
        public static bool IsVideoFile(string path) =>
            VideoExtensions.Contains(Path.GetExtension(path));

        /// <summary>Returns true if the filename contains a TV episode pattern (SxxExx or NxNN).</summary>
        public static bool IsTvShowEpisode(string name) =>
            TvShowEpisodeRegex().IsMatch(name) || TvShowEpisodeLegacyRegex().IsMatch(name);

        /// <summary>Returns true if the file is a video file containing a TV episode pattern.</summary>
        public static bool IsVideoTvShowEpisode(string path) =>
            IsVideoFile(path) && IsTvShowEpisode(Path.GetFileName(path));

        /// <summary>
        /// Returns true if the file or folder name already follows Plex naming conventions and
        /// does not need to be looked up or renamed.
        /// <para>Movies: <c>Title (Year)</c> or <c>Title (Year) - partN</c></para>
        /// <para>TV episodes: <c>Show (Year) - SxxExx</c></para>
        /// </summary>
        public static bool IsPlexFormatted(string name)
        {
            var ext = Path.GetExtension(name);
            if (VideoExtensions.Contains(ext))
                name = Path.GetFileNameWithoutExtension(name);

            return PlexMovieNameRegex().IsMatch(name) || PlexEpisodeNameRegex().IsMatch(name);
        }

        /// <summary>Parses a TV episode filename into show name, season, and episode number. Returns null if no episode pattern is found.</summary>
        public static TvShowEpisodeInfo? ParseTvShowEpisode(string name)
        {
            var ext = Path.GetExtension(name);
            if (VideoExtensions.Contains(ext))
                name = Path.GetFileNameWithoutExtension(name);

            name = StripSitePrefix(name);

            var match = TvShowEpisodeRegex().Match(name);
            if (!match.Success)
                match = TvShowEpisodeLegacyRegex().Match(name);
            if (!match.Success)
                return null;

            var rawTitle = name[..match.Index];
            rawTitle = rawTitle.Replace('.', ' ').Replace('_', ' ').Trim();
            rawTitle = StripLanguageSuffix(rawTitle);

            // Extract year from the show name portion so it can be passed to TMDB as a
            // first_air_date_year hint without polluting the title query.
            // Handles both "Yellowstone 2018" (bare year) and "Show Name (2018)" (year in parens).
            // Guard: don't strip the year if it IS the entire title (e.g. show "1883").
            var year = TryStripTrailingYear(ref rawTitle);

            rawTitle = CleanTitle(rawTitle);

            int.TryParse(match.Groups[1].Value, out int season);
            int.TryParse(match.Groups[2].Value, out int episode);
            return new TvShowEpisodeInfo(
                ShowName: rawTitle,
                Year:     year,
                Season:   season,
                Episode:  episode);
        }

        /// <summary>Extracts a probable movie title and optional release year from a filename or folder name.</summary>
        public static (string title, int? year) Parse(string name)
        {
            var ext = Path.GetExtension(name);
            if (VideoExtensions.Contains(ext))
                name = Path.GetFileNameWithoutExtension(name);

            name = StripSitePrefix(name);
            name = StripLanguageSuffix(name);

            // Try explicit year in parentheses first: "Movie Name (2009)"
            var yearInParensMatch = YearInParensRegex().Match(name);
            if (yearInParensMatch.Success)
            {
                var title = CleanTitle(name[..yearInParensMatch.Index].Trim());
                if (!string.IsNullOrWhiteSpace(title))
                    return (title, int.TryParse(yearInParensMatch.Groups[1].Value, out int y) ? y : (int?)null);
            }

            var cleaned  = name.Replace('.', ' ').Replace('_', ' ');
            var rawTitle = FindStandaloneYear(cleaned, out int? parsedYear);

            rawTitle = CutAtTokens(rawTitle);
            rawTitle = CleanTitle(rawTitle);

            return (rawTitle, parsedYear);
        }

        private static string StripSitePrefix(string name)
        {
            var match = SitePrefixRegex().Match(name);
            if (match.Success)
                name = name[match.Length..].TrimStart();
            return name;
        }

        private static string StripLanguageSuffix(string name)
        {
            var match = LanguageSuffixRegex().Match(name);
            if (match.Success)
                name = name[..match.Index].TrimEnd();
            return name;
        }

        // Strips a trailing year hint from rawTitle (year in parens or bare year at end-of-string).
        // Returns the year if the remainder is non-empty, null otherwise.
        // Guard: does not strip the year when it IS the entire title (e.g. show "1883").
        private static int? TryStripTrailingYear(ref string rawTitle)
        {
            // Try year in parens first: "Show Name (2018)"
            var yearInParensMatch = YearInParensRegex().Match(rawTitle);
            if (yearInParensMatch.Success)
            {
                var titlePart = rawTitle[..yearInParensMatch.Index].Trim();
                if (!string.IsNullOrEmpty(titlePart))
                {
                    rawTitle = titlePart;
                    return int.TryParse(yearInParensMatch.Groups[1].Value, out int y) ? y : (int?)null;
                }
                return null;
            }

            // Try bare year at end-of-string: "Yellowstone 2018"
            var yearMatch = StandaloneYearRegex().Match(rawTitle);
            if (yearMatch.Success && yearMatch.Index + yearMatch.Length == rawTitle.Length)
            {
                var titlePart = rawTitle[..yearMatch.Index].Trim();
                if (!string.IsNullOrEmpty(titlePart))
                {
                    rawTitle = titlePart;
                    return int.TryParse(yearMatch.Value, out int y) ? y : (int?)null;
                }
            }
            return null;
        }

        // Finds the first standalone 4-digit year in a pre-cleaned (dots/underscores removed) title string.
        // If the first match would leave an empty title (e.g. "1917 2019 BluRay"), advances to the next
        // occurrence so the year-as-title becomes part of the title string.
        // Returns the title portion before the year; sets parsedYear to null if no year was found.
        private static string FindStandaloneYear(string cleaned, out int? parsedYear)
        {
            var yearMatch = StandaloneYearRegex().Match(cleaned);
            if (!yearMatch.Success)
            {
                parsedYear = null;
                return cleaned;
            }

            if (string.IsNullOrWhiteSpace(cleaned[..yearMatch.Index]))
            {
                var next = StandaloneYearRegex().Match(cleaned, yearMatch.Index + yearMatch.Length);
                if (next.Success)
                    yearMatch = next;
            }

            parsedYear = int.TryParse(yearMatch.Value, out int y) ? y : (int?)null;
            return cleaned[..yearMatch.Index];
        }

        private static string CutAtTokens(string input)
        {
            var words  = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            foreach (var word in words)
            {
                if (CutoffTokens.Contains(word, StringComparer.OrdinalIgnoreCase))
                    break;
                result.Add(word);
            }
            return string.Join(' ', result);
        }

        private static string CleanTitle(string title)
        {
            title = title.Trim(' ', '-', '.', '_');
            title = CurlyBraceTagRegex().Replace(title, "").Trim();
            title = SquareBracketTagRegex().Replace(title, "").Trim();
            title = MultiSpaceRegex().Replace(title, " ");
            return title.Trim();
        }

        [GeneratedRegex(@"\((\d{4})\)")]
        private static partial Regex YearInParensRegex();

        [GeneratedRegex(@"\b(19|20)\d{2}\b")]
        private static partial Regex StandaloneYearRegex();

        [GeneratedRegex(@"\{[^}]*\}")]
        private static partial Regex CurlyBraceTagRegex();

        [GeneratedRegex(@"\[[^\]]*\]")]
        private static partial Regex SquareBracketTagRegex();

        [GeneratedRegex(@"\s{2,}")]
        private static partial Regex MultiSpaceRegex();

        [GeneratedRegex(@"\[\s*[^\]]*\.[a-z]{2,3}\s*\]\s*", RegexOptions.IgnoreCase)]
        private static partial Regex SitePrefixRegex();

        [GeneratedRegex(@"[_]((?:FR|EN|VF|VO)[-]?(?:FR|EN|HP|DL|VF|VO)?(?:[-](?:FR|EN|HP|DL|VF|VO))*)$", RegexOptions.IgnoreCase)]
        private static partial Regex LanguageSuffixRegex();

        // Primary: SxxExx / S1E1 (dominant scene and P2P standard)
        [GeneratedRegex(@"S(\d{1,2})E(\d{1,2})", RegexOptions.IgnoreCase)]
        private static partial Regex TvShowEpisodeRegex();

        // Legacy: 1x01 notation used by older releases
        [GeneratedRegex(@"\b(\d{1,2})x(\d{2})\b", RegexOptions.IgnoreCase)]
        private static partial Regex TvShowEpisodeLegacyRegex();

        // Matches Plex movie format: "Title (Year)" optionally followed by " - partN" (multi-part files)
        [GeneratedRegex(@"^.+\s\(\d{4}\)(\s-\s(cd|disc|disk|dvd|part|pt)\d)?$", RegexOptions.IgnoreCase)]
        private static partial Regex PlexMovieNameRegex();

        // Matches Plex TV episode format: "Show (Year) - SxxExx" (always zero-padded in output)
        [GeneratedRegex(@"^.+\s\(\d{4}\)\s-\sS\d{2}E\d{2}$", RegexOptions.IgnoreCase)]
        private static partial Regex PlexEpisodeNameRegex();
    }

    public sealed record TvShowEpisodeInfo(string ShowName, int? Year, int Season, int Episode);
}
