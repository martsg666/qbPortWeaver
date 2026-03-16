using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver
{
    /// <summary>HTTP client for The Movie Database (TMDB) search API.</summary>
    public sealed class TmdbClient
    {
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3/"; // NOSONAR S1075 - fixed TMDB API endpoint, not a configurable path

        // Static shared instance: HttpClient is thread-safe and reusing it avoids per-cycle socket exhaustion.
        private static readonly HttpClient _httpClient = new()
        {
            BaseAddress = new Uri(TmdbBaseUrl),
            Timeout     = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds)
        };

        private readonly string _apiKey;

        public TmdbClient(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>Searches for a movie by title and optional year. Returns the best match, or null if none found.</summary>
        public async Task<MovieInfo?> SearchMovieAsync(string query, int? year = null)
        {
            var url = $"search/movie?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1";
            if (year.HasValue)
                url += $"&year={year.Value}";

            var response = await _httpClient.GetFromJsonAsync<TmdbMovieSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result == null)
                return null;

            int? releaseYear = result.ReleaseDate?.Length >= 4 && int.TryParse(result.ReleaseDate[..4], out int y) ? y : null;
            return new MovieInfo(result.Title, releaseYear, result.Id);
        }

        /// <summary>Searches for a TV show by title and optional first-air year. Returns the best match, or null if none found.</summary>
        public async Task<TvShowInfo?> SearchTvShowAsync(string query, int? year = null)
        {
            var url = $"search/tv?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1";
            if (year.HasValue)
                url += $"&first_air_date_year={year.Value}";
            var response = await _httpClient.GetFromJsonAsync<TmdbTvSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result == null)
                return null;

            int? airYear = result.FirstAirDate?.Length >= 4 && int.TryParse(result.FirstAirDate[..4], out int y) ? y : null;
            return new TvShowInfo(result.Name, airYear, result.Id);
        }
    }

    /// <summary>TMDB title, release year, and database ID for a movie.</summary>
    public sealed record MovieInfo(string Title, int? Year, int TmdbId);

    /// <summary>TMDB title, first-air year, and database ID for a TV show.</summary>
    public sealed record TvShowInfo(string Title, int? Year, int TmdbId);

    // TMDB API response shapes — internal to TmdbClient deserialization
    public sealed record TmdbMovieSearchResult(
        [property: JsonPropertyName("results")] List<TmdbMovie>? Results);

    public sealed record TmdbMovie(
        [property: JsonPropertyName("id")]           int     Id,
        [property: JsonPropertyName("title")]        string  Title,
        [property: JsonPropertyName("release_date")] string? ReleaseDate);

    public sealed record TmdbTvSearchResult(
        [property: JsonPropertyName("results")] List<TmdbTvShow>? Results);

    public sealed record TmdbTvShow(
        [property: JsonPropertyName("id")]             int     Id,
        [property: JsonPropertyName("name")]           string  Name,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate);
}
