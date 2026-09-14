using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;
using ZeroZero.Brand.WinUI;

namespace ChargeKeeper.Tests;

/// <summary>
/// Which update-check outcomes reach a dialog. A window that raised a dialog merely by opening would
/// interrupt someone who only wanted to read the version, and a manual check that failed without a
/// dialog would leave them believing it had worked.
/// </summary>
public class UpdateButtonPolicyTests
{
    // By name: the status type is internal and a public test method cannot take it as a parameter.
    public static TheoryData<string> EveryStatus()
    {
        var data = new TheoryData<string>();
        foreach (string name in Enum.GetNames<UpdateStatus>()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void AnAutomaticCheckNeverOpensADialog(string statusName)
    {
        var status = Enum.Parse<UpdateStatus>(statusName);
        Assert.False(UpdateButtonPolicy.ShowsNotice(status, UpdateCheckTrigger.Automatic));
        Assert.False(UpdateButtonPolicy.OpensUpdateDialog(status, UpdateCheckTrigger.Automatic));
    }

    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void TheButtonReportsOnlyAFailureInADialog(string statusName)
    {
        var status = Enum.Parse<UpdateStatus>(statusName);
        bool failure = status is not (UpdateStatus.UpToDate or UpdateStatus.Available);
        Assert.Equal(failure, UpdateButtonPolicy.ShowsNotice(status, UpdateCheckTrigger.Button));
        Assert.False(UpdateButtonPolicy.OpensUpdateDialog(status, UpdateCheckTrigger.Button));
    }

    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void TheTrayMenuReportsEveryOutcomeInADialog(string statusName)
    {
        var status = Enum.Parse<UpdateStatus>(statusName);
        bool available = status == UpdateStatus.Available;
        Assert.Equal(available,  UpdateButtonPolicy.OpensUpdateDialog(status, UpdateCheckTrigger.TrayMenu));
        Assert.Equal(!available, UpdateButtonPolicy.ShowsNotice(status, UpdateCheckTrigger.TrayMenu));
    }

    [Fact]
    public void TheButtonShowsTheOutcome()
    {
        var upToDate  = UpdateCheckService.CheckOutcome.Release(false, "1.0.0", "1.0.0", "https://example.invalid", null, null);
        var available = UpdateCheckService.CheckOutcome.Release(true, "9.9.9", "1.0.0", "https://example.invalid", null, null);

        Assert.Equal(new UpdateButtonPolicy.Look(BrandBracketButtonState.Success, "Up to date"),
                     UpdateButtonPolicy.After(upToDate));
        Assert.Equal(new UpdateButtonPolicy.Look(BrandBracketButtonState.Attention, "Update to 9.9.9"),
                     UpdateButtonPolicy.After(available));
        Assert.Equal(new UpdateButtonPolicy.Look(BrandBracketButtonState.Rest, "Check for updates"),
                     UpdateButtonPolicy.After(UpdateCheckService.CheckOutcome.NetworkUnavailable()));
    }
}
