using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver
{
    /// <summary>HTTP client for The Movie Database (TMDB) search API. Applies a per-request delay to stay within TMDB's rate limit (~40 requests/10 seconds).</summary>
    public sealed class TmdbClient
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

        private readonly string _apiKey;

        public TmdbClient(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>Searches for a movie by title and optional year. Returns the best match, or null if none found.</summary>
        public async Task<MovieInfo?> SearchMovieAsync(string query, int? year = null)
        {
            var url = $"search/movie?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1"; // NOSONAR S4790 - TMDB API v3 requires the key as a query parameter; transmitted over HTTPS only
            if (year.HasValue)
                url += $"&year={year.Value}";

            var response = await GetWithRateLimitAsync<TmdbMovieSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result is null)
                return null;

            int? releaseYear = result.ReleaseDate?.Length >= 4 && int.TryParse(result.ReleaseDate[..4], out int y) ? y : null;
            return new MovieInfo(result.Title, releaseYear, result.Id, result.VoteCount);
        }

        /// <summary>Searches for a TV show by title and optional first-air year. Returns the best match, or null if none found.</summary>
        public async Task<TvShowInfo?> SearchTvShowAsync(string query, int? year = null)
        {
            var url = $"search/tv?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1"; // NOSONAR S4790 - TMDB API v3 requires the key as a query parameter; transmitted over HTTPS only
            if (year.HasValue)
                url += $"&first_air_date_year={year.Value}";

            var response = await GetWithRateLimitAsync<TmdbTvSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result is null)
                return null;

            int? airYear = result.FirstAirDate?.Length >= 4 && int.TryParse(result.FirstAirDate[..4], out int y) ? y : null;
            return new TvShowInfo(result.Name, airYear, result.Id, result.VoteCount);
        }

        /// <summary>
        /// Searches TMDB with confidence tracking: primary search, no-year confidence check,
        /// retry without year, and fallback strategies (after-dash, trailing-number).
        /// Shared by both processors and the re-check UI to avoid duplicating lookup logic.
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
                isConfident = FileNameParser.IsStrongNoYearMatch(title, getTitle(info), getVoteCount(info));

            // Retry without year: parsed year may not match TMDB's release/first-air year
            if (info is null && year.HasValue)
            {
                info = await search(title, null).ConfigureAwait(false);
                if (info is not null) isConfident = false;
            }

            (info, isConfident) = await FileNameParser.TryFallbackLookupsAsync(
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
                try { await Task.Delay(RateLimitDelayMs).ConfigureAwait(false); }
                finally { _rateLimiter.Release(); }
            }
        }
    }

    /// <summary>TMDB title, release year, database ID, and vote count for a movie.</summary>
    public sealed record MovieInfo(string Title, int? Year, int TmdbId, int VoteCount = 0);

    /// <summary>TMDB title, first-air year, database ID, and vote count for a TV show.</summary>
    public sealed record TvShowInfo(string Title, int? Year, int TmdbId, int VoteCount = 0);

    // TMDB API response shapes (internal - only used for deserialization)
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
