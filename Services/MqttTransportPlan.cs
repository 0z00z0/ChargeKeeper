using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>How the client reaches the broker. Not two dialects of one endpoint: a broker that
/// serves both listens on separate ports, and which one is reachable depends on where the machine
/// sits — plain TCP on the internal network, WebSocket through whatever fronts it from outside.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum MqttTransport { Tcp, WebSocket }

/// <summary>The user's choice. <see cref="Auto"/> is a probe order rather than a third transport,
/// so an explicit choice is never second-guessed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum MqttTransportSetting { Auto, Tcp, WebSocket }

/// <summary>Which transport, and which port, to try next. Pure: the settings, what last worked and
/// the attempts so far go in, a candidate or "nothing left to try" comes out — no client, no socket,
/// no clock.</summary>
/// <remarks>
/// Both the live publisher and the page's connection check walk this, so what the button reports is
/// what the connection will do. TCP leads in Auto because it is the internal path and the cheaper
/// one: at home it answers at once and WebSocket is never attempted, while away from home it costs
/// a connect timeout per candidate before the fallback. The remembered endpoint takes that cost off
/// every later connect — a laptop that moves pays the full sweep once per move, not once per
/// reconnect — and it is remembered against the host and user it was found for, because the same
/// broker legitimately answers on a different port and transport from inside and from outside.
/// </remarks>
internal static class MqttTransportPlan
{
    /// <summary>The order the transports would be tried in from a clean slate.</summary>
    public static IReadOnlyList<MqttTransport> Order(
        MqttTransportSetting setting, MqttTransport? lastSuccessful) => setting switch
    {
        // An explicit choice is the whole plan: no fallback, so a machine pinned to one path fails
        // loudly rather than quietly connecting some other way.
        MqttTransportSetting.Tcp       => [MqttTransport.Tcp],
        MqttTransportSetting.WebSocket => [MqttTransport.WebSocket],

        _ => lastSuccessful is MqttTransport.WebSocket
                ? [MqttTransport.WebSocket, MqttTransport.Tcp]
                : [MqttTransport.Tcp, MqttTransport.WebSocket],
    };

    /// <summary>Whether the broker itself answered, as opposed to nothing being reached.</summary>
    public static bool Answered(MqttProbeOutcome outcome) => outcome
        is MqttProbeOutcome.Success or MqttProbeOutcome.AuthRejected or MqttProbeOutcome.Rejected;

    /// <summary>Provenance shown once the endpoint in force was found rather than chosen.</summary>
    public const string AutomaticallyDetected = "Automatically detected";

    /// <summary>Provenance shown when both halves were pinned by hand, so nothing was probed.</summary>
    public const string SetManually = "Set manually";

    /// <summary>The ports a broker is commonly reached on over one transport, most likely first.</summary>
    /// <remarks>
    /// TCP: 1883 is IANA's <c>mqtt</c> and 8883 its <c>secure-mqtt</c>, which is the whole of what a
    /// plain socket sees in practice. WebSocket is served through an HTTP front door, so its list is
    /// the front door's ports rather than MQTT's: 443 first because that is what a broker published
    /// through a CDN or reverse proxy answers on, then Mosquitto's conventional 9001, EMQX's 8083
    /// and 8084, the common alternate 8080, and finally bare 80. 80 and 8080 sit last deliberately —
    /// a CDN accepts a socket on both whether or not MQTT is behind them, so they are the candidates
    /// most likely to open and then fail the handshake.
    /// </remarks>
    public static IReadOnlyList<int> Ports(MqttTransport transport) => transport == MqttTransport.Tcp
        ? [1883, 8883]
        : [443, 9001, 8083, 8084, 8080, 80];

    /// <summary>Every port the settings dropdown offers, in the order the sweep tries them. One list
    /// for both, so what can be chosen and what is probed cannot drift apart.</summary>
    public static IReadOnlyList<int> OfferedPorts { get; } =
        [.. Ports(MqttTransport.Tcp), .. Ports(MqttTransport.WebSocket)];

    /// <summary>The port/transport pair to try after <paramref name="attempts"/>, or null when the
    /// sweep is spent. Pure: the staged settings, the cache and the attempts so far go in — no
    /// client, no socket, no clock.</summary>
    public static MqttEndpointCandidate? NextEndpoint(
        MqttEndpointRequest request, MqttEndpointMemory? memory,
        IReadOnlyList<MqttEndpointAttempt> attempts)
    {
        // Same rule as the transport plan: a broker that answered at all is the same broker at the
        // next candidate, so carrying on only spends time and blurs a precise verdict.
        foreach (var attempt in attempts)
            if (Answered(attempt.Outcome)) return null;

        foreach (var candidate in Sweep(request, memory))
            if (!Attempted(attempts, candidate)) return candidate;

        return null;
    }

