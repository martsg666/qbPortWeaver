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
            "720p", "1080p", "2160p", "4k", "uhd", "hdr", "10bit", "3d",
            // Source
            "bluray", "blu-ray", "bdrip", "brrip", "bdremux", "remux",
            "dvdrip", "dvdscr", "hdtv", "hdrip", "hdlight",
            "webrip", "web-dl", "webdl", "web",
            "cam", "screener", "telecine", "vod", "imax",
            // Video codec
            "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
            "av1", "vp9",
            // Audio codec
            "aac", "ac3", "dts", "dts-hd", "mp3", "flac", "truehd", "atmos", "ddp5", "eac3",
            // Language
            "multi", "truefrench", "french", "vff", "vfi", "vf2", "vfq",
            "dubbed", "subbed",
            // Edition / release flags
            "proper", "repack", "extended", "unrated", "uncut", "directors", "theatrical",
            "remastered", "remaster", "criterion", "limited", "internal",
            "final", "hybrid", "mhd",
            // Misc
            "pmtp", "ntsc"
        ];

        /// <summary>Strips characters that are invalid in file names and collapses runs of spaces. Replaces <c>:</c> with <c> -</c> to preserve subtitle separators.</summary>
        public static string SanitizeFileName(string name)
        {
            name = name.Replace(":", " -");
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, ' ');
            while (name.Contains("  "))
                name = name.Replace("  ", " ");
            return name.Trim();
        }

        /// <summary>Returns true if the file has a recognised video extension.</summary>
        public static bool IsVideoFile(string path) =>
            VideoExtensions.Contains(Path.GetExtension(path));

        /// <summary>Returns true if the filename contains a TV episode pattern (SxxExx).</summary>
        public static bool IsTvEpisode(string name) =>
            TvEpisodeRegex().IsMatch(name);

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
        public static TvEpisodeInfo? ParseTvEpisode(string name)
        {
            var ext = Path.GetExtension(name);
            if (VideoExtensions.Contains(ext))
                name = Path.GetFileNameWithoutExtension(name);

            name = StripSitePrefix(name);

            var match = TvEpisodeRegex().Match(name);
            if (!match.Success)
                return null;

            var rawTitle = name[..match.Index];
            rawTitle = rawTitle.Replace('.', ' ').Replace('_', ' ').Trim();
            rawTitle = CleanTitle(rawTitle);

            return new TvEpisodeInfo
            {
                ShowName = rawTitle,
                Season   = int.Parse(match.Groups[1].Value),
                Episode  = int.Parse(match.Groups[2].Value)
            };
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
                    return (title, int.Parse(yearInParensMatch.Groups[1].Value));
            }

            var cleaned = name.Replace('.', ' ').Replace('_', ' ');

            // Try to find a standalone 4-digit year (1900–2099)
            var yearMatch = StandaloneYearRegex().Match(cleaned);
            string rawTitle;
            int? parsedYear = null;

            if (yearMatch.Success)
            {
                rawTitle   = cleaned[..yearMatch.Index];
                parsedYear = int.Parse(yearMatch.Value);
            }
            else
            {
                rawTitle = cleaned;
            }

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

        [GeneratedRegex(@"S(\d{2})E(\d{2})", RegexOptions.IgnoreCase)]
        private static partial Regex TvEpisodeRegex();

        // Matches Plex movie format: "Title (Year)" optionally followed by " - partN" (multi-part files)
        [GeneratedRegex(@"^.+\s\(\d{4}\)(\s-\s(cd|disc|disk|dvd|part|pt)\d)?$", RegexOptions.IgnoreCase)]
        private static partial Regex PlexMovieNameRegex();

        // Matches Plex TV episode format: "Show (Year) - SxxExx"
        [GeneratedRegex(@"^.+\s\(\d{4}\)\s-\sS\d{2}E\d{2}$", RegexOptions.IgnoreCase)]
        private static partial Regex PlexEpisodeNameRegex();
    }

    public class TvEpisodeInfo
    {
        public string ShowName { get; set; } = "";
        public int Season { get; set; }
        public int Episode { get; set; }
    }
}
