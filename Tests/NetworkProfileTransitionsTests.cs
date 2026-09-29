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

    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartingWhereTheMachineAlreadyIsRunsNothing()
    {
        var transitions = new NetworkProfileTransitions();

        // The baseline, and then the first reading of the same network the machine was already on.
        transitions.Seed(Office);

        Assert.Empty(transitions.Observe(Office, nothingDetected: false, T0));
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
}
