using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ChargeKeeper.Services;

/// <summary>
/// Why an update check ended the way it did. Every cause stays distinguishable all the way to
/// <see cref="Helpers.UpdateMessage"/>, which owns the wording: a GitHub throttle, a 500, a dead
/// network, a timeout and an unparseable tag are five different things, and only one of them is
/// the user's connection.
/// </summary>
internal enum UpdateStatus
{
    /// <summary>GitHub's latest release is not newer than the running build. Deliberately the
    /// zero value rather than <see cref="Available"/>: a default-constructed outcome must never
    /// raise the tray badge.</summary>
    UpToDate,

    /// <summary>A newer release exists. The only status that may raise the badge.</summary>
    Available,

    /// <summary>The repo has no published releases (HTTP 404 — tags are not releases on GitHub).</summary>
    NoReleases,

    /// <summary>GitHub refused because the anonymous request quota is spent. Nothing to do with
    /// the user's network, so the message must not blame it.</summary>
    RateLimited,

    /// <summary>GitHub answered with an unsuccessful status that is none of the above — a 5xx, or
    /// a 403 that is a plain refusal rather than a throttle. The code is carried so it is quotable.</summary>
    HttpError,

    /// <summary>No response arrived at all: DNS failure, no route, refused connection. The only
    /// status for which "check your internet connection" is honest.</summary>
    NetworkUnavailable,

    /// <summary>The request did not finish inside <see cref="UpdateCheckService.TimeoutSeconds"/>.
    /// Distinct from <see cref="NetworkUnavailable"/> — the network may be fine and GitHub slow.</summary>
    TimedOut,

    /// <summary>The response was read but not understood: an unparseable <c>tag_name</c>, or a body
    /// that is not the JSON expected. A release-metadata problem, explicitly not a network one.</summary>
    UnreadableRelease,
}

/// <summary>
/// Checks the GitHub releases API for a newer ChargeKeeper and downloads the installer.
/// </summary>
/// <remarks>
/// An instance rather than a static so the two things that decide the outcome can be supplied:
/// the <see cref="HttpClient"/> (a stub handler drives every arm below without a network) and the
/// running version (the up-to-date/available decision is then exercised against a known build
/// instead of against whichever assembly happens to host the code). <see cref="Shared"/> is what
/// the app uses.
/// </remarks>
internal sealed class UpdateCheckService
{
    /// <summary>The releases API endpoint. One constant — it used to be spelled out at each of the
    /// two check methods, which is how they drifted apart in the first place.</summary>
    private const string ReleasesApiUrl = "https://api.github.com/repos/0z00z0/ChargeKeeper/releases/latest";

    /// <summary>Where to send the user when the API path fails.</summary>
    internal const string ReleasesPageUrl = "https://github.com/0z00z0/ChargeKeeper/releases";

