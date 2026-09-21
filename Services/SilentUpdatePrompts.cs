using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Services;

/// <summary>
/// The answers an automatic install gives: install, and say nothing. Every outcome the component
/// would otherwise put in a window is written to the log instead.
/// </summary>
/// <remarks>
/// This is what makes an automatic install reuse the ordinary flow rather than a second download
/// and install path: the flow asks its prompts whether to install and where to report progress, and
/// these answers draw nothing. Nothing here touches XAML, so unlike the window prompts it does not
/// have to be called from the thread that owns the application's windows.
/// </remarks>
internal sealed class SilentUpdatePrompts : IUpdatePrompts
{
    /// <summary>Discards every report. The download runs with nobody watching by design.</summary>
    private sealed class NoProgress : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) { }
    }

    public Task<InstallChoice> AskToInstallAsync(ReleaseInfo release, Version runningVersion)
    {
        AppLog.Info($"Automatic update: installing {release.VersionText} over {runningVersion}.");
        return Task.FromResult(InstallChoice.Install);
    }

    public DownloadSurface BeginDownload(ReleaseInfo release) =>
        new(new NoProgress(), CancellationToken.None);

    // A check never reaches these on this path — the install starts from a release already in hand
    // — but the interface carries them, and silence is the right answer to all three.
    public Task SayUpToDateAsync(Version runningVersion) => Task.CompletedTask;
    public Task SayNothingReleasedAsync() => Task.CompletedTask;
    public Task SayCheckFailedAsync(UpdateCheckResult result) => Task.CompletedTask;

    public Task SayCannotInstallAsync(PreparedUpdate update)
    {
        AppLog.Info($"Automatic update: refused — {update.Outcome}. {update.Detail}");
        return Task.CompletedTask;
    }

    public Task SayLaunchFailedAsync(PreparedUpdate update, LaunchResult result)
    {
        AppLog.Info($"Automatic update: Setup did not start. {result.Detail}");
        return Task.CompletedTask;
    }

    public void Dismiss() { }
}
