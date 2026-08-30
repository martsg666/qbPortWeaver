using System.Text;
using System.Text.RegularExpressions;

namespace qbPortWeaver;

/// <summary>Extracts movie titles, release years, and TV episode metadata from filenames for Plex-compatible renaming.</summary>
public static partial class FileNameParser
{
    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".vob", ".webm"
    };

    private static readonly HashSet<string> _subtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".smi", ".pgs", ".sup"
    };

    private static readonly HashSet<string> _cutoffTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // Resolution / quality
        "240p", "360p", "480i", "480p", "540i", "540p", "576i", "576p",
        "720p", "1080p", "1080i", "1440p", "2160p", "4320p", "4k", "fhd", "uhd", "qhd",
        "hdr", "hdr10", "hdr10plus", "hlg", "sdr", "dovi", "dolbyvision", "pq", "pq10",
        "8bit", "8-bit", "10bit", "10-bit", "12bit", "12-bit", "hi10p", "hi10",
        "3d", "sbs", "hsbs", "half-ou", "hou", "mvc",
        "upscale", "upscaled",
        // Source
        "bluray", "blu-ray", "bdrip", "brrip", "bdremux", "remux",
        "bd25", "bd50", "bd66", "bd100", "bdscr", "bdiso", "bdmv",
        "dvdrip", "dvdscr", "dvdscreener", "dvdr", "dvd9", "dvd5",
        "hdtv", "hdtvrip", "pdtv", "sdtv", "uhdtv", "hdrip", "hdlight", "hdcam", "hqcam",
        "tvrip", "dvrip", "dvbrip", "satrip", "vhsrip", "ppvrip", "dsr", "dsrip",
        "webrip", "web-dl", "webdl", "web", "webmux", "webscreener", "uhdrip",
        "cam", "camrip", "scr", "screener", "telecine", "telesync", "ts", "tc", "hdts", "hdtc", "vod", "imax",
        "r5", "r6", "workprint", "wp", "retail",
        "hddvd", "hd-dvd", "ldrip", "dcp",
        // Streaming service prefixes (appear before the web source token)
        "amzn", "amz", "nf", "nflx", "dsnp", "dnsp", "dsny", "hmax", "hbomax", "hbo",
        "atvp", "pcok", "pmtp", "pmnp", "para", "crav", "hulu", "roku",
        "bcore", "stan", "itun", "htsr", "dscp", "funi", "adn", "ma",
        "sho", "starz", "itvx", "tubi", "pluto", "mubi",
        // Video codec
        "x264", "x265", "x266", "h264", "h265", "h266", "hevc", "avc", "xvid", "divx",
        "av1", "vp7", "vp8", "vp9", "vc-1", "vc1", "vvc", "mpeg", "mpeg2", "mpeg4",
        // Audio codec
        "aac", "ac3", "dts", "dts-hd", "dts-hdma", "dts-ma", "dtsma", "dts-hdhr", "dts-hdhra", "dts-es", "dts-x", "dtsx",
        "mp2", "mp3", "flac", "vorbis", "heaac", "he-aac",
        "truehd", "atmos", "dd", "dd1", "dd2", "dd5", "dd7", "ddp", "ddp1", "ddp2", "ddp5", "ddp7", "ddplus",
        "dolbydigital", "eac3", "opus", "lpcm", "pcm",
        "stereo", "mono", "2ch", "6ch", "8ch",
        // Language (note: "french" omitted - too common in real titles)
        "multi", "dual", "dualaud", "truefrench", "vff", "vfi", "vf2", "vfq",
        "vost", "vostfr", "vof", "dubbed", "subbed", "korsub", "latino", "castellano",
        // Subtitle
        "multisubs", "multisub", "hardsub", "hardcoded", "softsub",
        "fansub", "fastsub", "subforced",
        // Edition / release flags (note: "final" omitted - too common in real titles)
        "proper", "repack", "rerip", "extended", "unrated", "uncut", "directors", "theatrical",
        "remastered", "remaster", "criterion", "limited", "internal",
        "redux", "restored", "hybrid", "mhd", "custom", "readnfo", "anniversary",
        "v2", "v3", "v4", "uncensored", "censored", "fanres", "fanedit",
        "obfuscated", "convert", "preair", "extras", "bonus", "featurettes",
        // French tags for complete series / integrals
        "integral", "integrale", "complete",
        // Frame rate
        "hfr", "24fps", "25fps", "30fps", "48fps", "60fps", "120fps",
        // Edition / other
        "untouched", "colorized", "samplefix",
        // Fix / misc
        "sample", "nfofix", "dirfix", "subfix", "syncfix",
        "nuked", "commentary", "fullscreen", "widescreen", "ws",
        "ntsc", "pal"
    };

    /// <summary>
    /// Normalises a title for loose comparison: lowercases, drops non-whitespace punctuation
    /// that sits between two alphanumeric characters (apostrophes, hyphens in compound words),
    /// and collapses all other non-alphanumeric characters to a single space.
    /// Examples: "Show Name" -> "show name", "Title (Year)" -> "title year"
    /// </summary>
    internal static string NormalizeTitleForMatch(string title)
    {
        // NFD decomposition splits accented chars into base + combining mark (e.g. î -> i + ◌̂).
        // The intra-word punctuation rule below then drops combining marks, stripping all accents.
        title = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(title.Length);
        bool lastWasSpace = true; // start true to avoid a leading space
        for (int i = 0; i < title.Length; i++)
        {
            var c = title[i];
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!char.IsWhiteSpace(c)
                     && i > 0 && i < title.Length - 1
                     && char.IsLetterOrDigit(title[i - 1])
                     && char.IsLetterOrDigit(title[i + 1]))
            {
                // Non-whitespace punctuation flanked by word chars: drop entirely.
                // Keeps lastWasSpace unchanged so no separator is inserted.
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats a TMDB title and year into a Plex-compliant name: <c>Title (Year)</c>, sanitised for use as a file or folder name.</summary>
    public static string FormatPlexName(string title, int? year) =>
        SanitizeFileName(year.HasValue ? $"{title} ({year.Value})" : title);

    // Cached set of invalid filename characters for O(1) lookup in SanitizeFileName
    private static readonly HashSet<char> _invalidFileNameChars = new(Path.GetInvalidFileNameChars());

    /// <summary>Strips characters that are invalid in file names and collapses runs of spaces. Replaces <c>:</c> with <c> -</c> to preserve subtitle separators.</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length + 2); // +2 for potential colon expansion
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == ':') { sb.Append(" -"); continue; }
            sb.Append(_invalidFileNameChars.Contains(c) ? ' ' : c);
        }
        return MultiSpaceRegex().Replace(sb.ToString(), " ").Trim().TrimEnd('-').Trim();
    }

    /// <summary>Returns true if the file has a recognized video extension.</summary>
    public static bool IsVideoFile(string path) =>
        _videoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Returns true if the file has a subtitle extension recognized by Plex (.srt, .sub, .ass, etc.).</summary>
    public static bool IsSubtitleFile(string path) =>
        _subtitleExtensions.Contains(Path.GetExtension(path));

    /// <summary>Returns true if the filename contains a TV episode pattern (SxxExx or NxNN).</summary>
    public static bool IsTvShowEpisode(string name) =>
        TvShowEpisodeRegex().IsMatch(name) || TvShowEpisodeLegacyRegex().IsMatch(name);

    /// <summary>
    /// Returns true if the name looks like a TV show - either an individual episode (SxxExx or NxNN)
    /// or a season pack in SxxExx format (S01, S02, etc. without an episode number).
    /// </summary>
    public static bool IsTvShow(string name) =>
        IsTvShowEpisode(name) || TvShowSeasonOnlyRegex().IsMatch(name);

    /// <summary>Returns true if the file is a video file containing a TV episode pattern.</summary>
    public static bool IsVideoTvShowEpisode(string path) =>
        IsVideoFile(path) && IsTvShowEpisode(Path.GetFileName(path));

    // Returns the filename without its extension if it is a recognized video file; otherwise returns the name unchanged.
    private static string StripVideoExtension(string name) =>
        _videoExtensions.Contains(Path.GetExtension(name)) ? Path.GetFileNameWithoutExtension(name) : name;

    /// <summary>Parses a TV episode filename into show name, season, and episode number. Returns null if no episode pattern is found or if no usable show name could be extracted.</summary>
    public static TvShowEpisodeInfo? ParseTvShowEpisode(string name)
    {
        name = StripVideoExtension(name);
        name = StripSitePrefix(name);

        var match = TvShowEpisodeRegex().Match(name);
        if (!match.Success)
            match = TvShowEpisodeLegacyRegex().Match(name);
        if (!match.Success)
            return null;

        var rawTitle = name[..match.Index];
        rawTitle = StripLanguageSuffix(rawTitle);
        rawTitle = rawTitle.Replace('.', ' ').Replace('_', ' ').Trim();
        rawTitle = CutAtTokens(rawTitle);

        var year = TryStripTrailingYear(ref rawTitle);

        rawTitle = CleanTitle(rawTitle);

        // No usable show name before the episode pattern (e.g. filename starts directly with S01E01)
        if (string.IsNullOrWhiteSpace(rawTitle))
            return null;

        // The regex captures season/episode as digit groups, but TryParse can still fail on
        // overflow (an absurdly long number); treat any parse failure as "not an episode".
        if (!int.TryParse(match.Groups[1].Value, out int season) || !int.TryParse(match.Groups[2].Value, out int episode))
            return null;
        int? endEpisode = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out int ep2) ? ep2 : null;

        // Season or episode 0 is not a valid episode (e.g. S00E00 matches the regex but is not importable)
        if (season == 0 || episode == 0)
            return null;

        return new TvShowEpisodeInfo(
            ShowName: rawTitle,
            Year: year,
            Season: season,
            Episode: episode,
            EndEpisode: endEpisode);
    }

    /// <summary>
    /// Extracts a probable title and optional release year from a filename or folder name.
    /// Generic: used for movies, TV show folder names, and any other <c>Title (Year)</c> pattern.
    /// </summary>
    public static (string Title, int? Year) ParseTitleYear(string name)
    {
        name = StripVideoExtension(name);
        name = StripSitePrefix(name);
        name = StripLanguageSuffix(name);

        // Try explicit year in parentheses first: "Movie Name (2009)" or "(2000) Title..."
        var result = TryParseYearInParens(name);
        if (result is not null)
            return result.Value;

        // Fallback: standalone year detection with two-pass cutoff strategy
        var cleaned = name.Replace('.', ' ').Replace('_', ' ');
        var rawTitle = FindStandaloneYear(cleaned, out int? parsedYear);

        if (parsedYear is not null)
        {
            // Year found: strip cutoff tokens only from the pre-year title portion
            // (avoids cutting the year itself when edition tokens appear before it)
            rawTitle = CutAtTokens(rawTitle);
        }
        else
        {
            // No year: strip cutoff tokens from full string, then retry year detection
            cleaned = CutAtTokens(cleaned);
            rawTitle = FindStandaloneYear(cleaned, out parsedYear);
        }

        rawTitle = CleanTitle(rawTitle);

        return (rawTitle, parsedYear);
    }

    // Attempts to extract title and year from a parenthesized year pattern.
    // Handles "Title (2009)" and leading "(2000) Title..." formats.
    private static (string Title, int? Year)? TryParseYearInParens(string name)
    {
        var match = YearInParensRegex().Match(name);
        if (!match.Success)
            return null;

        int? year = int.TryParse(match.Groups[1].Value, out int y) ? y : null;

        // Standard case: "Title (2009)" - title appears before the year
        var titleBefore = CleanTitle(name[..match.Index].Trim());
        if (!string.IsNullOrWhiteSpace(titleBefore))
            return (titleBefore, year);

        // Leading year case: "(2000) Title..." - year at start, title follows
        if (match.Index != 0)
            return null;

        var rest = name[match.Length..].Trim();
        if (string.IsNullOrWhiteSpace(rest))
            return null;

        var restCleaned = rest.Replace('.', ' ').Replace('_', ' ');
        restCleaned = CutAtTokens(restCleaned);
        var restTitle = CleanTitle(restCleaned);

        return string.IsNullOrWhiteSpace(restTitle) ? null : (restTitle, year);
    }

    private static string StripSitePrefix(string name)
    {
        var match = SitePrefixRegex().Match(name);
        // Safe to use match.Length as start index because SitePrefixRegex is anchored to ^
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
    // Guard: does not strip the year when it IS the entire title (e.g. "1883").
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

        // Try bare year at end-of-string: "Title 2018"
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
    // Handles three special cases:
    //   1) Year-as-title with second year:  "1917 2019"       -> title "1917",              year 2019
    //   2) Leading year with no second year: "2008 The Title"  -> title "The Title",         year 2008
    //   3) Back-to-back years:               "Title 2049 2017" -> title "Title 2049",        year 2017
    // Returns the title portion; sets parsedYear to null if no year was found.
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
            // Year at position 0: title before it is empty
            var next = StandaloneYearRegex().Match(cleaned, yearMatch.Index + yearMatch.Length);
            if (next.Success)
            {
                // Case 1: second year exists - advance so the first year becomes the title
                yearMatch = next;
            }
            else
            {
                // Case 2: no second year - use text after the year as the title
                parsedYear = int.TryParse(yearMatch.Value, out int y2) ? y2 : (int?)null;
                return cleaned[(yearMatch.Index + yearMatch.Length)..];
            }
        }
        else
        {
            // Title before year is non-empty - check for back-to-back years
            var next = StandaloneYearRegex().Match(cleaned, yearMatch.Index + yearMatch.Length);
            if (next.Success && string.IsNullOrWhiteSpace(cleaned[(yearMatch.Index + yearMatch.Length)..next.Index]))
            {
                // Case 3: two years with only whitespace between - first is part of title,
                // second is the release year (e.g. "Title 2049 2017")
                yearMatch = next;
            }
        }

        parsedYear = int.TryParse(yearMatch.Value, out int y) ? y : (int?)null;
        return cleaned[..yearMatch.Index];
    }

    // Walks the whitespace-split words and returns everything before the first cutoff token.
    // Handles "Token-Group" compounds by checking the prefix before the first hyphen.
    private static string CutAtTokens(string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var word in words)
        {
            if (_cutoffTokens.Contains(word))
                break;

            // Compound naming: "Token-Suffix" (e.g. a cutoff token followed by a hyphen and trailing text)
            // Check the prefix before the first hyphen against cutoff tokens
            var dashIndex = word.IndexOf('-');
            if (dashIndex > 0 && _cutoffTokens.Contains(word[..dashIndex]))
                break;

            result.Add(word);
        }
        return string.Join(' ', result);
    }

    // Final title cleanup: strips curly/square bracket tags, collapses whitespace,
    // removes trailing incomplete parenthetical groups, and trims orphan punctuation.
    private static string CleanTitle(string title)
    {
        title = title.Trim(' ', '-', '.', '_');
        title = CurlyBraceTagRegex().Replace(title, "").Trim();
        title = SquareBracketTagRegex().Replace(title, "").Trim();
        title = MultiSpaceRegex().Replace(title, " ");
        title = title.Trim();

        // Strip trailing incomplete parenthetical group: "Title (junk stuff" -> "Title"
        var openParen = title.LastIndexOf('(');
        if (openParen >= 0 && !title[openParen..].Contains(')'))
        {
            var trimmed = title[..openParen].TrimEnd();
            if (!string.IsNullOrEmpty(trimmed))
                title = trimmed;
        }

        // Strip orphan trailing brackets left after tag/year removal (e.g. "Title [", "Title (")
        return title.TrimEnd(' ', '-', '.', '_', '[', '(');
    }

    // Matches a 4-digit year wrapped in parentheses: "(2009)", captures the year
    [GeneratedRegex(@"\((\d{4})\)")]
    private static partial Regex YearInParensRegex();

    // Matches a 4-digit year (1900-2099) as a standalone word: "2009" but not "x2009" or "20091"
    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex StandaloneYearRegex();

    // Matches content inside curly braces: "{info}" (used for tag removal)
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex CurlyBraceTagRegex();

    // Matches content inside square brackets: "[info]" (used for tag removal)
    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex SquareBracketTagRegex();

    // Matches two or more consecutive whitespace characters (used for collapsing)
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    // Anchored to start. Two alternatives:
    //   1) Bracketed site tag:  [www.SiteName.com]  (TLD 2-3 chars)
    //   2) Bare www prefix:     www.SiteName.com -  (TLD 2-5 chars, followed by a dash separator)
    // Also handles ww. and www, (non-standard dot separators)
    //
    // The [--] class below holds three characters: hyphen, en dash and em dash. The em dash is the
    // repository's only one and it is deliberate - this is a pattern the parser *reads*, not prose it
    // writes, so the project-wide "no em dashes" rule does not apply here any more than the "no media
    // titles" rule applies to the codec tokens in _cutoffTokens. A sweep that strips it would stop the
    // parser recognising the real filenames that use an em dash as the separator. Leave all three.
    [GeneratedRegex(@"^(?:\[\s*[^\]]*\.[a-z]{2,3}\s*\]\s*|ww[w]?[.,][\w.-]+\.[a-z]{2,5}\s*[-–—]\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex SitePrefixRegex();

    // Matches underscore-prefixed language codes at end of string: _VOSTFR, _VF-EN, _VO-FR, etc.
    [GeneratedRegex(@"[_]((?:FR|EN|VF|VO)[-]?(?:FR|EN|VF|VO)?(?:[-](?:FR|EN|VF|VO))*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageSuffixRegex();

    // Primary TV pattern: SxxExx / S1E1 / S004E111, captures season, first episode, and optional end episode.
    // Handles multi-episode files: S01E01E02 (glued) and S01E01-E02 (hyphen-separated).
    // Group 1 = season, Group 2 = first episode, Group 3 = end episode (optional).
    [GeneratedRegex(@"S(\d{1,4})E(\d{1,4})(?:-?E(\d{1,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex TvShowEpisodeRegex();

    // Legacy TV pattern: 1x01 notation used by older releases, captures season and episode.
    // No leading \b so it matches even when glued to a word (e.g. "Show1x01").
    [GeneratedRegex(@"(\d{1,2})x(\d{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex TvShowEpisodeLegacyRegex();

    // Season-only TV pattern: S01, S2, S004, etc. without an episode number.
    // Matches season packs and complete-season folders. Uses \b on both sides so "S01" in
    // "S01E01" does NOT match (\b requires a word/non-word boundary and "1E" are both word chars).
    [GeneratedRegex(@"\bS\d{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex TvShowSeasonOnlyRegex();

    // Anchored to start AND end so it does not match partial folder names like "Season Finale".
    // Covers English, French, Spanish, Italian. Leading zeros are stripped via 0* before the capture group.
    [GeneratedRegex(@"^(?:season|saison|temporada|stagione|s)\s*0*(\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    // Matches a 1-3 digit number at the start of a filename, followed by either a separator
    // (space, dash, dot, underscore) or end-of-string. Leading zeros are absorbed by 0* so
    // "01-Title" captures 1, "001_Title" captures 1, and a bare "01" captures 1.
    // The 1-3 digit cap on (\d{1,3}) prevents matching 4+ digit prefixes like a release year
    // (e.g. "2020.Movie" cannot reach a separator at any backtrack position).
    [GeneratedRegex(@"^0*(\d{1,3})(?:[\s\-_.]|$)")]
    private static partial Regex EpisodePrefixRegex();

    /// <summary>Returns the season number when <paramref name="folderName"/> matches a season indicator pattern (e.g. "Season 1", "saison 01", "S01"), or <see langword="null"/> otherwise.</summary>
    public static int? ParseSeasonFromFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var match = SeasonFolderRegex().Match(folderName.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out int n) && n > 0 ? n : null;
    }

    /// <summary>Returns the episode number from a 1-3 digit prefix at the start of <paramref name="fileName"/> (e.g. "01-Title.mkv" -> 1), or <see langword="null"/> if no prefix is present.</summary>
    public static int? ParseEpisodePrefix(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var match = EpisodePrefixRegex().Match(fileName);
        return match.Success && int.TryParse(match.Groups[1].Value, out int n) && n > 0 ? n : null;
    }
}

/// <summary>Parsed TV episode identity: show name, optional year hint, season number, first episode number, and optional end episode for multi-episode files.</summary>
public sealed record TvShowEpisodeInfo(string ShowName, int? Year, int Season, int Episode, int? EndEpisode = null);

/// <summary>TV episode identity resolved from directory structure rather than filename pattern (e.g. <c>Show\Season 01\01-Title.mkv</c>). <see cref="ShowName"/> comes from the grandparent folder, <see cref="Season"/> from the parent folder, <see cref="Episode"/> from the leading digits of the filename.</summary>
public sealed record FolderClassifiedEpisode(string FilePath, string ShowName, int Season, int Episode);
