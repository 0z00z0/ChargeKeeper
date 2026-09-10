using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What runs a script and what does not. The repeat cases are the ones that cannot be tried by
/// hand: Windows sends the lid's current state again whenever listening starts, and the battery
/// report arrives every few seconds saying the same power source, so a reading mistaken for an
/// event would run a script for something that never happened.
/// </summary>
/// <remarks>The enums are internal, and a public test method cannot take one as a parameter, so the
/// cases carry the member NAME and are resolved here — which also makes a renamed member fail rather
/// than an ordinal silently pointing at a different one.</remarks>
public class ScriptTriggerPolicyTests
{
    private static LidEventKind Lid(string name) => Enum.Parse<LidEventKind>(name);
    private static ScriptTrigger Trigger(string name) => Enum.Parse<ScriptTrigger>(name);

    [Theory]
    [InlineData(nameof(LidEventKind.Closed), nameof(ScriptTrigger.LidClosed))]
    [InlineData(nameof(LidEventKind.Opened), nameof(ScriptTrigger.LidOpened))]
    public void ARealLidMovementRunsItsScripts(string kind, string expected) =>
        Assert.Equal(Trigger(expected), ScriptTriggerPolicy.ForLid(Lid(kind)));

    [Theory]
    [InlineData(nameof(LidEventKind.ClosedRepeat))]
    [InlineData(nameof(LidEventKind.OpenedRepeat))]
    [InlineData(nameof(LidEventKind.ClosedAtRegistration))]
    [InlineData(nameof(LidEventKind.OpenedAtRegistration))]
    public void ARepeatedOrReplayedLidNotificationRunsNothing(string kind) =>
        Assert.Null(ScriptTriggerPolicy.ForLid(Lid(kind)));

    [Theory]
    [InlineData(true,  nameof(ScriptTrigger.MainsConnected))]
    [InlineData(false, nameof(ScriptTrigger.MainsDisconnected))]
    public void APowerSourceEdgeRunsItsScripts(bool wentOnToMains, string expected) =>
        Assert.Equal(Trigger(expected), ScriptTriggerPolicy.ForPowerSource(wentOnToMains));

    /// <summary>A battery report that changed no power source is not an event. It arrives every few
    /// seconds, so treating one as an event would run a script continuously.</summary>
    [Fact]
    public void ABatteryReportThatChangedNothingRunsNothing() =>
        Assert.Null(ScriptTriggerPolicy.ForPowerSource(null));

    [Fact]
    public void OnlyTheScriptsBoundToTheEventRun()
    {
        var scripts = new List<ScriptDefinition>
        {
            new("a", "Pause sync",      ScriptTrigger.MainsDisconnected, "Write-Output 'a'"),
            new("b", "Resume sync",     ScriptTrigger.MainsConnected,    "Write-Output 'b'"),
            new("c", "Also on battery", ScriptTrigger.MainsDisconnected, "Write-Output 'c'"),
            new("d", "On the lid",      ScriptTrigger.LidClosed,         "Write-Output 'd'"),
        };

        Assert.Equal(["a", "c"],
                     ScriptTriggerPolicy.Matching(scripts, ScriptTrigger.MainsDisconnected)
                                        .Select(s => s.Id));
    }

    /// <summary>An empty script is left out. PowerShell run on nothing reports success, which would
    /// show a script as working when it does nothing at all.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public void AScriptWithNothingInItIsNotRun(string body)
    {
        var scripts = new List<ScriptDefinition>
        {
            new("a", "Empty", ScriptTrigger.LidClosed, body),
        };

        Assert.Empty(ScriptTriggerPolicy.Matching(scripts, ScriptTrigger.LidClosed));
    }
}
