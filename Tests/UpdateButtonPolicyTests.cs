using ChargeKeeper.Helpers;
using Xunit;
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
}
