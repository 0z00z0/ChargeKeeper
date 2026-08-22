using System.Net;
using ChargeKeeper.Services;
using MQTTnet;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Which transport is tried, and in what order. The decision is a pure function of the setting, the
/// transport that last worked and the attempts already made, so the whole of it is testable without
/// a broker, a socket or a clock.
/// </summary>
public class MqttTransportPlanTests
{
    private static MqttTransport? First(MqttTransportSetting setting, MqttTransport? lastGood = null) =>
        MqttTransportPlan.Next(setting, lastGood, []);

    private static MqttTransport? After(
        MqttTransportSetting setting, MqttTransport? lastGood, params MqttTransportAttempt[] attempts) =>
        MqttTransportPlan.Next(setting, lastGood, attempts);

    private static MqttTransportAttempt Failed(MqttTransport transport, MqttProbeOutcome outcome) =>
        new(transport, outcome);

    // TCP is the internal path and the cheaper one, so a cold Auto starts there and only pays for
    // WebSocket when TCP does not answer.
    [Fact]
    public void Auto_WithNothingRemembered_TriesTcpFirstThenWebSocket()
    {
        Assert.Equal([MqttTransport.Tcp, MqttTransport.WebSocket], MqttTransportPlan.Order(MqttTransportSetting.Auto, null));
        Assert.Equal(MqttTransport.Tcp, First(MqttTransportSetting.Auto));
    }

    [Fact]
    public void Auto_AfterTcpFailsToReachAnything_FallsBackToWebSocket()
    {
        Assert.Equal(MqttTransport.WebSocket,
            After(MqttTransportSetting.Auto, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.Unreachable)));
        Assert.Equal(MqttTransport.WebSocket,
            After(MqttTransportSetting.Auto, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.TimedOut)));
        // A TLS or protocol failure on one transport says nothing about the other, so it falls back too.
        Assert.Equal(MqttTransport.WebSocket,
            After(MqttTransportSetting.Auto, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.Failed)));
    }

    // Whichever answered last time leads, so a laptop that moves pays the full probe once per move.
    [Fact]
    public void Auto_StartsWithWhateverConnectedLastTime()
    {
        Assert.Equal(MqttTransport.WebSocket, First(MqttTransportSetting.Auto, MqttTransport.WebSocket));
        Assert.Equal([MqttTransport.WebSocket, MqttTransport.Tcp],
            MqttTransportPlan.Order(MqttTransportSetting.Auto, MqttTransport.WebSocket));

        // …and the fallback still exists in that order, for the move back.
        Assert.Equal(MqttTransport.Tcp, After(MqttTransportSetting.Auto, MqttTransport.WebSocket,
            Failed(MqttTransport.WebSocket, MqttProbeOutcome.Unreachable)));
    }

    // The explicit choices are the whole plan. A remembered transport must not creep in behind them,
    // or "TCP" would quietly mean "TCP, or whatever else works".
    [Fact]
    public void ExplicitChoice_IsHonouredEvenWhenTheOtherOneLastWorked()
    {
        Assert.Equal(MqttTransport.Tcp,       First(MqttTransportSetting.Tcp, MqttTransport.WebSocket));
        Assert.Equal(MqttTransport.WebSocket, First(MqttTransportSetting.WebSocket, MqttTransport.Tcp));

        Assert.Equal([MqttTransport.Tcp],       MqttTransportPlan.Order(MqttTransportSetting.Tcp, MqttTransport.WebSocket));
        Assert.Equal([MqttTransport.WebSocket], MqttTransportPlan.Order(MqttTransportSetting.WebSocket, MqttTransport.Tcp));
    }

    [Fact]
    public void ExplicitChoice_NeverFallsBackToTheOtherTransport()
    {
        Assert.Null(After(MqttTransportSetting.Tcp, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.Unreachable)));
        Assert.Null(After(MqttTransportSetting.WebSocket, null, Failed(MqttTransport.WebSocket, MqttProbeOutcome.TimedOut)));
    }

    // A broker that answered is the same broker over the other transport: trying again would spend
    // the user's time and replace a precise verdict with a vague one.
    [Fact]
    public void AnAnsweringBrokerEndsThePlan_WhateverItAnswered()
    {
        Assert.Null(After(MqttTransportSetting.Auto, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.AuthRejected)));
        Assert.Null(After(MqttTransportSetting.Auto, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.Rejected)));
        Assert.Null(After(MqttTransportSetting.Auto, null, Failed(MqttTransport.Tcp, MqttProbeOutcome.Success)));
    }

    [Fact]
    public void BothTransportsTried_LeavesNothingToTry()
    {
        Assert.Null(After(MqttTransportSetting.Auto, null,
            Failed(MqttTransport.Tcp, MqttProbeOutcome.Unreachable),
            Failed(MqttTransport.WebSocket, MqttProbeOutcome.Unreachable)));
    }
}

