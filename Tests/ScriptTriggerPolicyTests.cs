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

    [Theory]
    [InlineData(nameof(LidEventKind.ClosedRepeat))]
    [InlineData(nameof(LidEventKind.ClosedAtRegistration))]
    public void ARepeatedOrReplayedLidNotificationRunsNothing(string kind) =>
        Assert.Null(ScriptTriggerPolicy.ForLid(Lid(kind)));

    /// <summary>A battery report that changed no power source is not an event. It arrives every few
    /// seconds, so treating one as an event would run a script continuously.</summary>
    [Fact]
    public void ABatteryReportThatChangedNothingRunsNothing() =>
        Assert.Null(ScriptTriggerPolicy.ForPowerSource(null));
}