    /// <summary>How long a check may take before it is abandoned. Named because
    /// <see cref="Helpers.UpdateMessage"/> tells the user the number.</summary>
    internal const int TimeoutSeconds = 10;

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
    };

    // Separate client for downloads — the check client's 10 s timeout is far too short for a ~56 MB file.
    private static readonly HttpClient DefaultDownloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>The instance the app runs on: real clients, real assembly version.</summary>
    internal static UpdateCheckService Shared { get; } = new();

    private readonly HttpClient _http;
    private readonly HttpClient _download;
    private readonly Version    _runningVersion;

    internal UpdateCheckService(HttpClient? http = null, HttpClient? downloadClient = null,
                                Version? runningVersion = null)
    {
        _http     = http           ?? DefaultHttpClient;
        _download = downloadClient ?? DefaultDownloadClient;
        // Read from the assembly manifest so the version never drifts out of sync with the csproj.
        _runningVersion = runningVersion
                       ?? Assembly.GetEntryAssembly()?.GetName().Version
                       ?? new Version(1, 0, 0);
    }

    /// <summary>
    /// The result of one <see cref="CheckNowAsync"/> call. Built only through the factories below,
    /// so every arm has to state its <see cref="Status"/> — there is no way left to produce an
    /// outcome whose cause the caller has to infer from a null.
    /// </summary>
    internal readonly record struct CheckOutcome
    {
        /// <summary>Why the check ended as it did.</summary>
        public UpdateStatus Status { get; init; }

        /// <summary>The version compared against, three-part. The remote one when a release was
        /// read, the running one when up to date; null otherwise.</summary>
        public string? LatestVersion { get; init; }

        /// <summary>Release page to open. Always set — it is the fallback for every failure.</summary>
        public string ReleaseUrl { get; init; }

        /// <summary>Direct .exe asset URL, null when the release carries none.</summary>
        public string? InstallerUrl { get; init; }

        /// <summary>Release body with markdown stripped, null when there is nothing to show.</summary>
        public string? ReleaseNotes { get; init; }

        /// <summary>The HTTP status GitHub answered with. 0 when no response arrived.</summary>
        public int StatusCode { get; init; }

        /// <summary>When GitHub's quota refills, from <c>X-RateLimit-Reset</c> or <c>Retry-After</c>.
        /// Null when the response said neither — the message then stays vague rather than invent a
        /// time.</summary>
        public DateTimeOffset? RateLimitResetsAt { get; init; }

        /// <summary>The raw <c>tag_name</c> that would not parse — the one thing worth reporting for
        /// <see cref="UpdateStatus.UnreadableRelease"/>. Null when the body never got that far.</summary>
        public string? ReleaseTag { get; init; }

        internal static CheckOutcome Release(bool newer, string remoteVersion, string runningVersion,
                                             string releaseUrl, string? installerUrl, string? notes) => new()
        {
            Status        = newer ? UpdateStatus.Available : UpdateStatus.UpToDate,
            LatestVersion = newer ? remoteVersion : runningVersion,
            ReleaseUrl    = releaseUrl,
            InstallerUrl  = newer ? installerUrl : null,
            ReleaseNotes  = newer ? notes : null,
            StatusCode    = (int)HttpStatusCode.OK,
        };

        internal static CheckOutcome NoReleases() => new()
        {
            Status     = UpdateStatus.NoReleases,
            ReleaseUrl = ReleasesPageUrl,
            StatusCode = (int)HttpStatusCode.NotFound,
        };

        internal static CheckOutcome RateLimited(int statusCode, DateTimeOffset? resetsAt) => new()
        {
            Status            = UpdateStatus.RateLimited,
            ReleaseUrl        = ReleasesPageUrl,
            StatusCode        = statusCode,
            RateLimitResetsAt = resetsAt,
        };

        internal static CheckOutcome HttpFailure(int statusCode) => new()
        {
            Status     = UpdateStatus.HttpError,
            ReleaseUrl = ReleasesPageUrl,
            StatusCode = statusCode,
        };

        internal static CheckOutcome NetworkUnavailable() => new()
        {
            Status     = UpdateStatus.NetworkUnavailable,
            ReleaseUrl = ReleasesPageUrl,
        };

        internal static CheckOutcome TimedOut() => new()
        {
            Status     = UpdateStatus.TimedOut,
            ReleaseUrl = ReleasesPageUrl,
        };

        internal static CheckOutcome UnreadableRelease(string? tag, string? releaseUrl = null) => new()
        {
            Status     = UpdateStatus.UnreadableRelease,
            ReleaseUrl = releaseUrl ?? ReleasesPageUrl,
            ReleaseTag = tag,
        };
    }

    /// <summary>
    /// On-demand update check. Reports every outcome so a menu action can show a result either
    /// way, and logs each one. Never throws.
    /// </summary>
    internal async Task<CheckOutcome> CheckNowAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.ParseAdd($"ChargeKeeper/{_runningVersion.ToString(3)}");

            using var response = await _http.SendAsync(request).ConfigureAwait(false);

            // GitHub returns 404 when a repo has tags but no published releases yet.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                AppLog.Info("Update check: GitHub has no published releases yet (HTTP 404).");
                return CheckOutcome.NoReleases();
            }

            // Checked ahead of the generic unsuccessful-status arm: a spent quota is the one HTTP
            // failure the user can neither fix nor be blamed for, so it must not read as a fault.
            if (IsRateLimited(response))
            {
                var resetsAt = RateLimitReset(response);
                AppLog.Info($"Update check: GitHub rate limit reached (HTTP {(int)response.StatusCode}); " +
                            $"resets {resetsAt?.ToString("u") ?? "unknown"}.");
                return CheckOutcome.RateLimited((int)response.StatusCode, resetsAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info($"Update check: GitHub answered HTTP {(int)response.StatusCode} " +
                            $"{response.ReasonPhrase}.");
                return CheckOutcome.HttpFailure((int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var releaseUrl = root.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? ReleasesPageUrl : ReleasesPageUrl;

            // TryGetProperty, not GetProperty: a release without a readable tag is a reportable
            // outcome, not an exception for the catch-all at the bottom to guess at.
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (tag is not { Length: > 0 } || !Version.TryParse(tag.TrimStart('v'), out var remote))
            {
                AppLog.Info($"Update check: GitHub's latest release carries an unusable tag_name " +
                            $"'{tag ?? "(absent)"}'.");
                return CheckOutcome.UnreadableRelease(tag, releaseUrl);
            }

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

            var newer = remote > _runningVersion;
            AppLog.Info($"Update check: running {_runningVersion.ToString(3)}, latest {remote.ToString(3)} " +
                        $"— {(newer ? "update available" : "up to date")}.");

            return CheckOutcome.Release(newer, remote.ToString(3), _runningVersion.ToString(3),
                                        releaseUrl, installerUrl, releaseNotes);
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient surfaces its own timeout as a TaskCanceledException, so without this arm a
            // slow GitHub lands in the network arm below and reads as a broken connection. No caller
            // token reaches this method, so a cancellation here can only be the time budget expiring.
            AppLog.Error($"Update check timed out after {TimeoutSeconds} s.", ex);
            return CheckOutcome.TimedOut();
        }
        catch (HttpRequestException ex)
        {
            // No usable response at all — DNS failure, no route, refused or reset connection.
            AppLog.Error("Update check could not reach GitHub.", ex);
            return CheckOutcome.NetworkUnavailable();
        }
        catch (JsonException ex)
        {
            AppLog.Error("Update check could not parse GitHub's response body.", ex);
            return CheckOutcome.UnreadableRelease(null);
        }
        catch (Exception ex)
        {
            // The transport worked and the arms above did not fire, so whatever failed is on this
            // side of the wire. Reported as unreadable rather than as a network fault, because
            // telling the user to check their connection here would be a guess.
            AppLog.Error("Update check failed while reading GitHub's response.", ex);
            return CheckOutcome.UnreadableRelease(null);
        }
    }

    /// <summary>
    /// True when GitHub refused because the quota is spent, as opposed to refusing outright.
    /// <para>
    /// Status alone cannot decide it: 403 is GitHub's answer for the primary rate limit AND for a
    /// plain forbidden, so the headers arbitrate — <c>X-RateLimit-Remaining: 0</c>, or a
    /// <c>Retry-After</c>, which GitHub sends only when it wants the caller back later. 429 is the
    /// secondary/abuse limit and is always a throttle.
    /// </para>
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden)       return false;

        if (HeaderValue(response, "X-RateLimit-Remaining") is { } remaining &&
            int.TryParse(remaining, out var left))
            return left <= 0;

        return response.Headers.RetryAfter is not null;
    }

    /// <summary>
    /// When the quota refills. <c>X-RateLimit-Reset</c> (unix seconds) wins because it is an
    /// absolute instant; <c>Retry-After</c> is the fallback. Null when the response carried neither.
    /// </summary>
    private static DateTimeOffset? RateLimitReset(HttpResponseMessage response)
    {
        if (HeaderValue(response, "X-RateLimit-Reset") is { } reset && long.TryParse(reset, out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return DateTimeOffset.UtcNow.Add(delta);
        if (retryAfter?.Date  is { } date)  return date;

        return null;
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Downloads the installer at <paramref name="url"/> and returns its path. Throws on failure.
    /// The caller MUST pass the result through <see cref="Helpers.InstallerSignature.Verify"/>
    /// before launching it, and is responsible for cleaning up.
    /// </summary>
    internal async Task<string> DownloadInstallerAsync(string url)
    {
        var path = Helpers.InstallerSignature.NewDownloadPath();
        using var response = await _download
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                       bufferSize: 81920, useAsync: true);
        await src.CopyToAsync(dst).ConfigureAwait(false);
        AppLog.Info($"Update: installer downloaded to {path}.");
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
    /// The silent startup check: invokes <paramref name="onUpdateAvailable"/> with the new version
    /// string when a newer release exists, and does nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="CheckNowAsync"/> rather than repeating the request. It used to be a
    /// second, near-identical copy with its own hard-coded endpoint and its own bare catch, which
    /// is how the two drifted; there is now one code path, and every failure it can hit is already
    /// logged by the one that does the work.
    /// </remarks>
    internal async Task CheckAsync(Action<string> onUpdateAvailable)
    {
        var outcome = await CheckNowAsync().ConfigureAwait(false);
        if (outcome.Status == UpdateStatus.Available && outcome.LatestVersion is { Length: > 0 } version)
            onUpdateAvailable(version);
    }
}
