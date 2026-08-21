using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Drives every arm of the update check through a stub handler. The point of the exercise is that
/// each cause stays distinguishable: before this, a GitHub throttle, a 500 and a dead network all
/// collapsed into one <c>Error</c> that told the user to check their internet connection.
/// </summary>
public class UpdateCheckServiceTests
{
    private const string RunningVersion = "1.10.0";

    /// <summary>Answers with whatever the test hands it, or throws what the test hands it.</summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }

    private sealed class ThrowingHandler(Func<Exception> throwThis) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
            => throw throwThis();
    }

    private static UpdateCheckService ServiceFor(HttpMessageHandler handler) =>
        new(new HttpClient(handler), null, new Version(RunningVersion));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string ReleaseJson(string tag, string asset = "ChargeKeeper-Setup-9.9.9.exe") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/0z00z0/ChargeKeeper/releases/tag/{{tag}}",
          "body": "## What's new\n- A **thing**\n",
          "assets": [ { "name": "{{asset}}",
                        "browser_download_url": "https://example.invalid/{{asset}}" } ]
        }
        """;

    [Fact]
    public async Task NewerTag_IsUpdateAvailable_WithInstallerAndNotes()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, ReleaseJson("v9.9.9"))))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.Available, outcome.Status);
        Assert.Equal("9.9.9", outcome.LatestVersion);
        Assert.NotNull(outcome.InstallerUrl);
        // Markdown stripping is a ChargeKeeper advantage worth keeping: the heading marker goes.
        Assert.DoesNotContain("##", outcome.ReleaseNotes);
    }

    [Fact]
    public async Task OlderTag_IsUpToDate_AndReportsTheRunningBuild()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, ReleaseJson("v1.0.0"))))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.UpToDate, outcome.Status);
        Assert.Equal(RunningVersion, outcome.LatestVersion);
        // An up-to-date result must not carry a download: it is the only gate on offering one.
        Assert.Null(outcome.InstallerUrl);
    }

    [Fact]
    public async Task SameTag_IsUpToDate()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, ReleaseJson("v1.10.0"))))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.UpToDate, outcome.Status);
    }

    [Fact]
    public async Task DefaultOutcome_IsUpToDate_SoNothingAnnouncesAnUpdateByAccident()
    {
        Assert.Equal(UpdateStatus.UpToDate, default(UpdateCheckService.CheckOutcome).Status);
    }

    [Fact]
    public async Task NotFound_IsNoReleases()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.NotFound, "{}")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.NoReleases, outcome.Status);
    }

    [Fact]
    public async Task TooManyRequests_IsRateLimited_WithoutAnyHeaders()
    {
        // 429 is GitHub's secondary/abuse limit and is always a throttle, headers or not.
        var outcome = await ServiceFor(new StubHandler(() => Json((HttpStatusCode)429, "{}")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.RateLimited, outcome.Status);
        Assert.Equal(429, outcome.StatusCode);
        Assert.Null(outcome.RateLimitResetsAt);
    }

    [Fact]
    public async Task Forbidden_WithRemainingZero_IsRateLimited_AndReadsTheResetInstant()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(17).ToUnixTimeSeconds();
        var outcome = await ServiceFor(new StubHandler(() =>
        {
            var r = Json(HttpStatusCode.Forbidden, "{}");
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Reset", reset.ToString());
            return r;
        })).CheckNowAsync();

        Assert.Equal(UpdateStatus.RateLimited, outcome.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), outcome.RateLimitResetsAt);
    }

    [Fact]
    public async Task Forbidden_WithRetryAfterOnly_IsRateLimited()
    {
        // GitHub sends Retry-After only when it wants the caller back later, so it is a throttle
        // signal in its own right even without the X-RateLimit family.
        var outcome = await ServiceFor(new StubHandler(() =>
        {
            var r = Json(HttpStatusCode.Forbidden, "{}");
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            return r;
        })).CheckNowAsync();

        Assert.Equal(UpdateStatus.RateLimited, outcome.Status);
        Assert.NotNull(outcome.RateLimitResetsAt);
    }

    [Fact]
    public async Task Forbidden_WithRemainingLeft_IsAPlainHttpError_NotAThrottle()
    {
        // The whole reason status alone cannot decide it: 403 is also GitHub's plain refusal.
        var outcome = await ServiceFor(new StubHandler(() =>
        {
            var r = Json(HttpStatusCode.Forbidden, "{}");
            r.Headers.Add("X-RateLimit-Remaining", "42");
            return r;
        })).CheckNowAsync();

        Assert.Equal(UpdateStatus.HttpError, outcome.Status);
        Assert.Equal(403, outcome.StatusCode);
    }

    [Fact]
    public async Task Forbidden_WithNoRateLimitHeadersAtAll_IsAPlainHttpError()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.Forbidden, "{}")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.HttpError, outcome.Status);
    }

    [Fact]
    public async Task ServerError_IsHttpError_AndCarriesTheCode()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.InternalServerError, "{}")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.HttpError, outcome.Status);
        Assert.Equal(500, outcome.StatusCode);
    }

    [Fact]
    public async Task NoResponseAtAll_IsNetworkUnavailable()
    {
        var outcome = await ServiceFor(new ThrowingHandler(() => new HttpRequestException("no such host")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.NetworkUnavailable, outcome.Status);
    }

    [Fact]
    public async Task CancelledRequest_IsTimedOut_NotANetworkFailure()
    {
        // HttpClient reports its own timeout as a TaskCanceledException; landing that in the
        // network arm is exactly what made a slow GitHub read as a broken connection.
        var outcome = await ServiceFor(new ThrowingHandler(() => new TaskCanceledException()))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.TimedOut, outcome.Status);
    }

    [Fact]
    public async Task UnparseableTag_IsUnreadableRelease_AndNamesTheTag()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, ReleaseJson("nightly-2026-08-21"))))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.UnreadableRelease, outcome.Status);
        Assert.Equal("nightly-2026-08-21", outcome.ReleaseTag);
    }

    [Fact]
    public async Task MissingTag_IsUnreadableRelease_WithNoTagToName()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, """{ "html_url": "x" }""")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.UnreadableRelease, outcome.Status);
        Assert.Null(outcome.ReleaseTag);
    }

    [Fact]
    public async Task MalformedBody_IsUnreadableRelease_NotANetworkFailure()
    {
        var outcome = await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, "not json at all")))
                            .CheckNowAsync();

        Assert.Equal(UpdateStatus.UnreadableRelease, outcome.Status);
    }

    [Fact]
    public async Task EveryFailureOutcome_StillOffersTheReleasePage()
    {
        // The page URL is the fallback the tray menu opens, so no arm may leave it empty.
        var handlers = new HttpMessageHandler[]
        {
            new StubHandler(() => Json(HttpStatusCode.NotFound, "{}")),
            new StubHandler(() => Json((HttpStatusCode)429, "{}")),
            new StubHandler(() => Json(HttpStatusCode.InternalServerError, "{}")),
            new ThrowingHandler(() => new HttpRequestException("x")),
            new ThrowingHandler(() => new TaskCanceledException()),
            new StubHandler(() => Json(HttpStatusCode.OK, "not json at all")),
        };

        foreach (var handler in handlers)
        {
            var outcome = await ServiceFor(handler).CheckNowAsync();
            Assert.False(string.IsNullOrWhiteSpace(outcome.ReleaseUrl));
        }
    }

    [Fact]
    public async Task SilentStartupCheck_FiresOnlyWhenAnUpdateExists()
    {
        string? announced = null;

        await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, ReleaseJson("v1.0.0"))))
              .CheckAsync(v => announced = v);
        Assert.Null(announced);

        await ServiceFor(new StubHandler(() => Json(HttpStatusCode.OK, ReleaseJson("v9.9.9"))))
              .CheckAsync(v => announced = v);
        Assert.Equal("9.9.9", announced);
    }

    [Fact]
    public async Task SilentStartupCheck_StaysSilentOnEveryFailure()
    {
        var handlers = new HttpMessageHandler[]
        {
            new StubHandler(() => Json((HttpStatusCode)429, "{}")),
            new StubHandler(() => Json(HttpStatusCode.InternalServerError, "{}")),
            new ThrowingHandler(() => new HttpRequestException("x")),
            new ThrowingHandler(() => new TaskCanceledException()),
            new StubHandler(() => Json(HttpStatusCode.OK, "not json at all")),
        };

        foreach (var handler in handlers)
        {
            var fired = false;
            await ServiceFor(handler).CheckAsync(_ => fired = true);
            Assert.False(fired);
        }
    }
}
