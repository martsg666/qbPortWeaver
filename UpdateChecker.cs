using System.Net.Http.Headers;
using System.Text.Json;

namespace qbPortWeaver
{
    public static class UpdateChecker
    {
        private const string JSON_HTML_URL_ELEMENT = "html_url";
        private const string JSON_HTML_TAG_ELEMENT = "tag_name";
        private const int HTTP_TIMEOUT_SECONDS = 10;

        private static readonly string GITHUB_BASE_API_URL  = $"https://api.github.com/repos/{AppConstants.GITHUB_REPO_OWNER}/{AppConstants.APP_NAME}";
        private static readonly string GITHUB_API_URL       = GITHUB_BASE_API_URL + "/releases/latest";

        public record LatestReleaseInfo(string TagName, string ReleaseUrl, bool IsNewer);
        public record ContributorInfo(string Login, string ProfileUrl);

        // Returns the latest release tag and URL if a newer version exists on GitHub; null if up-to-date or on any error
        public static async Task<(string Version, string Url)?> CheckForUpdateAsync()
        {
            var info = await GetLatestReleaseInfoAsync();
            return info?.IsNewer == true ? (info.TagName, info.ReleaseUrl) : null;
        }

        // Returns full release info from GitHub including whether a newer version exists; null on any error
        public static async Task<LatestReleaseInfo?> GetLatestReleaseInfoAsync()
        {
            try
            {
                using var client = CreateHttpClient(AppConstants.APP_VERSION);

                using var response = await client.GetAsync(GITHUB_API_URL);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc    = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                if (!root.TryGetProperty(JSON_HTML_TAG_ELEMENT, out var tagElement) ||
                    !root.TryGetProperty(JSON_HTML_URL_ELEMENT, out var urlElement))
                    return null;

                string tagName = tagElement.GetString() ?? "";
                string htmlUrl = urlElement.GetString() ?? "";

                // Strip leading 'v' or 'V' from the tag (e.g. "v2.1.0" → "2.1.0") before parsing
                string versionString = tagName.TrimStart('v', 'V');
                bool isNewer = Version.TryParse(versionString, out var latest) &&
                               Version.TryParse(AppConstants.APP_VERSION, out var current) &&
                               latest > current;

                return new LatestReleaseInfo(tagName, htmlUrl, isNewer);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetLatestReleaseInfoAsync: {ex.Message}");
                return null;
            }
        }

        // Returns all unique human contributors to the repo. Bots are excluded.
        // Returns an empty list on any error.
        public static async Task<IReadOnlyList<ContributorInfo>> GetReleaseContributorsAsync()
        {
            try
            {
                using var client = CreateHttpClient(AppConstants.APP_VERSION);

                using var response = await client.GetAsync(GITHUB_BASE_API_URL + "/contributors?per_page=100");
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc    = await JsonDocument.ParseAsync(stream);

                var seen         = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var contributors = new List<ContributorInfo>();

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string login = item.TryGetProperty("login",              out var loginEl) ? loginEl.GetString() ?? "" : "";
                    string url   = item.TryGetProperty(JSON_HTML_URL_ELEMENT, out var urlEl)   ? urlEl.GetString()   ?? "" : "";
                    string type  = item.TryGetProperty("type",               out var typeEl)  ? typeEl.GetString()  ?? "" : "";

                    if (string.IsNullOrEmpty(login)) continue;
                    if (IsBot(login, type)) continue;
                    if (!seen.Add(login)) continue;

                    contributors.Add(new ContributorInfo(login, url));
                }

                // Always list the repo owner first
                int ownerIndex = contributors.FindIndex(c => c.Login.Equals(AppConstants.GITHUB_REPO_OWNER, StringComparison.OrdinalIgnoreCase));
                if (ownerIndex > 0)
                {
                    var owner = contributors[ownerIndex];
                    contributors.RemoveAt(ownerIndex);
                    contributors.Insert(0, owner);
                }

                return contributors;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogDebug($"UpdateChecker.GetReleaseContributorsAsync: {ex.Message}");
                return [];
            }
        }

        private static bool IsBot(string login, string type) =>
            type.Equals("Bot", StringComparison.OrdinalIgnoreCase) ||
            login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

        // Creates an HttpClient pre-configured with the required User-Agent and timeout
        private static HttpClient CreateHttpClient(string version)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppConstants.APP_NAME, version));
            return client;
        }


    }
}
