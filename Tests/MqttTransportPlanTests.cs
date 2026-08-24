using System.Net;
using ChargeKeeper.Services;
using MQTTnet;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Which port and transport are tried, and in what order. The decision is a pure function of the
/// staged settings, the endpoint remembered for that host and the attempts already made, so the
/// whole of it is testable without a broker, a socket or a clock.
/// </summary>
public class MqttTransportPlanTests
{
    private static MqttEndpointRequest Auto(int? port = null, string host = "mq.laget.no", string user = "ck") =>
        new(host, user, port, MqttTransportSetting.Auto);

    private static MqttEndpointCandidate? First(
        MqttEndpointRequest request, MqttEndpointMemory? memory = null) =>
        MqttTransportPlan.NextEndpoint(request, memory, []);

    private static MqttEndpointCandidate? After(
        MqttEndpointRequest request, MqttEndpointMemory? memory, params MqttEndpointAttempt[] attempts) =>
        MqttTransportPlan.NextEndpoint(request, memory, attempts);

    private static MqttEndpointAttempt Failed(int port, MqttTransport transport, MqttProbeOutcome outcome) =>
        new(new MqttEndpointCandidate(port, transport), outcome);

    /// <summary>Everything a full sweep would try, as attempts that all failed to reach anything.</summary>
    private static MqttEndpointAttempt[] AllFailed(
        MqttEndpointRequest request, MqttEndpointMemory? memory = null) =>
        [.. MqttTransportPlan.Sweep(request, memory)
             .Select(c => new MqttEndpointAttempt(c, MqttProbeOutcome.Unreachable))];

