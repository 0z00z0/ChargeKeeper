using System;
using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The words the seven readings drawn from a declared list may publish.
/// </summary>
/// <remarks>
/// A receiver holds the state of one of these against the list announced with it and rejects
/// anything not on it, so the words are part of the published contract in the way a display name is
/// not: changing one breaks every automation and template comparing against it. The literals here
/// are spelled in full rather than composed from the source they guard, so a changed word fails
/// instead of being followed.
/// </remarks>
public class MqttEnumSensorTests
{
    [Fact]
    public void TheLidCloseWaitWords_AreTheOnesAReceiverAlreadyMatchesOn() =>
        Assert.Equal(
            [
                "Off",
                "Idle",
                "Waiting for the timer",
                "Waiting for the battery target",
                "Waiting for the timer or the battery target",
                "Waiting with nothing left to reach",
            ],
            LidWaitStates.Words);

    [Fact]
    public void TheLastChangeWords_AreTheOnesAReceiverAlreadyMatchesOn() =>
        Assert.Equal(
            [
                "Lid closed",
                "Lid opened",
                "Wait ended with nothing to wait for",
                "Wait ended on the delay",
                "Wait ended on the battery target",
                "Wait ended on the temperature ceiling",
                "Keep awake started",
                "Keep awake ended",
            ],
            AppChangeLog.Words);

    [Fact]
    public void TheLastLidEventWords_AreTheOnesAReceiverAlreadyMatchesOn() =>
        Assert.Equal(
            [
                "Closed",
                "Opened",
                "Closed again, a repeat",
                "Opened again, a repeat",
                "Closed at registration",
                "Opened at registration",
            ],
            LidEventLog.Words);

    [Fact]
    public void ThePowerStateWords_AreTheOnesAReceiverAlreadyMatchesOn() =>
        Assert.Equal(["Discharging", "Charging", "Idle on mains"], PowerStates.Words);

    [Fact]
    public void TheBatteryHealthWords_AreTheOnesAReceiverAlreadyMatchesOn() =>
        Assert.Equal(["Good", "Degraded", "Poor"], LiveStateBuilder.HealthWords);

    [Fact]
    public void TheBatteryStateWords_AreTheOnesAReceiverAlreadyMatchesOn() =>
        Assert.Equal(["Charging", "Not Charging", "Full"], LiveStateBuilder.BatteryStateWords);
}
