using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver
{
    /// <summary>HTTP client for The Movie Database (TMDB) search API. Applies a per-request delay to stay within TMDB's rate limit (~40 requests/10 seconds).</summary>
    public sealed class TmdbClient(string apiKey)
    {
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3/"; // NOSONAR S1075 - fixed TMDB API endpoint, not a configurable path
        private const int    RateLimitDelayMs = 260; // ~3.8 req/s, comfortably under TMDB's ~4 req/s limit

        // Static shared instance: HttpClient is thread-safe and reusing it avoids per-cycle socket exhaustion.
        private static readonly HttpClient _httpClient = new()
        {
            BaseAddress = new Uri(TmdbBaseUrl),
            Timeout     = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds)
        };

        // Serialises concurrent requests to enforce the rate limit
        private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

        /// <summary>Searches for a movie by title and optional year. Returns the best match, or null if none found.</summary>
        public async Task<MovieInfo?> SearchMovieAsync(string query, int? year = null)
        {
            var url = $"search/movie?api_key={apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1"; // NOSONAR S4790 - TMDB API v3 requires the key as a query parameter; transmitted over HTTPS only
            if (year.HasValue)
                url += $"&year={year.Value}";

            var response = await GetWithRateLimitAsync<TmdbMovieSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result is null)
                return null;

            return new MovieInfo(result.Title, ParseYearFromDate(result.ReleaseDate), result.Id, result.VoteCount);
        }

        /// <summary>Searches for a TV show by title and optional first-air year. Returns the best match, or null if none found.</summary>
        public async Task<TvShowInfo?> SearchTvShowAsync(string query, int? year = null)
        {
            var url = $"search/tv?api_key={apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1"; // NOSONAR S4790 - TMDB API v3 requires the key as a query parameter; transmitted over HTTPS only
            if (year.HasValue)
                url += $"&first_air_date_year={year.Value}";

            var response = await GetWithRateLimitAsync<TmdbTvSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result is null)
                return null;

            return new TvShowInfo(result.Name, ParseYearFromDate(result.FirstAirDate), result.Id, result.VoteCount);
        }

        // Extracts the year from a TMDB date string (format "YYYY-MM-DD"), or null if the string is missing or malformed.
        private static int? ParseYearFromDate(string? date) =>
            date?.Length >= 4 && int.TryParse(date[..4], out int y) ? y : null;

        /// <summary>
        /// Searches TMDB with confidence tracking: primary search, no-year confidence check,
        /// retry without year, and fallback strategies (after-dash, trailing-number).
        /// Shared by both processors and the re-match UI to avoid duplicating lookup logic.
        /// </summary>
        internal static async Task<(T? Info, bool IsConfident)> SearchWithConfidenceAsync<T>(
            string title, int? year,
            Func<string, int?, Task<T?>> search,
            Func<T, bool> hasYear,
            Func<T, string> getTitle,
            Func<T, int> getVoteCount) where T : class
        {
            bool isConfident = true;

            var info = await search(title, year).ConfigureAwait(false);

            // Without a year in the filename we cannot corroborate the match by year alone.
            // Require an exact title match and a meaningful vote count to stay confident.
            if (info is not null && !year.HasValue)
                isConfident = IsStrongNoYearMatch(title, getTitle(info), getVoteCount(info));

            // Retry without year: parsed year may not match TMDB's release/first-air year
            if (info is null && year.HasValue)
            {
                info = await search(title, null).ConfigureAwait(false);
                if (info is not null) isConfident = false;
            }

            (info, isConfident) = await TryFallbackLookupsAsync(
                title, year, info, isConfident, search, hasYear).ConfigureAwait(false);

            return (info, isConfident);
        }

        // Enforces a minimum delay between TMDB API calls to avoid HTTP 429 rate limiting.
        // The delay runs inside the semaphore hold so the next caller waits for the cooldown to finish.
        private static async Task<T?> GetWithRateLimitAsync<T>(string url)
        {
            await _rateLimiter.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _httpClient.GetFromJsonAsync<T>(url).ConfigureAwait(false);
            }
            finally
            {
                // Delay inside the semaphore hold so the next caller waits for the cooldown.
                // Inner finally ensures Release() runs even if Task.Delay is interrupted.
                try { await Task.Delay(RateLimitDelayMs).ConfigureAwait(false); }
                finally { _rateLimiter.Release(); }
            }
        }

        // Returns true when a TMDB result found without a year in the filename is a high-confidence match.
        // Requires a meaningful vote count and a normalised title match to filter out obscure or incorrect entries.
        private static bool IsStrongNoYearMatch(string searchedTitle, string returnedTitle, int voteCount, int minVoteCount = 50) =>
            voteCount >= minVoteCount &&
            string.Equals(FileNameParser.NormalizeTitleForMatch(returnedTitle), FileNameParser.NormalizeTitleForMatch(searchedTitle), StringComparison.OrdinalIgnoreCase);

        // Applies two fallback lookup strategies when the primary search fails or returns a low-confidence result.
        // After-dash strategy: retries with the part after " - " when info is null (no initial match).
        // Trailing-number strategy: retries without trailing digit when info is null OR hasYear returns false
        // (a year-less result is ambiguous; a stripped-title result that includes a year is higher quality).
        private static async Task<(T? Info, bool IsConfident)> TryFallbackLookupsAsync<T>(
            string title, int? year, T? info, bool isConfident,
            Func<string, int?, Task<T?>> search,
            Func<T, bool> hasYear) where T : class
        {
            var afterDash = info is null ? ExtractAfterDash(title) : null;
            if (afterDash is not null)
            {
                LogManager.Instance.LogDebug($"TmdbClient.TryFallbackLookupsAsync: Retrying with after-dash title '{afterDash}'", Subsystem.MediaManager);
                info = await search(afterDash, year).ConfigureAwait(false);
                if (info is not null) isConfident = false;
            }

            if (info is null || (!hasYear(info) && title.Length > 2))
            {
                var withoutNum = StripTrailingNumber(title);
                if (withoutNum is not null)
                {
                    LogManager.Instance.LogDebug($"TmdbClient.TryFallbackLookupsAsync: Retrying without trailing number '{withoutNum}'", Subsystem.MediaManager);
                    var altInfo = await search(withoutNum, year).ConfigureAwait(false);
                    if (altInfo is not null && hasYear(altInfo))
                    {
                        info        = altInfo;
                        isConfident = false;
                    }
                }
            }

            return (info, isConfident);
        }

        // Returns the substring after the first " - " separator, or null if not present.
        private static string? ExtractAfterDash(string title)
        {
            int idx = title.IndexOf(" - ", StringComparison.Ordinal);
            if (idx < 0) return null;
            var after = title[(idx + 3)..].Trim();
            return after.Length > 0 ? after : null;
        }

        // Strips a single trailing digit preceded by a space (e.g. "Title 2" -> "Title"), or null if not present.
        private static string? StripTrailingNumber(string title)
        {
            var trimmed = title.TrimEnd();
            if (trimmed.Length <= 2 || !char.IsDigit(trimmed[^1]) || trimmed[^2] != ' ')
                return null;
            return trimmed[..^2].Trim();
        }
    }

    /// <summary>TMDB title, release year, database ID, and vote count for a movie.</summary>
    public sealed record MovieInfo(string Title, int? Year, int TmdbId, int VoteCount = 0);

    /// <summary>TMDB title, first-air year, database ID, and vote count for a TV show.</summary>
    public sealed record TvShowInfo(string Title, int? Year, int TmdbId, int VoteCount = 0);

    // TMDB API response shapes - only used for deserialization
    internal sealed record TmdbMovieSearchResult(
        [property: JsonPropertyName("results")] List<TmdbMovie>? Results);

    internal sealed record TmdbMovie(
        [property: JsonPropertyName("id")]           int     Id,
        [property: JsonPropertyName("title")]        string  Title,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("vote_count")]   int     VoteCount = 0);

    internal sealed record TmdbTvSearchResult(
        [property: JsonPropertyName("results")] List<TmdbTvShow>? Results);

    internal sealed record TmdbTvShow(
        [property: JsonPropertyName("id")]             int     Id,
        [property: JsonPropertyName("name")]           string  Name,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("vote_count")]     int     VoteCount = 0);
}
