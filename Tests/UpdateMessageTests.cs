using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Pins the sentence the user reads for each update-check outcome. Only
/// <see cref="UpdateStatus.NetworkUnavailable"/> may mention the connection, so GitHub's own
/// throttling is never blamed on the user's network.
/// </summary>
public class UpdateMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 14, 0, 0, TimeSpan.Zero);

    private static UpdateCheckService.CheckOutcome With(UpdateStatus status, int code = 0,
                                                        DateTimeOffset? resetsAt = null, string? tag = null)
        => new()
        {
            Status            = status,
            ReleaseUrl        = UpdateCheckService.ReleasesPageUrl,
            StatusCode        = code,
            RateLimitResetsAt = resetsAt,
            ReleaseTag        = tag,
        };

    private static string TextFor(UpdateStatus status, int code = 0,
                                  DateTimeOffset? resetsAt = null, string? tag = null)
        => UpdateMessage.For(With(status, code, resetsAt, tag), "1.11.0", Now)!.Value.Text;

    [Fact]
    public void UpdateAvailable_HasNoMessage_TheDialogIsTheReport()
    {
        Assert.Null(UpdateMessage.For(With(UpdateStatus.Available), "1.11.0", Now));
    }

    [Fact]
    public void UpToDate_NamesTheRunningBuild_AndIsNotAnError()
    {
        var notice = UpdateMessage.For(With(UpdateStatus.UpToDate), "1.11.0", Now)!.Value;

        Assert.Equal("You're on the latest version (v1.11.0).", notice.Text);
        Assert.False(notice.IsError);
    }

    [Fact]
    public void NoReleases_IsNotAnError_NothingIsBroken()
    {
        var notice = UpdateMessage.For(With(UpdateStatus.NoReleases), "1.11.0", Now)!.Value;

        Assert.Equal("No releases have been published yet.", notice.Text);
        Assert.False(notice.IsError);
    }

    [Fact]
    public void NetworkUnavailable_IsTheOnlyOutcomeThatMentionsTheConnection()
    {
        Assert.Contains("internet connection", TextFor(UpdateStatus.NetworkUnavailable));

        foreach (var status in Enum.GetValues<UpdateStatus>())
        {
            if (status is UpdateStatus.NetworkUnavailable or UpdateStatus.Available) continue;

            var text = TextFor(status, code: 500, tag: "nightly");
            Assert.DoesNotContain("internet connection", text);
            Assert.DoesNotContain("Check your internet", text);
        }
    }

    [Fact]
    public void RateLimited_SaysItIsGitHubsLimit_AndWhenToComeBack()
    {
        var text = TextFor(UpdateStatus.RateLimited, code: 403, resetsAt: Now.AddMinutes(20));

        Assert.Contains("GitHub is limiting how many update checks it will answer", text);
        Assert.Contains("Try again after", text);
    }

    [Fact]
    public void RateLimited_WithNoResetHeader_StaysVagueRatherThanInventingATime()
    {
        Assert.Contains("Try again in a few minutes.",
                        TextFor(UpdateStatus.RateLimited, code: 429, resetsAt: null));
    }

    [Fact]
    public void RateLimited_WithAResetAlreadyPast_DoesNotTellTheUserToWaitForYesterday()
    {
        // A stale or clock-skewed header must not produce "try again after 13:40" at 14:00.
        Assert.Contains("Try again in a few minutes.",
                        TextFor(UpdateStatus.RateLimited, code: 403, resetsAt: Now.AddMinutes(-20)));
    }

    [Fact]
    public void RateLimited_ResetOnAnotherDay_IsDatedIso()
    {
        var text = TextFor(UpdateStatus.RateLimited, code: 403, resetsAt: Now.AddDays(2));

        Assert.Matches(@"Try again after \d{4}-\d{2}-\d{2} \d{2}:\d{2}\.", text);
    }

    [Fact]
    public void HttpError_QuotesTheStatusCode()
    {
        Assert.Contains("HTTP 503", TextFor(UpdateStatus.HttpError, code: 503));
    }

    [Fact]
    public void TimedOut_QuotesTheBudget_AndIsNotANetworkVerdict()
    {
        var text = TextFor(UpdateStatus.TimedOut);

        Assert.Contains($"{UpdateCheckService.TimeoutSeconds} seconds", text);
        Assert.Contains("timed out", text);
    }

    [Fact]
    public void UnreadableRelease_WithATag_QuotesIt()
    {
        var text = TextFor(UpdateStatus.UnreadableRelease, tag: "nightly-2026-08-21");

        Assert.Contains("'nightly-2026-08-21'", text);
        Assert.Contains("not with your connection", text);
    }

    [Fact]
    public void UnreadableRelease_WithoutATag_SaysTheBodyCouldNotBeRead()
    {
        var text = TextFor(UpdateStatus.UnreadableRelease, tag: null);

        Assert.Contains("Could not read the release information", text);
        Assert.Contains("not a network problem", text);
    }

    [Fact]
    public void EveryFailureOutcome_IsFlaggedAsAnError()
    {
        foreach (var status in new[]
                 {
                     UpdateStatus.RateLimited, UpdateStatus.HttpError, UpdateStatus.NetworkUnavailable,
                     UpdateStatus.TimedOut, UpdateStatus.UnreadableRelease,
                 })
        {
            Assert.True(UpdateMessage.For(With(status), "1.11.0", Now)!.Value.IsError, status.ToString());
        }
    }

    [Fact]
    public void EveryStatusExceptAvailable_ProducesANonEmptyMessage()
    {
        foreach (var status in Enum.GetValues<UpdateStatus>())
        {
            if (status == UpdateStatus.Available) continue;

            var notice = UpdateMessage.For(With(status), "1.11.0", Now);
            Assert.NotNull(notice);
            Assert.False(string.IsNullOrWhiteSpace(notice!.Value.Text), status.ToString());
        }
    }
}
