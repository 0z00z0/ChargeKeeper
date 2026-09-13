using System;
using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What joining and leaving a network profile runs. Both cases here run a PowerShell script on a
/// person's machine, and neither can be tried by hand: the application starting where it already is
/// must run nothing, and a dock rebind — which resolves to no network at all for a few seconds —
/// must not read as leaving and arriving again.
/// </summary>
public class NetworkProfileTransitionsTests
{
    private const string Office = "office-profile";
    private const string Home   = "home-profile";

    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Describe(System.Collections.Generic.IReadOnlyList<NetworkProfileTransition> fired) =>
        string.Join(";", fired.Select(f => $"{f.Trigger} {f.ProfileId}"));

    [Fact]
    public void StartingWhereTheMachineAlreadyIsRunsNothing()
    {
        var transitions = new NetworkProfileTransitions();

        // The baseline, and then the first reading of the same network the machine was already on.
        transitions.Seed(Office);

        Assert.Empty(transitions.Observe(Office, nothingDetected: false, T0));
    }

    [Fact]
    public void AReadingBeforeAnyBaselineOnlyTakesTheBaseline()
    {
        var transitions = new NetworkProfileTransitions();

        Assert.Empty(transitions.Observe(Office, nothingDetected: false, T0));
        Assert.Empty(transitions.Observe(Office, nothingDetected: false, T0.AddSeconds(30)));
    }

    [Fact]
    public void MovingToAnotherProfileLeavesTheOldOneAndJoinsTheNew()
    {
        var transitions = new NetworkProfileTransitions();
        transitions.Seed(Office);

        Assert.Equal($"NetworkLeft {Office};NetworkJoined {Home}",
                     Describe(transitions.Observe(Home, nothingDetected: false, T0)));
    }

    [Fact]
    public void ABriefDropBackToTheSameProfileRunsNothing()
    {
        var transitions = new NetworkProfileTransitions();
        transitions.Seed(Office);

        // The failed read a dock rebind produces, then the same network again inside the window.
        Assert.Empty(transitions.Observe(null, nothingDetected: true, T0));
        Assert.Empty(transitions.Observe(Office, nothingDetected: false,
                                         T0 + NetworkProfileTransitions.SettleWindow - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ADropThatDoesNotComeBackLeavesOnceTheWindowHasRunOut()
    {
        var transitions = new NetworkProfileTransitions();
        transitions.Seed(Office);
        transitions.Observe(null, nothingDetected: true, T0);

        Assert.Empty(transitions.Expire(T0 + NetworkProfileTransitions.SettleWindow - TimeSpan.FromSeconds(1)));
        Assert.Equal($"NetworkLeft {Office}",
                     Describe(transitions.Expire(T0 + NetworkProfileTransitions.SettleWindow)));
    }

    [Fact]
    public void ADropThatComesBackOnAnotherProfileLeavesTheOldOneAndJoinsTheNew()
    {
        var transitions = new NetworkProfileTransitions();
        transitions.Seed(Office);
        transitions.Observe(null, nothingDetected: true, T0);

        Assert.Equal($"NetworkLeft {Office};NetworkJoined {Home}",
                     Describe(transitions.Observe(Home, nothingDetected: false, T0.AddSeconds(2))));
    }
}
