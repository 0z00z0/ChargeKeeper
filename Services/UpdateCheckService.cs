using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ChargeKeeper.Services;

internal static class UpdateCheckService
{
    // Read from the assembly manifest so the version never drifts out of sync with the csproj.
    private static readonly Version CurrentVersion =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    // Separate client for downloads — the check client's 10 s timeout is too short for a ~56 MB file.
    private static readonly HttpClient _downloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// The latest version string retrieved from GitHub, or null if the check has not run
    /// or failed.
    /// </summary>
    public static string? LatestVersion { get; private set; }

    private const string ReleasesPageUrl = "https://github.com/0z00z0/ChargeKeeper/releases";

    public enum UpdateStatus { UpToDate, Available, NoReleases, Error }

    /// <summary>Result of an on-demand "Check for updates" request.</summary>
    public readonly record struct CheckOutcome(
        UpdateStatus Status,
        string?      LatestVersion,
        string       ReleaseUrl,
        string?      InstallerUrl,
        string?      ReleaseNotes);

    /// <summary>
    /// On-demand update check that reports every outcome (up-to-date / available / no releases /
    /// error) so a menu action can show a result either way. Never throws.
    /// </summary>
    public static async Task<CheckOutcome> CheckNowAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.github.com/repos/0z00z0/ChargeKeeper/releases/latest");
            request.Headers.UserAgent.ParseAdd("ChargeKeeper/1.0");

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            // GitHub returns 404 when a repo has tags but no published releases yet.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(UpdateStatus.NoReleases, null, ReleasesPageUrl, null, null);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var releaseUrl = root.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? ReleasesPageUrl : ReleasesPageUrl;

            if (!root.TryGetProperty("tag_name", out var tagEl) ||
                tagEl.GetString() is not { Length: > 0 } tag ||
                !Version.TryParse(tag.TrimStart('v'), out var remote))
                return new(UpdateStatus.Error, null, releaseUrl, null, null);

            // Installer URL: first .exe asset in the release.
            string? installerUrl = null;
            if (root.TryGetProperty("assets", out var assetsEl))
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameEl) &&
                        nameEl.GetString() is { } name &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        asset.TryGetProperty("browser_download_url", out var urlEl))
                    {
                        installerUrl = urlEl.GetString();
                        break;
                    }
                }
            }

            var releaseNotes = root.TryGetProperty("body", out var bodyEl)
                ? StripMarkdown(bodyEl.GetString() ?? "") : null;

            LatestVersion = tag.TrimStart('v');
            return remote > CurrentVersion
                ? new(UpdateStatus.Available,  LatestVersion,               releaseUrl, installerUrl, releaseNotes)
                : new(UpdateStatus.UpToDate,   CurrentVersion.ToString(3),  releaseUrl, null,         null);
        }
        catch
        {
            return new(UpdateStatus.Error, null, ReleasesPageUrl, null, null);
        }
    }

    /// <summary>
    /// Downloads the installer at <paramref name="url"/> to a temp file and returns its path.
    /// Throws on failure. The caller is responsible for launching and/or cleaning up the file.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(string url)
    {
        var path = Path.Combine(Path.GetTempPath(), "ChargeKeeper-Setup.exe");
        using var response = await _downloadClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                       bufferSize: 81920, useAsync: true);
        await src.CopyToAsync(dst).ConfigureAwait(false);
        return path;
    }

    private static string StripMarkdown(string md)
    {
        // Code fences first so inner content isn't processed further.
        md = Regex.Replace(md, @"```[\s\S]*?```", "", RegexOptions.None);
        // Headings: ## Heading → Heading
        md = Regex.Replace(md, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Inline code: `code` → code
        md = Regex.Replace(md, @"`([^`]*)`", "$1");
        // Links: [text](url) → text
        md = Regex.Replace(md, @"\[([^\]]*)\]\([^\)]*\)", "$1");
        // Bold/italic markers
        md = Regex.Replace(md, @"\*{1,2}|_{1,2}", "");
        return md.Trim();
    }

    /// <summary>
    /// Performs a single update check against the GitHub releases API.
    /// Invokes <paramref name="onUpdateAvailable"/> with the new version string if a newer
    /// release exists. Network failures are silently swallowed.
    /// </summary>
    public static async Task CheckAsync(Action<string> onUpdateAvailable)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.github.com/repos/0z00z0/ChargeKeeper/releases/latest");

            request.Headers.UserAgent.ParseAdd("ChargeKeeper/1.0");

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tag_name", out var tagNameElement))
                return;

            var tagName = tagNameElement.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
                return;

            // Strip leading 'v' (e.g. "v1.2.3" -> "1.2.3")
            var versionString = tagName.TrimStart('v');

            if (!Version.TryParse(versionString, out var remoteVersion))
                return;

            LatestVersion = versionString;

            if (remoteVersion > CurrentVersion)
            {
                onUpdateAvailable(versionString);
            }
        }
        catch
        {
            // Network or parse failures must never propagate to the caller.
        }
    }
}
