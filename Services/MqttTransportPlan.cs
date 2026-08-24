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

/// <summary>Whether the link to the broker is encrypted. Three-valued for the same reason the port
/// and the transport are: which one a broker accepts is a property of the broker, so it is something
/// to find rather than something to know. <see cref="Auto"/> is a probe order, not a third kind of
/// link — encrypted first, then plain — and an explicit choice is never probed around.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum MqttEncryptionSetting { Auto, On, Off }

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

    /// <summary>Whether to ask the endpoint for encryption, in the order to ask. Pure.</summary>
    /// <remarks>
    /// Automatic always asks for encryption first and only then in clear text, per endpoint rather
    /// than per sweep — the plain retry for a port is worth more than the encrypted attempt on the
    /// next one, and holding every plain candidate back would put the ordinary internal broker
    /// behind the whole encrypted list. Nothing reorders this, not even what worked last time: the
    /// remembered endpoint leads the sweep as a whole, and within a pair cipher still comes first.
    /// The fallback is only ever reached when encryption failed to reach the broker at all — an
    /// endpoint that answered and refused the credentials ends the sweep in <see cref="NextEndpoint"/>
    /// through <see cref="Answered"/>, so a wrong password never causes a retry in clear text.
    /// </remarks>
    public static IReadOnlyList<bool> EncryptionOrder(MqttEncryptionSetting setting) => setting switch
    {
        MqttEncryptionSetting.On  => [true],
        MqttEncryptionSetting.Off => [false],
        _ => [true, false],
    };

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

        // What is stored is the encryption that was actually in force, so a WebSocket port whose
        // scheme is fixed collapses the two variants into one candidate rather than doubling the
        // sweep with a duplicate URI.
        void AddEndpoint(int port, MqttTransport transport)
        {
            foreach (bool requested in EncryptionOrder(request.Encryption))
                Add(new(port, transport, MqttTransportEndpoint.Encrypts(transport, port, requested)));
        }

        // An entry from before the encryption state was recorded only survives Reusable under an
        // explicit choice, so the setting is what fills the gap — never a bare false.
        if (remembered is { } m)
            Add(new(m.Port, m.Transport, m.Encrypted ?? MqttTransportEndpoint.Encrypts(
                m.Transport, m.Port, request.Encryption == MqttEncryptionSetting.On)));

        foreach (var transport in Order(request.Transport, remembered?.Transport))
        {
            if (request.Port is { } pinned) AddEndpoint(pinned, transport);
            else foreach (int port in Ports(transport)) AddEndpoint(port, transport);
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
        // An entry written before the encryption state was recorded cannot answer a question that
        // includes it. Absent, not false: a missing field read as plain would pin Automatic to clear
        // text for good on the strength of a struct default. Such an entry costs one sweep, once,
        // and is rewritten complete. Under an explicit choice it still applies — nothing is being
        // asked about encryption there.
        && (request.Encryption != MqttEncryptionSetting.Auto || m.Encrypted is not null)
            ? m : null;

    /// <summary>Whether an explicit action should start a probe. Pure.</summary>
    /// <remarks>
    /// The trigger is the gate, not the cache: a probe costs real seconds and puts the machine on the
    /// network, so it happens because somebody asked for it. <see cref="MqttProbeTrigger"/> is the
    /// closed set of things that count as asking, and showing the page is deliberately not one of
    /// them. What is remembered still leads the sweep once one runs — it decides the order, never
    /// whether there is a sweep at all.
    /// </remarks>
    public static bool ShouldProbe(MqttProbeTrigger trigger, bool publishingEnabled, string host) =>
        // Nothing is probed while publishing is off: in that state the app touches no network at all,
        // and a probe would be the one exception. A blank host has nothing to probe.
        trigger is MqttProbeTrigger.BrokerSettingChanged or MqttProbeTrigger.TestConnection
                or MqttProbeTrigger.Apply
        && publishingEnabled
        && !string.IsNullOrWhiteSpace(host);

    /// <summary>Whether the link in force is encrypted, or null when nothing has settled it yet.
    /// Pure.</summary>
    /// <remarks>
    /// What actually connected outranks what was asked for, because Automatic can end up in clear
    /// text without anyone choosing it. With nothing connected yet the setting has to answer, and
    /// where it cannot the answer is null rather than a guess — except that a pinned WebSocket port
    /// whose scheme is fixed is encrypted whatever the setting says, and saying otherwise would put
    /// a false statement about the wire on the page.
    /// </remarks>
    public static bool? EncryptionInForce(MqttEndpointRequest request, MqttEndpointMemory? memory) =>
        Reusable(request, memory)?.Encrypted
        ?? request.Encryption switch
        {
            MqttEncryptionSetting.On  => true,
            MqttEncryptionSetting.Off => request.Port is { } p
                                      && request.Transport == MqttTransportSetting.WebSocket
                                      && MqttTransportEndpoint.Encrypts(MqttTransport.WebSocket, p, requested: false),
            _ => null,
        };

    /// <summary>How the endpoint in force came to be what it is, and whether it is in clear text.
    /// Pure.</summary>
    /// <remarks>The encryption clause is not decoration. Automatic falls back to plain on its own, so
    /// a link can be downgraded with no user action at all, and nothing else on the page would say
    /// so.</remarks>
    public static string DescribeProvenance(MqttEndpointRequest request, MqttEndpointMemory? memory)
    {
        string source = request.Port is not null
                     && request.Transport != MqttTransportSetting.Auto
                     && request.Encryption != MqttEncryptionSetting.Auto
            ? SetManually
            : AutomaticallyDetected;

        return EncryptionInForce(request, memory) switch
        {
            true  => $"{source} — encrypted",
            false => $"{source} — not encrypted",
            _     => source,
        };
    }

    /// <summary>A pinned port, transport or encryption is honoured exactly, cache entry included —
    /// an explicit choice must not be reached around by something that happened to work once.</summary>
    private static bool Allowed(MqttEndpointRequest request, MqttEndpointCandidate candidate) =>
        (request.Port is not { } pinned || pinned == candidate.Port)
        && request.Transport switch
        {
            MqttTransportSetting.Tcp       => candidate.Transport == MqttTransport.Tcp,
            MqttTransportSetting.WebSocket => candidate.Transport == MqttTransport.WebSocket,
            _ => true,
        }
        && request.Encryption switch
        {
            MqttEncryptionSetting.On  => candidate.Encrypted,
            // Pinned off, except where the port itself fixes the scheme. A WebSocket front door on
            // 443 is encrypted by its address, which this setting cannot undo, and excluding it
            // would leave that port unreachable for everyone whose old switch read "off".
            MqttEncryptionSetting.Off => !candidate.Encrypted
                || MqttTransportEndpoint.Encrypts(candidate.Transport, candidate.Port, requested: false),
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

/// <summary>One endpoint to try against the broker host: a port, a transport, and whether the link
/// is encrypted. The unit the endpoint plan deals in — no part is meaningful alone, because which
/// ports a broker answers on depends on which transport is being spoken, and whether a port is
/// served in cipher is the third thing a broker decides for itself. <see cref="Encrypted"/> is what
/// the link will actually be, not what was asked for: on a WebSocket port whose scheme is fixed by
/// convention the two differ.</summary>
internal readonly record struct MqttEndpointCandidate(int Port, MqttTransport Transport, bool Encrypted = false);

/// <summary>Where the broker answered last, and for which host and user. State, not a setting, and
/// never a password: it records what was found, not what was chosen. A class rather than a struct so
/// the live publisher can swap the whole entry atomically — fields that must never be read as a mix
/// of two different answers.</summary>
/// <remarks>
/// <see cref="Encrypted"/> is nullable because an entry written before it existed does not say. Null
/// is "not recorded", which is not the same as plain, and reading it as plain would leave Automatic
/// permanently satisfied with clear text on the strength of a default. An entry without it is
/// re-probed once under Automatic and rewritten complete; under an explicit choice it still applies,
/// because nothing is being asked about encryption there.
/// </remarks>
internal sealed record MqttEndpointMemory(
    string Host, string Username, int Port, MqttTransport Transport, bool? Encrypted = null);

/// <summary>The staged broker choices the plan reads. A null <see cref="Port"/> means "find it";
/// an explicit one pins every candidate to that port, exactly as an explicit transport pins the
/// transport and an explicit <see cref="Encryption"/> pins the scheme.</summary>
internal readonly record struct MqttEndpointRequest(
    string Host, string Username, int? Port, MqttTransportSetting Transport,
    MqttEncryptionSetting Encryption = MqttEncryptionSetting.Auto);

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

/// <summary>What asked for a probe. A closed set, and every member is an action the user took:
/// opening the page, re-showing a section and a timer are absent on purpose, so a probe can only
/// follow something deliberate. <see cref="MqttTransportPlan.ShouldProbe"/> lists them one by one
/// rather than accepting whatever arrives, so a member added here has to be considered there before
/// it can put the machine on the network.</summary>
internal enum MqttProbeTrigger
{
    /// <summary>One of the Broker settings was edited and has settled.</summary>
    BrokerSettingChanged,
    /// <summary>The Test connection button.</summary>
    TestConnection,
    /// <summary>The Apply button, which is also what makes the staged values live.</summary>
    Apply,
}

/// <summary>Which stage of one candidate the sweep is on. The first two are worth telling apart
/// because they fail for different reasons: nothing listening on the port, versus something
/// listening that does not speak MQTT over this transport. <see cref="Finished"/> is the candidate's
/// own verdict, which is what turns a progress line into an account of the search rather than a
/// spinner with a port number on it.</summary>
internal enum MqttDetectStage { Port, Transport, Finished }

/// <summary>What the sweep is doing right now, so the page can say so. Pure data.
/// <paramref name="Result"/> is carried by <see cref="MqttDetectStage.Finished"/> alone; the two
/// stages before it have nothing to report yet.</summary>
internal readonly record struct MqttDetectProgress(
    MqttDetectStage Stage, int Port, MqttTransport Transport, MqttProbeResult? Result = null);
