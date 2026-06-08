using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace LenovoTray.Services;

internal static class UpdateCheckService
{
    // Read from the assembly manifest so the version never drifts out of sync with the csproj.
    private static readonly Version CurrentVersion =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// The latest version string retrieved from GitHub, or null if the check has not run
    /// or failed.
    /// </summary>
    public static string? LatestVersion { get; private set; }

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
                "https://api.github.com/repos/0z00z0/LenovoPowerTray/releases/latest");

            request.Headers.UserAgent.ParseAdd("LenovoPowerTray/1.0");

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
