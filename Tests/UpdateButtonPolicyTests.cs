using ChargeKeeper.Helpers;
using Xunit;
using ZeroZero.Brand.WinUI;
using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Tests;

/// <summary>
/// Which update-check outcomes reach a dialog. A window that raised a dialog merely by opening would
/// interrupt someone who only wanted to read the version, and a manual check that failed without a
/// dialog would leave them believing it had worked.
/// </summary>
public class UpdateButtonPolicyTests
{
    public static TheoryData<UpdateFlowResult> EveryResult()
    {
        var data = new TheoryData<UpdateFlowResult>();
        foreach (var result in Enum.GetValues<UpdateFlowResult>()) data.Add(result);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryResult))]
    public void AnAutomaticCheckNeverOpensADialog(UpdateFlowResult result)
    {
        Assert.False(UpdateButtonPolicy.ShowsNotice(result, UpdateCheckTrigger.Automatic));
        Assert.False(UpdateButtonPolicy.OpensUpdateDialog(result, UpdateCheckTrigger.Automatic));
    }

    [Theory]
    [MemberData(nameof(EveryResult))]
    public void TheButtonReportsOnlyAFailureInADialog(UpdateFlowResult result)
    {
        bool failure = result is not (UpdateFlowResult.UpToDate or UpdateFlowResult.UpdateAvailable
                                                                or UpdateFlowResult.DownloadCancelled);
        Assert.Equal(failure, UpdateButtonPolicy.ShowsNotice(result, UpdateCheckTrigger.Button));
        Assert.False(UpdateButtonPolicy.OpensUpdateDialog(result, UpdateCheckTrigger.Button));
    }

    [Theory]
    [MemberData(nameof(EveryResult))]
    public void TheTrayMenuReportsEveryOutcomeInADialog(UpdateFlowResult result)
    {
        bool available = result == UpdateFlowResult.UpdateAvailable;
        bool stopped   = result == UpdateFlowResult.DownloadCancelled;
        Assert.Equal(available,  UpdateButtonPolicy.OpensUpdateDialog(result, UpdateCheckTrigger.TrayMenu));
        Assert.Equal(!available && !stopped,
                     UpdateButtonPolicy.ShowsNotice(result, UpdateCheckTrigger.TrayMenu));
    }

    // Stopping the download closes the window it was stopped in. Reopening one to announce the stop
    // would report a person's own click back at them, from every surface at once.
    [Fact]
    public void AStoppedDownloadIsNeverAnnounced()
    {
        // One Fact rather than a theory: UpdateCheckTrigger is internal, and a public test method
        // cannot take it as a parameter.
        foreach (var trigger in Enum.GetValues<UpdateCheckTrigger>())
        {
            Assert.False(UpdateButtonPolicy.ShowsNotice(UpdateFlowResult.DownloadCancelled, trigger));
            Assert.False(UpdateButtonPolicy.OpensUpdateDialog(UpdateFlowResult.DownloadCancelled, trigger));
        }
    }

    // The button follows a check, and a check never downloads, so a cancelled download reaching it
    // means something has been rewired — it must not leave the button showing success or attention.
    [Fact]
    public void AStoppedDownloadLeavesTheButtonAtRest() =>
        Assert.Equal(UpdateButtonPolicy.Rest,
                     UpdateButtonPolicy.After(new UpdateFlowRun(UpdateFlowResult.DownloadCancelled)));

    [Fact]
    public void TheButtonShowsTheOutcome()
    {
        var release = new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "9.9.9", "ChargeKeeper v9.9.9",
                                      "", new Uri("https://example.invalid"), null, []);

        Assert.Equal(new UpdateButtonPolicy.Look(BrandBracketButtonState.Success, "Up to date"),
                     UpdateButtonPolicy.After(new UpdateFlowRun(UpdateFlowResult.UpToDate)));
        Assert.Equal(new UpdateButtonPolicy.Look(BrandBracketButtonState.Attention, "Update to 9.9.9"),
                     UpdateButtonPolicy.After(new UpdateFlowRun(UpdateFlowResult.UpdateAvailable, Release: release)));
        Assert.Equal(new UpdateButtonPolicy.Look(BrandBracketButtonState.Rest, "Check for updates"),
                     UpdateButtonPolicy.After(new UpdateFlowRun(UpdateFlowResult.CheckFailed)));
    }
}
