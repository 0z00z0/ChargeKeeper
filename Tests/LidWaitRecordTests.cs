using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What a lid-close wait leaves behind in the power trail, and what a start with the lid already
/// shut decides. Both are recorded rather than inferred: a wait that says nothing while it runs
/// cannot be told apart from one that never started, and the two have opposite answers to "did the
/// application do this".
/// </summary>
public class LidWaitRecordTests
{
    private static string ServiceSource() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "LidDelayService.cs")));

    // ── A start with the lid already shut (#154) ─────────────────────────────────────────────

    [Fact]
    public void BothSidesOfTheHandBackAreRecorded()
    {
        string body = SourceMethods.Body(
            Regex.Replace(ServiceSource(), @"//[^\r\n]*", string.Empty), "OnLidState");

        Assert.Contains("HandBackUntilTheLidOpens", body, StringComparison.Ordinal);
        Assert.Contains("TakeTheOverrideBack", body, StringComparison.Ordinal);
        Assert.Contains("RestoreSavedAction", body, StringComparison.Ordinal);
        Assert.Contains("CaptureAndOverride", body, StringComparison.Ordinal);
    }
}
