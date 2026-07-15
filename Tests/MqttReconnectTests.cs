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
}
