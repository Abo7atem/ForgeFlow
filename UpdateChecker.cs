using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeFlow
{
    public sealed class UpdateChecker : IDisposable
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/Abo7atem/ForgeFlowAPP/releases/latest";
        private const string GitHubLatestReleaseUrl = "https://github.com/Abo7atem/ForgeFlowAPP/releases/latest";
        private const string GitHubReleaseTagBaseUrl = "https://github.com/Abo7atem/ForgeFlowAPP/releases/tag/";
        private const string GitHubReleaseDownloadBaseUrl = "https://github.com/Abo7atem/ForgeFlowAPP/releases/download/";
        private const string StoreDownloadUrl = "https://4tiko.net";
        private readonly HttpClient _httpClient;

        public UpdateChecker()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeFlow-Lite");
            _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            _httpClient.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                return await CheckForUpdatesViaApiAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (IsRateLimitOrTransientGitHubError(ex.StatusCode))
            {
                return await CheckForUpdatesViaPublicRedirectAsync("GitHub API was temporarily limited. Used public release fallback.").ConfigureAwait(false);
            }
        }

        private async Task<UpdateInfo?> CheckForUpdatesViaApiAsync()
        {
            using var response = await _httpClient.GetAsync(GitHubApiUrl).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (IsRateLimitOrTransientGitHubError(response.StatusCode))
                return await CheckForUpdatesViaPublicRedirectAsync("GitHub API was temporarily limited. Used public release fallback.").ConfigureAwait(false);

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
            string reason = GetMandatoryReason(release.Body ?? "", current);
            if (IsAppDisabled(release.Body ?? "", current, out string disabledReason))
            {
                return new UpdateInfo
                {
                    CurrentVersion = current.ToString(),
                    LatestVersion = latest.ToString(),
                    ReleaseName = release.Name ?? release.TagName ?? "Latest release",
                    ReleaseNotes = release.Body ?? "",
                    PublishedDate = release.PublishedAt,
                    ReleaseUrl = StoreDownloadUrl,
                    DownloadUrl = StoreDownloadUrl,
                    IsMandatory = true,
                    IsBlocked = true,
                    MandatoryReason = disabledReason
                };
            }

            if (latest <= current)
                return null;

            GitHubAsset? asset = GetInstallerAsset(release);
            return new UpdateInfo
            {
                CurrentVersion = current.ToString(),
                LatestVersion = latest.ToString(),
                ReleaseName = release.Name ?? release.TagName ?? "Latest release",
                ReleaseNotes = release.Body ?? "",
                PublishedDate = release.PublishedAt,
                ReleaseUrl = StoreDownloadUrl,
                DownloadUrl = asset?.BrowserDownloadUrl ?? "",
                DownloadFileName = asset?.Name ?? "",
                DownloadSize = asset?.Size ?? 0,
                DownloadDigest = asset?.Digest ?? "",
                HasDirectInstaller = asset != null,
                IsMandatory = true,
                MandatoryReason = reason
            };
        }

        private async Task<UpdateInfo?> CheckForUpdatesViaPublicRedirectAsync(string fallbackReason)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestReleaseUrl);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("text/html");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            string? tag = TryReadReleaseTagFromUrl(response.RequestMessage?.RequestUri);
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            Version current = GetCurrentVersion();
            Version latest = ParseVersion(tag);
            if (latest <= current)
                return null;

            string cleanVersion = latest.ToString();
            string assetName = $"ForgeFlow_Lite_Setup_{cleanVersion}.exe";
            string releaseUrl = GitHubReleaseTagBaseUrl + "v" + cleanVersion;
            string downloadUrl = $"{GitHubReleaseDownloadBaseUrl}v{cleanVersion}/{assetName}";

            return new UpdateInfo
            {
                CurrentVersion = current.ToString(),
                LatestVersion = latest.ToString(),
                ReleaseName = "ForgeFlow Lite v" + cleanVersion,
                ReleaseNotes =
                    fallbackReason + Environment.NewLine + Environment.NewLine +
                    "mandatory=true" + Environment.NewLine +
                    "force_update=true" + Environment.NewLine +
                    "min_version=" + cleanVersion,
                PublishedDate = DateTime.UtcNow,
                ReleaseUrl = releaseUrl,
                DownloadUrl = downloadUrl,
                DownloadFileName = assetName,
                DownloadSize = 0,
                DownloadDigest = "",
                HasDirectInstaller = true,
                IsMandatory = true,
                MandatoryReason = fallbackReason
            };
        }

        private static bool IsRateLimitOrTransientGitHubError(HttpStatusCode? statusCode)
        {
            return statusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
        }

        private static string? TryReadReleaseTagFromUrl(Uri? uri)
        {
            if (uri == null)
                return null;

            string path = uri.AbsolutePath.TrimEnd('/');
            const string marker = "/releases/tag/";
            int index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            string tag = path[(index + marker.Length)..].Trim('/');
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
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

        private static bool IsAppDisabled(string releaseNotes, Version currentVersion, out string reason)
        {
            reason = "";
            foreach (string raw in releaseNotes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().Trim('`', '*', '-', ' ');
                if (line.Equals("disabled=true", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("app_disabled=true", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("block_all=true", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("[disabled]", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "This ForgeFlow Lite build has been disabled by the publisher.";
                    return true;
                }

                if (!line.StartsWith("blocked_versions", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("revoked_versions", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("disabled_versions", StringComparison.OrdinalIgnoreCase))
                    continue;

                int separator = line.IndexOfAny(new[] { '=', ':' });
                if (separator < 0 || separator >= line.Length - 1)
                    continue;

                string[] versions = line[(separator + 1)..]
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string versionText in versions)
                {
                    if (ParseVersion(versionText.Trim('`', '"', '\'')) == currentVersion)
                    {
                        reason = $"This ForgeFlow Lite version ({currentVersion}) has been disabled by the publisher.";
                        return true;
                    }
                }
            }

            return false;
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

        private static GitHubAsset? GetInstallerAsset(GitHubRelease release)
        {
            return release.Assets?
                .Where(a => GetInstallerAssetPriority(a.Name) > 0)
                .OrderByDescending(a => GetInstallerAssetPriority(a.Name))
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
        }

        private static int GetInstallerAssetPriority(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            string lower = name.ToLowerInvariant();
            if (lower.StartsWith("forgeflow_lite_setup_") && lower.EndsWith(".exe")) return 100;
            if (lower.Contains("forgeflow") && lower.Contains("setup") && lower.EndsWith(".exe")) return 90;
            if (lower.EndsWith(".exe")) return 80;
            if (lower.EndsWith(".msi")) return 60;
            return 0;
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
        public string ReleaseUrl { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public string DownloadFileName { get; init; } = "";
        public long DownloadSize { get; init; }
        public string DownloadDigest { get; init; } = "";
        public bool HasDirectInstaller { get; init; }
        public bool IsMandatory { get; init; }
        public bool IsBlocked { get; init; }
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

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