/// <summary>How the one host/port pair maps onto each transport, and onto MQTTnet's builder.</summary>
public class MqttTransportEndpointTests
{
    // There is no WebSocket port box: MQTT over WebSocket is served through an HTTPS front door, so
    // a bare host means wss on 443.
    [Fact]
    public void BareHost_BecomesWssOn443()
    {
        Assert.Equal("wss://mq.laget.no", MqttTransportEndpoint.WebSocketUri("mq.laget.no"));
        Assert.Equal("wss://mq.laget.no", MqttTransportEndpoint.WebSocketUri("  mq.laget.no  "));
        Assert.Equal(("mq.laget.no", 443),
            MqttTransportEndpoint.Reachability("mq.laget.no", 1883, MqttTransport.WebSocket));
    }

    // The escape hatch for a broker that is not behind 443, in place of a path or port field whose
    // only correct value is nearly always the default.
    [Fact]
    public void HostWrittenAsAUri_IsHonouredAsWritten()
    {
        Assert.Equal("ws://10.0.0.5:9001", MqttTransportEndpoint.WebSocketUri("ws://10.0.0.5:9001"));
        Assert.Equal("wss://mq.laget.no/mqtt", MqttTransportEndpoint.WebSocketUri("wss://mq.laget.no/mqtt"));

        Assert.Equal(("10.0.0.5", 9001), MqttTransportEndpoint.Reachability("ws://10.0.0.5:9001", 1883, MqttTransport.WebSocket));
        Assert.Equal(("10.0.0.5", 80),   MqttTransportEndpoint.Reachability("ws://10.0.0.5", 1883, MqttTransport.WebSocket));
    }

    [Fact]
    public void Tcp_UsesTheHostAndPortAsGiven_Clamped()
    {
        Assert.Equal(("mq.laget.no", 1883), MqttTransportEndpoint.Reachability(" mq.laget.no ", 1883, MqttTransport.Tcp));
        // A hand-edited settings.json reaches this unchecked, and MQTTnet throws on an out-of-range port.
        Assert.Equal(("mq.laget.no", 65535), MqttTransportEndpoint.Reachability("mq.laget.no", 99999, MqttTransport.Tcp));
        Assert.Equal(("mq.laget.no", 1),     MqttTransportEndpoint.Reachability("mq.laget.no", 0, MqttTransport.Tcp));
    }

    // Pins the MQTTnet 5 builder calls: WithWebSocketServer takes a nested builder whose WithUri
    // carries the scheme, and the wss scheme is what puts the handshake on TLS.
    [Fact]
    public void WebSocketOptions_CarryTheWssUriAndTls()
    {
        var options = new MqttClientOptionsBuilder()
            .WithTransport(MqttTransport.WebSocket, "mq.laget.no", 1883, useTls: false)
            .WithClientId("chargekeeper")
            .Build();

        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        Assert.Equal("wss://mq.laget.no", channel.Uri);
        Assert.True(channel.TlsOptions.UseTls);
        // MQTTnet asks for the "mqtt" subprotocol itself; the broker's front door selects on it.
        Assert.Contains("mqtt", channel.SubProtocols);
        // Validation is left to the platform: no handler means the default chain check applies.
        Assert.Null(channel.TlsOptions.CertificateValidationHandler);
        Assert.False(channel.TlsOptions.AllowUntrustedCertificates);
    }

    [Fact]
    public void PlainWebSocketUri_IsNotForcedOntoTls()
    {
        var options = new MqttClientOptionsBuilder()
            .WithTransport(MqttTransport.WebSocket, "ws://10.0.0.5:9001", 1883, useTls: true)
            .WithClientId("chargekeeper")
            .Build();

        var channel = Assert.IsType<MqttClientWebSocketOptions>(options.ChannelOptions);
        Assert.Equal("ws://10.0.0.5:9001", channel.Uri);
    }

    [Fact]
    public void TcpOptions_CarryTheHostPortAndTheTlsToggle()
    {
        var plain = new MqttClientOptionsBuilder()
            .WithTransport(MqttTransport.Tcp, "mq.laget.no", 1883, useTls: false)
            .WithClientId("chargekeeper").Build();
        var secured = new MqttClientOptionsBuilder()
            .WithTransport(MqttTransport.Tcp, "mq.laget.no", 8883, useTls: true)
            .WithClientId("chargekeeper").Build();

        var endpoint = Assert.IsType<DnsEndPoint>(
            Assert.IsType<MqttClientTcpOptions>(plain.ChannelOptions).RemoteEndpoint);
        Assert.Equal("mq.laget.no", endpoint.Host);
        Assert.Equal(1883, endpoint.Port);
        Assert.False(Assert.IsType<MqttClientTcpOptions>(plain.ChannelOptions).TlsOptions.UseTls);
        Assert.True(Assert.IsType<MqttClientTcpOptions>(secured.ChannelOptions).TlsOptions.UseTls);
    }
}
