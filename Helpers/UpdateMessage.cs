using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Turns an update-check outcome into the sentence the user reads. Pure — no HTTP, no WinUI, no
/// clock of its own — so every word the app can say about an update check is assertable in a test
/// rather than only reachable by actually being throttled by GitHub.
/// <para>
/// The rule the wording obeys: only <see cref="UpdateStatus.NetworkUnavailable"/> may mention the
/// user's connection. The single message this replaced said "Check your internet connection" for
/// every failure, GitHub's own throttling included, which sent people to reboot a router over a
/// quota that would have refilled by itself.
/// </para>
/// </summary>
internal static class UpdateMessage
{
    /// <summary>What to show, and whether it is a failure. The caller picks the channel
    /// (<c>Info</c> vs <c>Warn</c>), never the wording.</summary>
    internal readonly record struct Notice(string Text, bool IsError);

    /// <summary>
    /// The message for a completed check, or null for <see cref="UpdateStatus.Available"/> — that
    /// one is answered by the update dialog, which says far more than a sentence could, and a
    /// second message beside it would just be noise.
    /// </summary>
    /// <param name="outcome">The completed check.</param>
    /// <param name="runningVersion">The build the user is on, for the up-to-date confirmation.</param>
    /// <param name="now">Reference instant for phrasing the rate-limit retry time.</param>
    internal static Notice? For(UpdateCheckService.CheckOutcome outcome, string runningVersion,
                                DateTimeOffset now) =>
        outcome.Status switch
        {
            UpdateStatus.Available => null,

            UpdateStatus.UpToDate =>
                new Notice($"You're on the latest version (v{runningVersion}).", IsError: false),

            // Not a failure: the app works, there is simply nothing published to compare against.
            UpdateStatus.NoReleases =>
                new Notice("No releases have been published yet.", IsError: false),

            UpdateStatus.RateLimited =>
                new Notice(
                    "GitHub is limiting how many update checks it will answer, so this one could not "
                    + "run. That limit is GitHub's own, on requests that aren't signed in — nothing "
                    + "is wrong here.\n\n"
                    + RetrySentence(outcome.RateLimitResetsAt, now),
                    IsError: true),

            // Names the status code: it is the whole difference between "GitHub is broken" (5xx) and
            // "GitHub is refusing us" (a 403 that is not a throttle), and the user can quote it.
            UpdateStatus.HttpError =>
                new Notice(
                    $"GitHub answered the update check with HTTP {outcome.StatusCode}, so no version "
                    + "could be read. Try again later — see app.log.",
                    IsError: true),

            // The one arm entitled to blame the network: nothing came back at all.
            UpdateStatus.NetworkUnavailable =>
                new Notice("Could not reach GitHub to check for updates.\nCheck your internet connection.",
                           IsError: true),

            UpdateStatus.TimedOut =>
                new Notice(
                    $"The update check timed out — GitHub did not answer within "
                    + $"{UpdateCheckService.TimeoutSeconds} seconds. It may be slow right now; try "
                    + "again in a moment.",
                    IsError: true),

            UpdateStatus.UnreadableRelease => new Notice(UnreadableText(outcome.ReleaseTag), IsError: true),

            // A status added later must not inherit a sibling's claim, so it says only what is certain.
            _ => new Notice("Could not check for updates — see app.log.", IsError: true),
        };

    /// <summary>
    /// When to come back. A reset instant is quoted only while it is still ahead of
    /// <paramref name="now"/>; a stale or clock-skewed header would otherwise tell the user to wait
    /// until a time that has already passed.
    /// </summary>
    private static string RetrySentence(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || reset <= now) return "Try again in a few minutes.";

        var local = reset.ToLocalTime();
        // ISO date only when the reset lands on another day — within the hour the clock time is the answer.
        return local.Date == now.ToLocalTime().Date
            ? $"Try again after {local:HH:mm}."
            : $"Try again after {local:yyyy-MM-dd HH:mm}.";
    }

    /// <summary>
    /// Two different unreadable releases. A tag that can be quoted points at the release itself; no
    /// tag means the body never parsed. Both say the network is not the culprit, because that is
    /// exactly the wrong conclusion the old single message invited.
    /// </summary>
    private static string UnreadableText(string? tag) =>
        string.IsNullOrWhiteSpace(tag)
            ? "Could not read the release information GitHub returned, so no version could be "
              + "compared. This is not a network problem — see app.log."
            : $"GitHub's latest release is tagged '{tag}', which is not a version ChargeKeeper can "
              + "compare against. That is a problem with the release, not with your connection — "
              + "see app.log.";
}
