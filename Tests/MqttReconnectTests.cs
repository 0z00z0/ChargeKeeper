using System;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure reconnect-backoff step for the MQTT maintain loop (issue #41). The rest of the connection
// robustness (keep-alive ping, wake-on-disconnect, resume reconnect) is I/O against a live broker
// and isn't unit-tested here.
public class MqttReconnectTests
{
    [Fact]
    public void NextBackoff_Doubles()
    {
        Assert.Equal(TimeSpan.FromSeconds(6),  HomeAssistantService.NextBackoff(TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(24), HomeAssistantService.NextBackoff(TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void NextBackoff_CapsAt60s()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), HomeAssistantService.NextBackoff(TimeSpan.FromSeconds(40)));
        Assert.Equal(TimeSpan.FromSeconds(60), HomeAssistantService.NextBackoff(TimeSpan.FromSeconds(60)));
    }

    // Wake-gating (review fix): a DisconnectedAsync must wake the maintain loop for an early reconnect
    // ONLY when a genuinely-live connection dropped. MQTTnet also raises DisconnectedAsync with
    // ClientWasConnected=false when ConnectAsync itself fails; waking on that short-circuits the
    // exponential backoff into continuous reconnect hammering.
    [Fact]
    public void ShouldWakeOnDisconnect_OnlyForALiveConnectionDrop()
    {
        Assert.True (HomeAssistantService.ShouldWakeOnDisconnect(enabled: true,  clientWasConnected: true));
        Assert.False(HomeAssistantService.ShouldWakeOnDisconnect(enabled: true,  clientWasConnected: false)); // failed connect
        Assert.False(HomeAssistantService.ShouldWakeOnDisconnect(enabled: false, clientWasConnected: true));  // shutting down
        Assert.False(HomeAssistantService.ShouldWakeOnDisconnect(enabled: false, clientWasConnected: false));
    }

    // Flap guard (review fix): a session that lived past the stability threshold reconnects fast; one
    // that dropped almost immediately is a flap and keeps escalating the backoff.
    [Fact]
    public void IsStableConnection_DiscriminatesFlapFromGenuineSession()
    {
        Assert.False(HomeAssistantService.IsStableConnection(TimeSpan.FromMilliseconds(200)));
        Assert.False(HomeAssistantService.IsStableConnection(TimeSpan.FromSeconds(5)));
        Assert.True (HomeAssistantService.IsStableConnection(TimeSpan.FromSeconds(30)));
        Assert.True (HomeAssistantService.IsStableConnection(TimeSpan.FromMinutes(10)));
    }
}

// Coalescing gate (review fix): a burst of charge-control StateChanged signals must collapse into at
// most one in-flight fresh vendor read plus one trailing read, instead of one read per signal.
public class CoalescingGateTests
{
    [Fact]
    public void FirstSignal_StartsTheLoop_AndFinishesWhenNoMoreArrive()
    {
        var gate = new CoalescingGate();
        Assert.True(gate.Signal());     // first signal → this caller runs the loop
        gate.BeginPass();
        Assert.False(gate.ShouldRepeat());  // nothing else queued → loop ends
    }

    [Fact]
    public void SignalWhileRunning_DoesNotStartASecondLoop_ButArmsATrailingPass()
    {
        var gate = new CoalescingGate();
        Assert.True(gate.Signal());     // loop owner
        gate.BeginPass();

        // Three more signals arrive mid-burst — none starts a second loop.
        Assert.False(gate.Signal());
        Assert.False(gate.Signal());
        Assert.False(gate.Signal());

        // They collapse into exactly ONE trailing pass, then the loop ends.
        Assert.True(gate.ShouldRepeat());
        gate.BeginPass();
        Assert.False(gate.ShouldRepeat());
    }

    [Fact]
    public void AfterLoopEnds_ANewSignal_StartsAFreshLoop()
    {
        var gate = new CoalescingGate();
        Assert.True(gate.Signal());
        gate.BeginPass();
        Assert.False(gate.ShouldRepeat());   // loop ended, running flag cleared

        Assert.True(gate.Signal());          // a later, separate change starts a new loop
    }
}
