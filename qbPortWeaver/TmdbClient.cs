using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver
{
    /// <summary>HTTP client for The Movie Database (TMDB) search API.</summary>
    public sealed class TmdbClient : IDisposable
    {
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3/";

        private readonly HttpClient _http;
        private readonly string _apiKey;

        public TmdbClient(string apiKey)
        {
            _apiKey = apiKey;
            _http   = new HttpClient { BaseAddress = new Uri(TmdbBaseUrl) };
        }

        /// <summary>Searches for a movie by title and optional year. Returns the best match, or null if none found.</summary>
        public async Task<MovieInfo?> SearchMovieAsync(string query, int? year = null)
        {
            var url = $"search/movie?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1";
            if (year.HasValue)
                url += $"&year={year.Value}";

            var response = await _http.GetFromJsonAsync<TmdbSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result == null)
                return null;

            return new MovieInfo
            {
                Title  = result.Title,
                Year   = result.ReleaseDate?.Length >= 4 && int.TryParse(result.ReleaseDate[..4], out int releaseYear) ? releaseYear : null,
                TmdbId = result.Id
            };
        }

        /// <summary>Searches for a TV show by title. Returns the best match, or null if none found.</summary>
        public async Task<TvShowInfo?> SearchTvShowAsync(string query)
        {
            var url      = $"search/tv?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1";
            var response = await _http.GetFromJsonAsync<TmdbTvSearchResult>(url).ConfigureAwait(false);
            var result   = response?.Results?.FirstOrDefault();
            if (result == null)
                return null;

            return new TvShowInfo
            {
                Title  = result.Name,
                Year   = result.FirstAirDate?.Length >= 4 && int.TryParse(result.FirstAirDate[..4], out int airYear) ? airYear : null,
                TmdbId = result.Id
            };
        }

        public void Dispose()
        {
            _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public class MovieInfo
    {
        public string  Title            { get; set; } = "";
        public int?    Year             { get; set; }
        public int     TmdbId          { get; set; }
    }

    public class TvShowInfo
    {
        public string Title  { get; set; } = "";
        public int?   Year   { get; set; }
        public int    TmdbId { get; set; }
    }

    public class TmdbSearchResult
    {
        [JsonPropertyName("results")]
        public List<TmdbMovie>? Results { get; set; }
    }

    public class TmdbMovie
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }
    }

    public class TmdbTvSearchResult
    {
        [JsonPropertyName("results")]
        public List<TmdbTvShow>? Results { get; set; }
    }

    public class TmdbTvShow
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }
    }
}