    /// <summary>Every candidate, in order, from a clean slate. Pure.</summary>
    /// <remarks>
    /// The remembered endpoint leads because it is where the broker answered last time, but it is
    /// never the whole sweep: a cached answer stops working the moment the machine moves, and one
    /// that is followed by nothing would turn a move into a permanent failure. So the full list
    /// still trails it, and the cache costs one attempt rather than the connection.
    /// </remarks>
    public static IReadOnlyList<MqttEndpointCandidate> Sweep(
        MqttEndpointRequest request, MqttEndpointMemory? memory)
    {
        var remembered = Reusable(request, memory);
        var sweep = new List<MqttEndpointCandidate>();

        void Add(MqttEndpointCandidate candidate)
        {
            if (Allowed(request, candidate) && !sweep.Contains(candidate)) sweep.Add(candidate);
        }

        if (remembered is { } m) Add(new(m.Port, m.Transport));

        foreach (var transport in Order(request.Transport, remembered?.Transport))
        {
            if (request.Port is { } pinned) Add(new(pinned, transport));
            else foreach (int port in Ports(transport)) Add(new(port, transport));
        }

        return sweep;
    }

    /// <summary>The cache entry that applies to this request, or null when none does. Pure.</summary>
    /// <remarks>
    /// Keyed on host and username together. The host because the same broker legitimately answers
    /// differently from inside and outside a network, so an entry found elsewhere says nothing here;
    /// the username because a broker commonly fronts separate listeners per account, and reusing one
    /// account's endpoint for another would probe the wrong door first.
    /// </remarks>
    public static MqttEndpointMemory? Reusable(MqttEndpointRequest request, MqttEndpointMemory? memory) =>
        memory is { } m
        && m.Port is >= 1 and <= 65535
        && Same(m.Host, request.Host)
        && Same(m.Username, request.Username)
            ? m : null;

    /// <summary>Whether the endpoint has to be probed at all: only when nothing is remembered for
    /// this host and username. Pure.</summary>
    public static bool ShouldDetect(MqttEndpointRequest request, MqttEndpointMemory? memory) =>
        !string.IsNullOrWhiteSpace(request.Host) && Reusable(request, memory) is null;

    /// <summary>How the endpoint in force came to be what it is. Pure.</summary>
    public static string DescribeProvenance(MqttEndpointRequest request) =>
        request.Port is not null && request.Transport != MqttTransportSetting.Auto
            ? SetManually
            : AutomaticallyDetected;

    /// <summary>A pinned port or transport is honoured exactly, cache entry included — an explicit
    /// choice must not be reached around by something that happened to work once.</summary>
    private static bool Allowed(MqttEndpointRequest request, MqttEndpointCandidate candidate) =>
        (request.Port is not { } pinned || pinned == candidate.Port)
        && request.Transport switch
        {
            MqttTransportSetting.Tcp       => candidate.Transport == MqttTransport.Tcp,
            MqttTransportSetting.WebSocket => candidate.Transport == MqttTransport.WebSocket,
            _ => true,
        };

    private static bool Attempted(
        IReadOnlyList<MqttEndpointAttempt> attempts, MqttEndpointCandidate candidate)
    {
        foreach (var attempt in attempts)
            if (attempt.Candidate == candidate) return true;
        return false;
    }

    // Host names are case-insensitive and users paste them with stray spaces; a username is compared
    // the same way so a cache entry cannot be missed over typing.
    private static bool Same(string a, string b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One port/transport pair to try against the broker host. The unit the endpoint plan
/// deals in: neither half is meaningful alone, because which ports a broker answers on depends on
/// which transport is being spoken.</summary>
internal readonly record struct MqttEndpointCandidate(int Port, MqttTransport Transport);

/// <summary>Where the broker answered last, and for which host and user. State, not a setting, and
/// never a password: it records what was found, not what was chosen. A class rather than a struct so
/// the live publisher can swap the whole entry atomically — four fields that must never be read as a
/// mix of two different answers.</summary>
internal sealed record MqttEndpointMemory(
    string Host, string Username, int Port, MqttTransport Transport);

/// <summary>The staged broker choices the plan reads. A null <see cref="Port"/> means "find it";
/// an explicit one pins every candidate to that port, exactly as an explicit transport pins the
/// transport.</summary>
internal readonly record struct MqttEndpointRequest(
    string Host, string Username, int? Port, MqttTransportSetting Transport);

/// <summary>One finished endpoint attempt. Carries the whole result so the sentence afterwards can
/// name what each candidate did.</summary>
internal readonly record struct MqttEndpointAttempt(
    MqttEndpointCandidate Candidate, MqttProbeResult Result)
{
    /// <summary>Outcome-only attempt, for callers with no detail to carry.</summary>
    public MqttEndpointAttempt(MqttEndpointCandidate candidate, MqttProbeOutcome outcome)
        : this(candidate, new MqttProbeResult(outcome, "")) { }

    public MqttProbeOutcome Outcome => Result.Outcome;
}

/// <summary>Which stage of one candidate the sweep is on. The two are worth telling apart because
/// they fail for different reasons: nothing listening on the port, versus something listening that
/// does not speak MQTT over this transport.</summary>
internal enum MqttDetectStage { Port, Transport }

/// <summary>What the sweep is doing right now, so the page can say so. Pure data.</summary>
internal readonly record struct MqttDetectProgress(
    MqttDetectStage Stage, int Port, MqttTransport Transport);