    // TCP is the internal path and the cheaper one, so a cold sweep starts there and only pays for
    // WebSocket once every plain port has failed to answer.
    [Fact]
    public void Auto_WithNothingRemembered_SweepsTcpPortsBeforeWebSocketPorts()
    {
        Assert.Equal([MqttTransport.Tcp, MqttTransport.WebSocket],
            MqttTransportPlan.Order(MqttTransportSetting.Auto, null));

        var sweep = MqttTransportPlan.Sweep(Auto(), null);
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp), sweep[0]);
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp), First(Auto()));

        int firstWebSocket = sweep.ToList().FindIndex(c => c.Transport == MqttTransport.WebSocket);
        Assert.All(sweep.Take(firstWebSocket), c => Assert.Equal(MqttTransport.Tcp, c.Transport));
        Assert.All(sweep.Skip(firstWebSocket), c => Assert.Equal(MqttTransport.WebSocket, c.Transport));
    }

    [Fact]
    public void Auto_AfterEveryTcpPortFails_FallsBackToWebSocket()
    {
        var tcpDead = MqttTransportPlan.Ports(MqttTransport.Tcp)
            .Select(p => Failed(p, MqttTransport.Tcp, MqttProbeOutcome.Unreachable)).ToArray();

        var next = After(Auto(), null, tcpDead);
        Assert.Equal(MqttTransport.WebSocket, next!.Value.Transport);
        Assert.Equal(443, next.Value.Port);
    }

    // A TLS or protocol failure on one candidate says nothing about the next, so the sweep carries on.
    [Fact]
    public void Auto_ATimeoutOrProtocolFailure_DoesNotEndTheSweep()
    {
        Assert.NotNull(After(Auto(), null, Failed(1883, MqttTransport.Tcp, MqttProbeOutcome.TimedOut)));
        Assert.NotNull(After(Auto(), null, Failed(1883, MqttTransport.Tcp, MqttProbeOutcome.Failed)));
    }

    // Whatever answered last time leads, so a laptop that moves pays the full sweep once per move.
    [Fact]
    public void Auto_StartsWithTheRememberedEndpoint()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);

        Assert.Equal(new MqttEndpointCandidate(443, MqttTransport.WebSocket), First(Auto(), memory));
        // …and the transport it found leads the rest of the order, for the move back.
        Assert.Equal([MqttTransport.WebSocket, MqttTransport.Tcp],
            MqttTransportPlan.Order(MqttTransportSetting.Auto, MqttTransport.WebSocket));
    }

    // The cache is an answer about one host. Carrying it to another would probe a door that was never
    // shown to exist there, and hide the one that does.
    [Fact]
    public void CacheForADifferentHost_IsNeverReused()
    {
        var elsewhere = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);
        var request   = Auto(host: "10.0.20.22");

        Assert.Null(MqttTransportPlan.Reusable(request, elsewhere));
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp), First(request, elsewhere));
        Assert.True(MqttTransportPlan.ShouldDetect(request, elsewhere));
    }

    // A broker commonly fronts a separate listener per account, so the entry belongs to the user name
    // as much as to the host.
    [Fact]
    public void CacheForADifferentUsername_IsNeverReused()
    {
        var other = new MqttEndpointMemory("mq.laget.no", "someone-else", 443, MqttTransport.WebSocket);

        Assert.Null(MqttTransportPlan.Reusable(Auto(), other));
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp), First(Auto(), other));
    }

    // Host names are case-insensitive and get pasted with stray spaces; neither may cost a cache hit.
    [Fact]
    public void CacheMatching_IgnoresCaseAndSurroundingSpace()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);
        Assert.NotNull(MqttTransportPlan.Reusable(Auto(host: "  MQ.Laget.NO "), memory));
    }

    // The cache saves an attempt; it must never cost the connection. A remembered endpoint that has
    // stopped answering has to fall through to the full sweep, or a machine that moves is stuck.
    [Fact]
    public void ACachedAnswerThatStopsWorking_FallsThroughToAFreshSweep()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);
        var stale  = Failed(443, MqttTransport.WebSocket, MqttProbeOutcome.Unreachable);

        var next = After(Auto(), memory, stale);
        Assert.NotNull(next);
        Assert.NotEqual(new MqttEndpointCandidate(443, MqttTransport.WebSocket), next!.Value);

        // …and the sweep behind it still reaches every candidate, the internal path included.
        var sweep = MqttTransportPlan.Sweep(Auto(), memory);
        Assert.Contains(new MqttEndpointCandidate(1883, MqttTransport.Tcp), sweep);
        Assert.Equal(MqttTransportPlan.Sweep(Auto(), null).Count, sweep.Count);
    }

    // The explicit choices are the whole plan. A remembered endpoint must not creep in behind them,
    // or "TCP" would quietly mean "TCP, or whatever else works".
    [Fact]
    public void ExplicitChoice_IsHonouredEvenWhenTheOtherOneLastWorked()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);
        var pinned = new MqttEndpointRequest("mq.laget.no", "ck", null, MqttTransportSetting.Tcp);

        Assert.All(MqttTransportPlan.Sweep(pinned, memory),
            c => Assert.Equal(MqttTransport.Tcp, c.Transport));
        Assert.Equal(MqttTransport.Tcp, First(pinned, memory)!.Value.Transport);

        Assert.Equal([MqttTransport.Tcp], MqttTransportPlan.Order(MqttTransportSetting.Tcp, MqttTransport.WebSocket));
        Assert.Equal([MqttTransport.WebSocket], MqttTransportPlan.Order(MqttTransportSetting.WebSocket, MqttTransport.Tcp));
    }

    [Fact]
    public void ExplicitChoice_NeverFallsBackToTheOtherTransport()
    {
        var pinned = new MqttEndpointRequest("mq.laget.no", "ck", null, MqttTransportSetting.Tcp);
        Assert.Null(After(pinned, null, AllFailed(pinned)));
    }

    // A pinned port is the only port tried, whatever the cache or the common list say.
    [Fact]
    public void ExplicitPort_IsTheOnlyPortTried()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);
        var pinned = Auto(port: 12345);

        Assert.All(MqttTransportPlan.Sweep(pinned, memory), c => Assert.Equal(12345, c.Port));
        Assert.Equal(12345, First(pinned, memory)!.Value.Port);
        // Both transports are still in play — pinning the port says nothing about the transport.
        Assert.Equal(2, MqttTransportPlan.Sweep(pinned, null).Count);
    }

    [Fact]
    public void ExplicitPortAndTransport_LeaveExactlyOneCandidate()
    {
        var pinned = new MqttEndpointRequest("mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp);
        Assert.Equal([new MqttEndpointCandidate(8883, MqttTransport.Tcp)], MqttTransportPlan.Sweep(pinned, null));
    }

    // A broker that answered is the same broker at the next candidate: trying on would spend the
    // user's time and replace a precise verdict with a vague one.
    [Fact]
    public void AnAnsweringBrokerEndsThePlan_WhateverItAnswered()
    {
        Assert.Null(After(Auto(), null, Failed(1883, MqttTransport.Tcp, MqttProbeOutcome.AuthRejected)));
        Assert.Null(After(Auto(), null, Failed(1883, MqttTransport.Tcp, MqttProbeOutcome.Rejected)));
        Assert.Null(After(Auto(), null, Failed(1883, MqttTransport.Tcp, MqttProbeOutcome.Success)));
    }

    [Fact]
    public void EveryCandidateTried_LeavesNothingToTry()
    {
        Assert.Null(After(Auto(), null, AllFailed(Auto())));
    }

    // The sweep is bounded and free of repeats: each candidate costs a connect timeout, so a
    // duplicate is a real second of the user's time.
    [Fact]
    public void TheSweep_HasNoRepeats()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);
        foreach (var sweep in new[] { MqttTransportPlan.Sweep(Auto(), null), MqttTransportPlan.Sweep(Auto(), memory) })
            Assert.Equal(sweep.Count, sweep.Distinct().Count());
    }

    // 443 and 80 are not MQTT ports at all — they are the front-door ports a broker published through
    // a CDN or reverse proxy is reachable on, and without them such a broker cannot be found.
    [Fact]
    public void TheOfferedPorts_CoverBothMqttsOwnPortsAndTheHttpFrontDoor()
    {
        Assert.Equal([1883, 8883], MqttTransportPlan.Ports(MqttTransport.Tcp));
        foreach (int port in new[] { 443, 80, 8080, 8083, 8084, 9001 })
            Assert.Contains(port, MqttTransportPlan.Ports(MqttTransport.WebSocket));

        // The dropdown offers exactly what the sweep probes, in the same order.
        Assert.Equal(
            [.. MqttTransportPlan.Ports(MqttTransport.Tcp), .. MqttTransportPlan.Ports(MqttTransport.WebSocket)],
            MqttTransportPlan.OfferedPorts);
        Assert.Equal(MqttTransportPlan.OfferedPorts.Count, MqttTransportPlan.OfferedPorts.Distinct().Count());
    }

    // Probing costs seconds, so it only runs when there is nothing to reuse.
    [Fact]
    public void ShouldDetect_OnlyWhenNothingIsRememberedForThisHost()
    {
        var memory = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket);

        Assert.True (MqttTransportPlan.ShouldDetect(Auto(), null));
        Assert.False(MqttTransportPlan.ShouldDetect(Auto(), memory));
        Assert.True (MqttTransportPlan.ShouldDetect(Auto(host: "10.0.20.22"), memory));
        // Nothing to probe without a host.
        Assert.False(MqttTransportPlan.ShouldDetect(Auto(host: "   "), null));
    }

    [Fact]
    public void Provenance_SeparatesWhatWasFoundFromWhatWasPinned()
    {
        Assert.Equal("Automatically detected", MqttTransportPlan.AutomaticallyDetected);
        Assert.Equal("Set manually",           MqttTransportPlan.SetManually);

        Assert.Equal(MqttTransportPlan.SetManually, MqttTransportPlan.DescribeProvenance(
            new MqttEndpointRequest("mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp)));

        // Either half left to the sweep makes the answer a found one.
        Assert.Equal(MqttTransportPlan.AutomaticallyDetected, MqttTransportPlan.DescribeProvenance(Auto()));
        Assert.Equal(MqttTransportPlan.AutomaticallyDetected, MqttTransportPlan.DescribeProvenance(Auto(port: 8883)));
        Assert.Equal(MqttTransportPlan.AutomaticallyDetected, MqttTransportPlan.DescribeProvenance(
            new MqttEndpointRequest("mq.laget.no", "ck", null, MqttTransportSetting.Tcp)));
    }
}

/// <summary>How the one host/port pair maps onto each transport, and onto MQTTnet's builder.</summary>
public class MqttTransportEndpointTests
{
    // MQTT over WebSocket is served through an HTTPS front door, so a bare host on 443 is wss with
    // no port in the authority — the common case reads as the plain host it is.
    [Fact]
    public void BareHostOn443_BecomesWssWithNoPortInTheAuthority()
    {
        Assert.Equal("wss://mq.laget.no", MqttTransportEndpoint.WebSocketUri("mq.laget.no", 443, useTls: false));
        Assert.Equal("wss://mq.laget.no", MqttTransportEndpoint.WebSocketUri("  mq.laget.no  ", 443, useTls: false));
        Assert.Equal(("mq.laget.no", 443),
            MqttTransportEndpoint.Reachability("mq.laget.no", 443, MqttTransport.WebSocket, useTls: false));
    }

    // The port is a real setting on the WebSocket side too: it picks the authority, and with the
    // encryption switch it picks the scheme.
    [Fact]
    public void WebSocketPort_PicksTheSchemeAndAppearsInTheAuthority()
    {
        Assert.Equal("ws://mq.laget.no",       MqttTransportEndpoint.WebSocketUri("mq.laget.no", 80, useTls: false));
        Assert.Equal("ws://mq.laget.no:9001",  MqttTransportEndpoint.WebSocketUri("mq.laget.no", 9001, useTls: false));
        // 8084 is served over TLS by convention, so it resolves to wss without the switch being found.
        Assert.Equal("wss://mq.laget.no:8084", MqttTransportEndpoint.WebSocketUri("mq.laget.no", 8084, useTls: false));
        // …and the switch puts any port on wss.
        Assert.Equal("wss://mq.laget.no:9001", MqttTransportEndpoint.WebSocketUri("mq.laget.no", 9001, useTls: true));

        Assert.Equal(("mq.laget.no", 9001),
            MqttTransportEndpoint.Reachability("mq.laget.no", 9001, MqttTransport.WebSocket, useTls: false));
    }

    // The escape hatch for a broker served under a path, which a port alone cannot express.
    [Fact]
    public void HostWrittenAsAUri_IsHonouredAsWritten()
    {
        Assert.Equal("ws://10.0.0.5:9001", MqttTransportEndpoint.WebSocketUri("ws://10.0.0.5:9001", 443, useTls: true));
        Assert.Equal("wss://mq.laget.no/mqtt", MqttTransportEndpoint.WebSocketUri("wss://mq.laget.no/mqtt", 80, useTls: false));

        Assert.Equal(("10.0.0.5", 9001),
            MqttTransportEndpoint.Reachability("ws://10.0.0.5:9001", 1883, MqttTransport.WebSocket, useTls: false));
        Assert.Equal(("10.0.0.5", 80),
            MqttTransportEndpoint.Reachability("ws://10.0.0.5", 1883, MqttTransport.WebSocket, useTls: false));
    }

    [Fact]
    public void Tcp_UsesTheHostAndPortAsGiven_Clamped()
    {
        Assert.Equal(("mq.laget.no", 1883),
            MqttTransportEndpoint.Reachability(" mq.laget.no ", 1883, MqttTransport.Tcp, useTls: false));
        // A hand-edited settings.json reaches this unchecked, and MQTTnet throws on an out-of-range port.
        Assert.Equal(("mq.laget.no", 65535),
            MqttTransportEndpoint.Reachability("mq.laget.no", 99999, MqttTransport.Tcp, useTls: false));
        Assert.Equal(("mq.laget.no", 1),
            MqttTransportEndpoint.Reachability("mq.laget.no", 0, MqttTransport.Tcp, useTls: false));
    }

    // Pins the MQTTnet 5 builder calls: WithWebSocketServer takes a nested builder whose WithUri
    // carries the scheme, and the wss scheme is what puts the handshake on TLS.
    [Fact]
    public void WebSocketOptions_CarryTheWssUriAndTls()
    {
        var options = new MqttClientOptionsBuilder()
            .WithTransport(MqttTransport.WebSocket, "mq.laget.no", 443, useTls: false)
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
