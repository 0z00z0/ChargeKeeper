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
    private static MqttEndpointRequest Auto(int? port = null, string host = "mq.laget.no", string user = "ck",
        MqttEncryptionSetting encryption = MqttEncryptionSetting.Auto) =>
        new(host, user, port, MqttTransportSetting.Auto, encryption);

    /// <summary>A cache entry, complete. 443 is a WebSocket front door, so what was found there was
    /// encrypted whatever anyone asked for.</summary>
    private static MqttEndpointMemory Found(
        int port = 443, MqttTransport transport = MqttTransport.WebSocket, bool? encrypted = true,
        string host = "mq.laget.no", string user = "ck") =>
        new(host, user, port, transport, encrypted);

    private static MqttEndpointCandidate? First(
        MqttEndpointRequest request, MqttEndpointMemory? memory = null) =>
        MqttTransportPlan.NextEndpoint(request, memory, []);

    private static MqttEndpointCandidate? After(
        MqttEndpointRequest request, MqttEndpointMemory? memory, params MqttEndpointAttempt[] attempts) =>
        MqttTransportPlan.NextEndpoint(request, memory, attempts);

    private static MqttEndpointAttempt Failed(
        int port, MqttTransport transport, MqttProbeOutcome outcome, bool encrypted = false) =>
        new(new MqttEndpointCandidate(port, transport, encrypted), outcome);

    /// <summary>Every candidate on one transport, as attempts that reached nothing.</summary>
    private static MqttEndpointAttempt[] AllFailedOn(
        MqttTransport transport, MqttEndpointRequest request, MqttEndpointMemory? memory = null) =>
        [.. MqttTransportPlan.Sweep(request, memory)
             .Where(c => c.Transport == transport)
             .Select(c => new MqttEndpointAttempt(c, MqttProbeOutcome.Unreachable))];

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
        // Encrypted leads within the pair, and the plain retry for that same port comes before the
        // next port is touched at all.
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: true),  sweep[0]);
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false), sweep[1]);
        Assert.Equal(sweep[0], First(Auto()));

        int firstWebSocket = sweep.ToList().FindIndex(c => c.Transport == MqttTransport.WebSocket);
        Assert.All(sweep.Take(firstWebSocket), c => Assert.Equal(MqttTransport.Tcp, c.Transport));
        Assert.All(sweep.Skip(firstWebSocket), c => Assert.Equal(MqttTransport.WebSocket, c.Transport));
    }

    [Fact]
    public void Auto_AfterEveryTcpPortFails_FallsBackToWebSocket()
    {
        var next = After(Auto(), null, AllFailedOn(MqttTransport.Tcp, Auto()));
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
        var memory = Found();

        Assert.Equal(new MqttEndpointCandidate(443, MqttTransport.WebSocket, Encrypted: true),
            First(Auto(), memory));
        // …and the transport it found leads the rest of the order, for the move back.
        Assert.Equal([MqttTransport.WebSocket, MqttTransport.Tcp],
            MqttTransportPlan.Order(MqttTransportSetting.Auto, MqttTransport.WebSocket));
    }

    // The cache is an answer about one host. Carrying it to another would probe a door that was never
    // shown to exist there, and hide the one that does.
    [Fact]
    public void CacheForADifferentHost_IsNeverReused()
    {
        var elsewhere = Found();
        var request   = Auto(host: "10.0.20.22");

        Assert.Null(MqttTransportPlan.Reusable(request, elsewhere));
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: true),
            First(request, elsewhere));
    }

    // A broker commonly fronts a separate listener per account, so the entry belongs to the user name
    // as much as to the host.
    [Fact]
    public void CacheForADifferentUsername_IsNeverReused()
    {
        var other = Found(user: "someone-else");

        Assert.Null(MqttTransportPlan.Reusable(Auto(), other));
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: true),
            First(Auto(), other));
    }

    // Host names are case-insensitive and get pasted with stray spaces; neither may cost a cache hit.
    [Fact]
    public void CacheMatching_IgnoresCaseAndSurroundingSpace()
    {
        var memory = Found();
        Assert.NotNull(MqttTransportPlan.Reusable(Auto(host: "  MQ.Laget.NO "), memory));
    }

    // The cache saves an attempt; it must never cost the connection. A remembered endpoint that has
    // stopped answering has to fall through to the full sweep, or a machine that moves is stuck.
    [Fact]
    public void ACachedAnswerThatStopsWorking_FallsThroughToAFreshSweep()
    {
        var memory = Found();
        var stale  = Failed(443, MqttTransport.WebSocket, MqttProbeOutcome.Unreachable, encrypted: true);

        var next = After(Auto(), memory, stale);
        Assert.NotNull(next);
        Assert.NotEqual(new MqttEndpointCandidate(443, MqttTransport.WebSocket, Encrypted: true), next!.Value);

        // …and the sweep behind it still reaches every candidate, the internal path included.
        var sweep = MqttTransportPlan.Sweep(Auto(), memory);
        Assert.Contains(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false), sweep);
        Assert.Equal(MqttTransportPlan.Sweep(Auto(), null).Count, sweep.Count);
    }

    // The explicit choices are the whole plan. A remembered endpoint must not creep in behind them,
    // or "TCP" would quietly mean "TCP, or whatever else works".
    [Fact]
    public void ExplicitChoice_IsHonouredEvenWhenTheOtherOneLastWorked()
    {
        var memory = Found();
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
        var memory = Found();
        var pinned = Auto(port: 12345);

        Assert.All(MqttTransportPlan.Sweep(pinned, memory), c => Assert.Equal(12345, c.Port));
        Assert.Equal(12345, First(pinned, memory)!.Value.Port);
        // Both transports and both schemes are still in play — pinning the port says nothing about
        // either of them.
        Assert.Equal(4, MqttTransportPlan.Sweep(pinned, null).Count);
    }

    [Fact]
    public void EveryHalfPinned_LeavesExactlyOneCandidate()
    {
        var pinned = new MqttEndpointRequest(
            "mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp, MqttEncryptionSetting.On);
        Assert.Equal([new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: true)],
            MqttTransportPlan.Sweep(pinned, null));

        // Leaving the encryption on Automatic leaves the one thing still to find.
        var half = new MqttEndpointRequest("mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp);
        Assert.Equal(
            [new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: true),
             new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: false)],
            MqttTransportPlan.Sweep(half, null));
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
        var memory = Found();
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

    // Probing puts the machine on the network, so it follows something the user did.
    [Fact]
    public void EveryExplicitAction_Probes()
    {
        // Every member, read off the enum, so a trigger added without a decision here fails loudly.
        foreach (var trigger in Enum.GetValues<MqttProbeTrigger>())
        {
            Assert.True(MqttTransportPlan.ShouldProbe(trigger, publishingEnabled: true, "mq.laget.no"));

            // Publishing off means no network at all, and a blank host has nothing to probe.
            Assert.False(MqttTransportPlan.ShouldProbe(trigger, publishingEnabled: false, "mq.laget.no"));
            Assert.False(MqttTransportPlan.ShouldProbe(trigger, publishingEnabled: true,  "   "));
            Assert.False(MqttTransportPlan.ShouldProbe(trigger, publishingEnabled: true,  ""));
        }
    }

    // Fail closed: a trigger the list has not been taught about probes nothing, so adding a member
    // for something passive — a page shown, a timer — cannot start a sweep by default.
    [Fact]
    public void AnUnlistedTrigger_NeverProbes() =>
        Assert.False(MqttTransportPlan.ShouldProbe((MqttProbeTrigger)99, publishingEnabled: true, "mq.laget.no"));

    // What is remembered orders the sweep; it never decides whether there is one.
    [Fact]
    public void ARememberedEndpoint_DoesNotSuppressTheProbe()
    {
        Assert.True(MqttTransportPlan.ShouldProbe(
            MqttProbeTrigger.BrokerSettingChanged, publishingEnabled: true, "mq.laget.no"));
        Assert.Equal(new MqttEndpointCandidate(443, MqttTransport.WebSocket, Encrypted: true),
            First(Auto(), Found()));
    }

    [Fact]
    public void Provenance_SeparatesWhatWasFoundFromWhatWasPinned()
    {
        Assert.Equal("Automatically detected", MqttTransportPlan.AutomaticallyDetected);
        Assert.Equal("Set manually",           MqttTransportPlan.SetManually);

        var pinned = new MqttEndpointRequest(
            "mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp, MqttEncryptionSetting.On);
        Assert.StartsWith(MqttTransportPlan.SetManually,
            MqttTransportPlan.DescribeProvenance(pinned, null));

        // Any part left to the sweep makes the answer a found one.
        foreach (var request in new[]
        {
            Auto(),
            Auto(port: 8883),
            new MqttEndpointRequest("mq.laget.no", "ck", null, MqttTransportSetting.Tcp),
            // Port and transport pinned, encryption still to find.
            new MqttEndpointRequest("mq.laget.no", "ck", 8883, MqttTransportSetting.Tcp),
        })
            Assert.StartsWith(MqttTransportPlan.AutomaticallyDetected,
                MqttTransportPlan.DescribeProvenance(request, null));
    }

    // Automatic can fall back to clear text with nobody choosing it, so the row that says where the
    // settings came from also has to say what is on the wire.
    [Fact]
    public void Provenance_StatesWhetherTheLinkIsEncrypted()
    {
        // Nothing settled yet: the clause is left off rather than guessed at.
        Assert.Equal(MqttTransportPlan.AutomaticallyDetected,
            MqttTransportPlan.DescribeProvenance(Auto(), null));

        // What actually connected is the answer, whichever way it went.
        Assert.Equal("Automatically detected — encrypted",
            MqttTransportPlan.DescribeProvenance(Auto(), Found(8883, MqttTransport.Tcp, encrypted: true)));
        Assert.Equal("Automatically detected — not encrypted",
            MqttTransportPlan.DescribeProvenance(Auto(), Found(1883, MqttTransport.Tcp, encrypted: false)));

        // With nothing connected, an explicit choice answers for itself.
        Assert.Equal("Automatically detected — encrypted",
            MqttTransportPlan.DescribeProvenance(Auto(encryption: MqttEncryptionSetting.On), null));
        Assert.Equal("Automatically detected — not encrypted",
            MqttTransportPlan.DescribeProvenance(Auto(encryption: MqttEncryptionSetting.Off), null));

        // …except on a WebSocket port whose scheme is fixed, where "off" cannot make it clear text
        // and saying so would put a false statement about the wire on the page.
        var frontDoor = new MqttEndpointRequest(
            "mq.laget.no", "ck", 443, MqttTransportSetting.WebSocket, MqttEncryptionSetting.Off);
        Assert.Equal("Set manually — encrypted", MqttTransportPlan.DescribeProvenance(frontDoor, null));
    }

    // The user's decision, taken knowing what it costs: cipher first everywhere, plain only after.
    [Fact]
    public void Auto_AsksForEncryptionFirstAndFallsBackToPlain()
    {
        Assert.Equal([true, false], MqttTransportPlan.EncryptionOrder(MqttEncryptionSetting.Auto));
        Assert.Equal([true],        MqttTransportPlan.EncryptionOrder(MqttEncryptionSetting.On));
        Assert.Equal([false],       MqttTransportPlan.EncryptionOrder(MqttEncryptionSetting.Off));

        // Per endpoint, not per sweep: the plain retry for a port beats the encrypted attempt on the
        // next one, or the ordinary internal broker would sit behind the whole encrypted list.
        var tcp = MqttTransportPlan.Sweep(Auto(), null).Where(c => c.Transport == MqttTransport.Tcp).ToList();
        Assert.Equal(
            [new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: true),
             new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false),
             new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: true),
             new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: false)],
            tcp);
    }

    // The whole point of the two-stage probe. A broker that answered and said no to the credentials
    // is the same broker without encryption, so retrying in clear text would put the password on the
    // wire and still fail.
    [Fact]
    public void AnAuthRefusalOverAnEncryptedLink_NeverRetriesInClearText()
    {
        var refused = Failed(8883, MqttTransport.Tcp, MqttProbeOutcome.AuthRejected, encrypted: true);
        Assert.Null(After(Auto(), null, refused));

        // Same for any other refusal the broker itself issued.
        Assert.Null(After(Auto(), null,
            Failed(8883, MqttTransport.Tcp, MqttProbeOutcome.Rejected, encrypted: true)));

        // A transport failure is the opposite case: nothing was reached, so the plain retry is what
        // the fallback is for, and it is the very next candidate on the same port.
        var handshake = Failed(1883, MqttTransport.Tcp, MqttProbeOutcome.Failed, encrypted: true);
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false),
            After(Auto(), null, handshake));
    }

    // An explicit choice pins the scheme exactly as it pins the port and the transport.
    [Fact]
    public void ExplicitEncryption_IsNeverProbedAround()
    {
        var on  = Auto(encryption: MqttEncryptionSetting.On);
        var off = Auto(encryption: MqttEncryptionSetting.Off);

        Assert.All(MqttTransportPlan.Sweep(on, null), c => Assert.True(c.Encrypted));
        Assert.Null(After(on, null, AllFailed(on)));

        // Off is plain everywhere it can be. The exception is a WebSocket port whose scheme the
        // address fixes: excluding it would leave that port unreachable for everyone upgrading from
        // the old switch set to off, and this setting cannot make it clear text anyway.
        foreach (var candidate in MqttTransportPlan.Sweep(off, null))
            Assert.Equal(
                MqttTransportEndpoint.Encrypts(candidate.Transport, candidate.Port, requested: false),
                candidate.Encrypted);
        Assert.Contains(new MqttEndpointCandidate(443, MqttTransport.WebSocket, Encrypted: true),
            MqttTransportPlan.Sweep(off, null));
        Assert.Contains(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false),
            MqttTransportPlan.Sweep(off, null));
    }

    // The cache records what worked, not what was chosen, so an entry can disagree with a pin set
    // after it. The pin wins, or "On" would quietly mean "on, unless clear text worked once".
    [Fact]
    public void ARememberedSchemeThatContradictsThePin_IsNotUsed()
    {
        var plainFound     = Found(1883, MqttTransport.Tcp, encrypted: false);
        var encryptedFound = Found(8883, MqttTransport.Tcp, encrypted: true);

        var on = Auto(encryption: MqttEncryptionSetting.On);
        Assert.DoesNotContain(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false),
            MqttTransportPlan.Sweep(on, plainFound));
        Assert.All(MqttTransportPlan.Sweep(on, plainFound), c => Assert.True(c.Encrypted));

        var off = Auto(encryption: MqttEncryptionSetting.Off);
        Assert.DoesNotContain(new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: true),
            MqttTransportPlan.Sweep(off, encryptedFound));
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: false),
            First(off, encryptedFound));
    }

    // A cached encrypted endpoint has to lead, or Automatic pays the full sweep on every connect.
    [Fact]
    public void ACachedEncryptedEndpoint_LeadsTheSweep()
    {
        var memory = Found(8883, MqttTransport.Tcp, encrypted: true);

        Assert.Equal(new MqttEndpointCandidate(8883, MqttTransport.Tcp, Encrypted: true),
            First(Auto(), memory));
        // …and one attempt is all it costs when it has stopped working.
        var next = After(Auto(), memory,
            Failed(8883, MqttTransport.Tcp, MqttProbeOutcome.Unreachable, encrypted: true));
        Assert.Equal(new MqttEndpointCandidate(1883, MqttTransport.Tcp, Encrypted: true), next);
    }

    // An entry written before the encryption was recorded says nothing about it. Read as plain it
    // would pin Automatic to clear text for good on the strength of a struct default, so under
    // Automatic it is re-probed once and rewritten complete.
    [Fact]
    public void ACachedEntryWithNoEncryptionRecorded_IsNotReadAsPlain()
    {
        var legacy = Found(443, MqttTransport.WebSocket, encrypted: null);

        Assert.Null(MqttTransportPlan.Reusable(Auto(), legacy));
        Assert.Equal(MqttTransportPlan.Sweep(Auto(), null), MqttTransportPlan.Sweep(Auto(), legacy));

        // Under an explicit choice it still applies: nothing is being asked about encryption there,
        // and the setting is what fills the gap rather than a bare false.
        var on = Auto(encryption: MqttEncryptionSetting.On);
        Assert.NotNull(MqttTransportPlan.Reusable(on, legacy));
        Assert.Equal(new MqttEndpointCandidate(443, MqttTransport.WebSocket, Encrypted: true),
            First(on, legacy));
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
