using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    public class ReleaseInfo
    {
        public Version? Version { get; set; }
        public string TagName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public DateTime? PublishedAt { get; set; }
        public string? DownloadUrl { get; set; }
        public long AssetSize { get; set; }
        public bool IsPrerelease { get; set; }
    }

    public class ReleaseChecker : IDisposable
    {
        private readonly HttpClient m_httpClient;
        private readonly JsonSerializerOptions m_jsonOptions;

        public ReleaseChecker(HttpClient? httpClient = null)
        {
            m_httpClient = httpClient ?? new HttpClient();
            m_jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        public async Task<Version?> GetLatestStableVersionAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
        {
            var info = await GetLatestReleaseInfoAsync(owner, repo, cancellationToken);
            return info?.Version;
        }

        public async Task<ReleaseInfo?> GetLatestReleaseInfoAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
        {
            var release = await GetLatestStableReleaseAsync(owner, repo, cancellationToken);
            if (release?.TagName == null)
            {
                return null;
            }

            var cleanTag = release.TagName.TrimStart('v').Split('-')[0];
            Version? parsedVersion = null;
            if (Version.TryParse(cleanTag, out var v))
            {
                parsedVersion = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }

            string? downloadUrl = null;
            long assetSize = 0;
            if (release.Assets != null)
            {
                foreach (var asset in release.Assets)
                {
                    if (asset.Name != null && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.BrowserDownloadUrl;
                        assetSize = asset.Size;
                        break;
                    }
                }
            }

            return new ReleaseInfo
            {
                Version = parsedVersion,
                TagName = release.TagName,
                Title = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                Changelog = release.Body ?? string.Empty,
                HtmlUrl = release.HtmlUrl ?? $"https://github.com/{owner}/{repo}/releases/tag/{release.TagName}",
                PublishedAt = release.PublishedAt,
                DownloadUrl = downloadUrl,
                AssetSize = assetSize,
                IsPrerelease = release.Prerelease
            };
        }

        public async Task DownloadAssetAsync(
            string downloadUrl,
            string targetFilePath,
            long expectedSize,
            IProgress<(long BytesRead, long TotalBytes, double SpeedMBps)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var request = CreateRequest(downloadUrl);
            using var response = await m_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
            var targetDir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            var buffer = new byte[65536];
            long totalBytesRead = 0;
            int bytesRead;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalBytesRead += bytesRead;

                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                var speedMBps = elapsedSeconds > 0 ? (totalBytesRead / (1024.0 * 1024.0)) / elapsedSeconds : 0;
                progress?.Report((totalBytesRead, totalBytes, speedMBps));
            }
        }

        private async Task<GitHubRelease?> GetLatestStableReleaseAsync(
            string owner,
            string repo,
            CancellationToken cancellationToken = default)
        {
            var releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases";

            using var request = CreateRequest(releasesUrl);
            using var response = await m_httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = JsonSerializer.Deserialize<GitHubRelease[]>(json, m_jsonOptions);

            if (releases == null || releases.Length == 0)
                return null;

            return FindLatestStableRelease(releases);
        }

        private static HttpRequestMessage CreateRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0");
            request.Headers.Add("Accept", "application/vnd.github.v3+json");
            return request;
        }

        private static GitHubRelease? FindLatestStableRelease(GitHubRelease[] releases)
        {
            foreach (var release in releases)
            {
                if (IsStableRelease(release))
                {
                    return release;
                }
            }
            // If all releases are prereleases (e.g. preview phase), return the newest one
            return releases.Length > 0 ? releases[0] : null;
        }

        private static bool IsStableRelease(GitHubRelease release)
        {
            if (release.Prerelease)
            {
                return false;
            }

            var tagName = release.TagName?.ToLowerInvariant() ?? string.Empty;
            return !tagName.Contains("rc") && !tagName.Contains("release-candidate");
        }

        public void Dispose()
        {
            m_httpClient?.Dispose();
        }
    }
}
