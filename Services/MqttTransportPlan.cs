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

/// <summary>One finished connect attempt. Carries the whole <see cref="MqttProbeResult"/> so the
/// sentence shown afterwards can name what each transport did, not just that it failed.</summary>
internal readonly record struct MqttTransportAttempt(MqttTransport Transport, MqttProbeResult Result)
{
    /// <summary>Outcome-only attempt, for callers that have no detail to carry.</summary>
    public MqttTransportAttempt(MqttTransport transport, MqttProbeOutcome outcome)
        : this(transport, new MqttProbeResult(outcome, "")) { }

    public MqttProbeOutcome Outcome => Result.Outcome;
}

/// <summary>Which transport to try next. Pure: the setting, what last worked and the attempts so
/// far go in, a transport or "nothing left to try" comes out — no client, no socket, no clock.</summary>
/// <remarks>
/// Both the live publisher and the page's connection check walk this, so what the button reports is
/// what the connection will do. TCP leads in Auto because it is the internal path and the cheaper
/// one: at home it answers at once and WebSocket is never attempted, while away from home it costs
/// one connect timeout before the fallback. The remembered transport takes that cost off every
/// later connect — a laptop that moves pays the full probe once per move, not once per reconnect.
/// </remarks>
internal static class MqttTransportPlan
{
    /// <summary>The transport to try after <paramref name="attempts"/>, or null when the plan is
    /// spent.</summary>
    public static MqttTransport? Next(
        MqttTransportSetting setting, MqttTransport? lastSuccessful,
        IReadOnlyList<MqttTransportAttempt> attempts)
    {
        // A broker that answered — accepted, refused the credentials, refused for any other stated
        // reason — is the same broker over the other transport, so trying it again only spends the
        // user's time and turns a precise verdict into a vague one.
        foreach (var attempt in attempts)
            if (Answered(attempt.Outcome)) return null;

        foreach (var transport in Order(setting, lastSuccessful))
            if (!Attempted(attempts, transport)) return transport;

        return null;
    }

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

    private static bool Attempted(IReadOnlyList<MqttTransportAttempt> attempts, MqttTransport transport)
    {
        foreach (var attempt in attempts)
            if (attempt.Transport == transport) return true;
        return false;
    }
}
