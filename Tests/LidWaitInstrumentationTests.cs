using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What the power trail has to be able to answer about a lid-close wait after the fact: what sort of
/// machine produced the record, whether the hold Windows was asked for was accepted, and which
/// conditions were in play — including the ones that were switched off. A condition recorded only
/// when it armed reads, later, as a condition that was never configured.
/// </summary>
public class LidWaitInstrumentationTests
{
    private static string LidSource()   => File.ReadAllText(RepoFiles.Find("Services/LidDelayService.cs"));
    private static string HolderSource()=> File.ReadAllText(RepoFiles.Find("Services/ExecutionStateHolder.cs"));
    private static string NativeSource()=> File.ReadAllText(RepoFiles.Find("Helpers/NativeMethods.cs"));

    // ---- what sort of standby this machine does ------------------------------------------------

    [Fact]
    public void TheStandbyFlagsAreReadFromTheirOwnPlacesInThePowerCapabilities()
    {
        // Every field before them is one byte, so the field index is the byte index. A wrong offset
        // reads a neighbouring flag and states the wrong sleep type with complete confidence.
        string source = NativeSource();

        Assert.Contains("SystemS3Offset = 5", source, StringComparison.Ordinal);
        Assert.Contains("AoAcOffset     = 20", source, StringComparison.Ordinal);
    }

    // ---- what the delay can honestly promise on this machine -----------------------------------

    /// <summary>
    /// A Modern Standby machine enters standby on its own idle rules while a wait is running, and the
    /// hold does not reliably prevent it. The feature appearing to work is the fault, so the
    /// limitation is stated where somebody deciding whether to switch it on will read it.
    /// </summary>
    [Fact]
    public void AModernStandbyMachine_IsToldTheDelayMayNotHold()
    {
        string? caveat = StandbyCapability.LidWaitCaveat(
            new StandbyCapability(ModernStandby: true, SupportsS3: false));

        Assert.NotNull(caveat);
        Assert.Contains("Modern Standby", caveat!, StringComparison.Ordinal);
        Assert.Contains("sleep sooner than the delay says", caveat!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLidDelayPage_ShowsTheCaveat() =>
        // The one surface it belongs on. Losing the call leaves the page promising a delay this class
        // of machine does not reliably keep, which is the state it was added against.
        Assert.Contains("StandbyCapability.LidWaitCaveat(StandbyCapability.Read())",
                        File.ReadAllText(RepoFiles.Find("UI/SettingsWindow.xaml.cs")),
                        StringComparison.Ordinal);

    // ---- was the execution-state hold accepted -------------------------------------------------

    [Fact]
    public void BothFeaturesShareOneHolderThatRecordsWhatWindowsMadeOfTheHold()
    {
        // #171 extracted the two near-copies into one holder loop, shared by both services, so a
        // refusal is now impossible to lose from one side alone by construction rather than by
        // convention.
        string body = SourceMethods.Body(HolderSource(), "Loop");

        Assert.Contains("uint previous = NativeMethods.SetThreadExecutionState(flags)",
                        body, StringComparison.Ordinal);
        Assert.Contains("ExecutionStateHold.Outcome(previous)", body, StringComparison.Ordinal);
    }

    // ---- which conditions armed, positively and negatively -------------------------------------

    [Fact]
    public void EveryConditionOfALidCloseIsRecordedInBothDirections()
    {
        // A field report described a wait as having a battery target that had been switched off
        // seconds before the lid closed. "Off" has to be as explicit in the trail as a value is.
        string body = SourceMethods.Body(LidSource(), "StartDelay");

        Assert.Contains("\"No delay timer on this lid close\", \"the timer condition is off\"",
                        body, StringComparison.Ordinal);
        Assert.Contains("\"No temperature ceiling on this lid close\", \"the setting is off\"",
                        body, StringComparison.Ordinal);
        // The battery target's own negatives are LidTargetArming's, and are always written.
        Assert.Contains("LidTargetArming.Describe", body, StringComparison.Ordinal);
    }
}
