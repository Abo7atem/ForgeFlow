using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeFlow
{
    public sealed class UpdateChecker : IDisposable
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/Abo7atem/ForgeFlow/releases/latest";
        private readonly HttpClient _httpClient;

        public UpdateChecker()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeFlow-Lite");
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            using var response = await _httpClient.GetAsync(GitHubApiUrl).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (release == null || release.Prerelease)
                return null;

            Version current = GetCurrentVersion();
            Version latest = ParseVersion(release.TagName ?? release.Name ?? "");
            if (latest <= current)
                return null;

            string reason = GetMandatoryReason(release.Body ?? "", current);
            return new UpdateInfo
            {
                CurrentVersion = current.ToString(),
                LatestVersion = latest.ToString(),
                ReleaseName = release.Name ?? release.TagName ?? "Latest release",
                ReleaseNotes = release.Body ?? "",
                PublishedDate = release.PublishedAt,
                DownloadUrl = GetDownloadUrl(release),
                IsMandatory = true,
                MandatoryReason = reason
            };
        }

        public static Version GetCurrentVersion()
        {
            string[] paths =
            {
                Path.Combine(AppContext.BaseDirectory, "version.txt"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "version.txt")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "version.txt"))
            };

            foreach (string path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string text = File.ReadAllText(path).Trim().TrimStart('\uFEFF');
                    if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        text = text[1..];
                    if (Version.TryParse(text, out var version))
                        return version;
                }
                catch { }
            }

            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        }

        public static Version ParseVersion(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
                return new Version(1, 0, 0);

            string text = versionText.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                text = text[1..];

            string clean = text.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
            return Version.TryParse(clean, out var version) ? version : new Version(1, 0, 0);
        }

        private static string GetMandatoryReason(string releaseNotes, Version currentVersion)
        {
            if (TryReadMinimumSupportedVersion(releaseNotes, out Version? minimum) && minimum != null && currentVersion < minimum)
                return $"Current version is older than minimum supported version {minimum}.";

            if (releaseNotes.Contains("mandatory=true", StringComparison.OrdinalIgnoreCase) ||
                releaseNotes.Contains("force_update=true", StringComparison.OrdinalIgnoreCase) ||
                releaseNotes.Contains("[mandatory]", StringComparison.OrdinalIgnoreCase))
                return "Release is marked as mandatory.";

            return "A newer version is available and must be installed before using ForgeFlow Lite.";
        }

        private static bool TryReadMinimumSupportedVersion(string releaseNotes, out Version? version)
        {
            version = null;
            foreach (string raw in releaseNotes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                string[] prefixes = { "min_supported_version", "minimum_supported_version", "minimum_version", "min_version" };
                foreach (string prefix in prefixes)
                {
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int separator = line.IndexOfAny(new[] { '=', ':' });
                    if (separator < 0 || separator >= line.Length - 1)
                        continue;

                    version = ParseVersion(line[(separator + 1)..].Trim().Trim('`', '"', '\''));
                    return true;
                }
            }

            return false;
        }

        private static string GetDownloadUrl(GitHubRelease release)
        {
            var asset = release.Assets?
                .OrderByDescending(a => IsInstallerAsset(a.Name))
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));

            return asset?.BrowserDownloadUrl ?? release.HtmlUrl ?? "https://github.com/Abo7atem/ForgeFlow/releases/latest";
        }

        private static bool IsInstallerAsset(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.EndsWith(".exe") || lower.EndsWith(".msi") || lower.EndsWith(".zip");
        }

        public void Dispose() => _httpClient.Dispose();
    }

    public sealed class UpdateInfo
    {
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string ReleaseName { get; init; } = "";
        public string ReleaseNotes { get; init; } = "";
        public DateTime PublishedDate { get; init; }
        public string DownloadUrl { get; init; } = "";
        public bool IsMandatory { get; init; }
        public string MandatoryReason { get; init; } = "";
    }

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
