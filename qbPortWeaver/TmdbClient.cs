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
            return new MovieInfo(result.Title, releaseYear, result.Id);
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
            return new TvShowInfo(result.Name, airYear, result.Id);
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

    /// <summary>TMDB title, release year, and database ID for a movie.</summary>
    public sealed record MovieInfo(string Title, int? Year, int TmdbId);

    /// <summary>TMDB title, first-air year, and database ID for a TV show.</summary>
    public sealed record TvShowInfo(string Title, int? Year, int TmdbId);

    // TMDB API response shapes (internal - only used for deserialization)
    internal sealed record TmdbMovieSearchResult(
        [property: JsonPropertyName("results")] List<TmdbMovie>? Results);

    internal sealed record TmdbMovie(
        [property: JsonPropertyName("id")]           int     Id,
        [property: JsonPropertyName("title")]        string  Title,
        [property: JsonPropertyName("release_date")] string? ReleaseDate);

    internal sealed record TmdbTvSearchResult(
        [property: JsonPropertyName("results")] List<TmdbTvShow>? Results);

    internal sealed record TmdbTvShow(
        [property: JsonPropertyName("id")]             int     Id,
        [property: JsonPropertyName("name")]           string  Name,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate);
}
