using System.Diagnostics;
using ChargeKeeper.Helpers;
using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Services;

/// <summary>
/// The application's update service over the shared component: the check every surface joins, and
/// the install a found release starts. Reporting stays with the caller — every check runs under
/// <see cref="UpdateTrigger.Silent"/>, so nothing the component shows can contradict
/// <see cref="Helpers.UpdateButtonPolicy"/>, whose rule is that a window opening never raises a
/// dialog and the button's own success shows on the button.
/// </summary>
internal sealed class AppUpdates
{
    /// <summary>The release certificate's subject, exactly as signtool writes it.</summary>
    internal const string ExpectedPublisher = "CN=ZeroZero Software";

    private const string Owner = "0z00z0";
    private const string Repository = "ChargeKeeper";

    /// <summary>The release asset to download. <c>{version}</c> is the component's own placeholder,
    /// expanded to the release's version text; the asset match is exact and case-sensitive, so no
    /// other spelling and no wildcard finds the installer.</summary>
    internal const string InstallerAssetName = "ChargeKeeper-Setup-{version}.exe";

    /// <summary>Prefix of the per-run download directory. Each download goes to a fresh,
    /// unpredictable name under it, so nothing can plant a file at a guessable path.</summary>
    private const string DownloadDirectoryPrefix = "ChargeKeeper-Update-";

    /// <summary>How old a leftover download must be before a sweep may take it. A sweep carries on
    /// past an entry it cannot remove, so one running beside a live install could take that
    /// install's own file with it; an hour puts this run's directory out of reach.</summary>
    private static readonly TimeSpan StaleDownloadAge = TimeSpan.FromHours(1);

    private readonly UpdateService _service;
    private readonly UpdateHandoverLauncher _launcher = new();
    private readonly UpdateFlow _checks;
    private readonly Action _shutdown;

    internal AppUpdates(Action shutdown)
    {
        _shutdown = shutdown;
        _service  = new UpdateService(Options(), source: null, launcher: _launcher);
        // Owner zero: a silent check puts nothing on screen, so these prompts never reach it.
        _checks   = new UpdateFlow(_service, PromptsFor(IntPtr.Zero), FlowOptions());

        // Start-up only, while no install can be in flight — see StaleDownloadAge.
        try { _service.SweepStaleDownloads(StaleDownloadAge); }
        catch (Exception ex) { AppLog.Error("AppUpdates.SweepStaleDownloads", ex); }
    }

    /// <summary>The version the check compares a release against.</summary>
    internal Version RunningVersion => _service.RunningVersion;

    /// <summary>Where every refused download sends the user instead.</summary>
    internal static Uri ReleasesPage { get; } = new($"https://github.com/{Owner}/{Repository}/releases");

    /// <summary>One check, reported by nobody: the caller reads the run and decides what to show.</summary>
    internal Task<UpdateFlowRun> CheckAsync() => _checks.RunAsync(UpdateTrigger.Silent);

    /// <summary>
    /// Offers and installs a release already found, with no second check. The flow is built per call
    /// because the dialog's owner window is fixed when the prompts are constructed, and the offer has
    /// to belong to whichever window asked for it.
    /// </summary>
    internal Task<UpdateFlowRun> InstallAsync(ReleaseInfo release, IntPtr owner)
    {
        // Read as Setup is launched, so a declined offer leaves no handover record behind.
        _launcher.TargetVersion = release.VersionText;
        return new UpdateFlow(_service, PromptsFor(owner), FlowOptions()).InstallAsync(release);
    }

    /// <summary>The component's own wording for every outcome a caller chooses to report.</summary>
    internal IUpdatePrompts PromptsFor(IntPtr owner) =>
        new NativeUpdatePrompts(owner, AppInfo.Name,
                                releaseNotes: release => ReleaseNotesText.Strip(release.Body ?? ""));

    private static UpdateOptions Options() => new()
    {
        RepositoryOwner = Owner,
        RepositoryName  = Repository,
        ProductName     = AppInfo.Name,
        // Named rather than left to the entry assembly, so the comparison uses the same number the
        // About window and the release tag do.
        RunningVersion  = Version.TryParse(AppInfo.Version, out var running) ? running : new Version(1, 0, 0),
        // Publisher name alone, no pinned thumbprint: the release certificate is self-signed, and a
        // pin would turn its next rotation into a silent update outage.
        ExpectedSigner  = new ExpectedSigner(ExpectedPublisher, certificateThumbprints: null,
                                             acceptSelfSignedSubject: true),
        DirectoryPrefix    = DownloadDirectoryPrefix,
        InstallerFileName  = InstallerAssetName,
        InstallerArguments = UnattendedUpdate.Arguments(UnattendedUpdate.InstallerLogPath),
        Log                = new AppLogSink(),
    };

    private UpdateFlowOptions FlowOptions() => new()
    {
        Shutdown        = _shutdown,
        OpenReleasePage = OpenInBrowser,
        Log             = new AppLogSink(),
    };

    private static void OpenInBrowser(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error("AppUpdates.OpenReleasePage", ex); }
    }
}
