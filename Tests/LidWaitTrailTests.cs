using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// A sleep a keep-awake session held back has to be served when the session ends, or the machine
/// stays awake with its lid shut. The service owns a lid subscription and a suspend, so the wiring is
/// read out of the source.
/// </summary>
public class LidWaitTrailTests
{
    private static string ServiceSource() => File.ReadAllText(RepoFiles.Find("Services/LidDelayService.cs"));

    // ---- a sleep a keep-awake session held back ----------------------------------------------

    /// <summary>
    /// The session ending is the only signal that says a suppressed sleep can be served, and nothing
    /// on the lid side listened to it. Losing the subscription puts the defect straight back with
    /// every other test still passing.
    /// </summary>
    [Fact]
    public void TheLidSide_ListensForTheSessionEnding()
    {
        string source = ServiceSource();

        Assert.Contains("KeepAwakeService.StateChanged += OnKeepAwakeStateChanged", source, StringComparison.Ordinal);
        Assert.Contains("KeepAwakeService.StateChanged -= OnKeepAwakeStateChanged", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deferred sleep goes through the same suspend as every other, so the one-off stand-down is
    /// taken for it too — it does reach sleep, and a stand-down owed to a sleep that happened must
    /// not be skipped because the sleep was late.
    /// </summary>
    [Fact]
    public void TheDeferredSleep_TakesTheSameSuspendPathAsEveryOther()
    {
        string source = ServiceSource();
        string body   = SourceMethods.Body(source, "OnKeepAwakeStateChanged");

        Assert.Contains("SuspendOffThisThread(gen)", body, StringComparison.Ordinal);
        Assert.Contains("TurnOffIfDue(LidDelayOutcome.Slept)",
                        SourceMethods.Body(source, "SuspendOffThisThread"), StringComparison.Ordinal);
        // Not merely reachable — the deferred sleep must be the reason the suspend runs, and it is
        // the same call every other ending makes.
        Assert.Contains("SuspendOffThisThread(gen)", SourceMethods.Body(source, "Complete"),
                        StringComparison.Ordinal);
    }
}
