using System.Net.Sockets;
using MQTTnet;

namespace ChargeKeeper.Services;

/// <summary>The three the user needs to tell apart are <see cref="Unreachable"/>,
/// <see cref="AuthRejected"/> and <see cref="Success"/>; the rest keep those from widening into
/// catch-alls.</summary>
internal enum MqttProbeOutcome
{
    Success,       // CONNACK accepted the session
    Unreachable,   // DNS/TCP never got us a broker: unknown host, refused, no route
    TimedOut,      // something is at that address but it never answered within the budget
    AuthRejected,  // CONNACK: bad username/password, or not authorised
    Rejected,      // CONNACK: refused for some other reason (client id, protocol, banned…)
    Failed,        // TLS handshake, protocol error, anything else
}

/// <summary><see cref="MqttProbeOutcome"/> plus a short broker/OS-supplied reason. Never carries credentials.</summary>
internal readonly record struct MqttProbeResult(MqttProbeOutcome Outcome, string Detail);

/// <summary>The staged broker values a probe should try. Mirrors the fields the MQTT page stages,
/// plus the transport setting and what last connected — the probe walks the same plan the live
/// connection does, so the button's verdict is about the connection that will actually be made.</summary>
internal readonly record struct MqttProbeTarget(
    string Host, int Port, string Username, string Password, bool UseTls, string ClientId,
    MqttTransportSetting Transport = MqttTransportSetting.Auto,
    MqttTransport? LastSuccessful = null);

/// <summary>Every transport the probe got as far as trying, in order. The last attempt is the
/// verdict; the earlier ones are why it came to that.</summary>
internal readonly record struct MqttProbeReport(IReadOnlyList<MqttTransportAttempt> Attempts)
{
    /// <summary>Empty only when there was nothing to try, so <see cref="MqttProbeOutcome.Failed"/>
    /// stands in rather than a fake success.</summary>
    public MqttProbeOutcome Outcome =>
        Attempts.Count == 0 ? MqttProbeOutcome.Failed : Attempts[^1].Outcome;

    /// <summary>The transport the verdict came from.</summary>
    public MqttTransport Transport =>
        Attempts.Count == 0 ? MqttTransport.Tcp : Attempts[^1].Transport;

    public bool Succeeded => Outcome == MqttProbeOutcome.Success;
}

/// <summary>The MQTT page's "Test connection": a throwaway CONNECT against the broker.</summary>
/// <remarks>
/// Its own short-lived client, never the live <see cref="HomeAssistantService"/> connection: a broker
/// kicks off any existing session holding the same client id, so reusing the node id would drop the
/// live connection on every button press. The probe publishes nothing and sets no Last Will. A plain
/// TCP connect comes first, because that is what makes "unreachable" a precise verdict straight from
/// the OS — an MQTT-library exception reads the same for a typo'd host and a wrong password. Only
/// once a socket opens is CONNECT sent, where a rejection can only be credentials or session. Both
/// stages run per transport, so Auto reports which one answered rather than only that one did.
/// </remarks>
internal static class MqttConnectionProbe
{
    /// <summary>
    /// Both stages together, per transport. 10 s matches the app's other network timeouts and beats
    /// Windows' own ~21 s SYN-retry give-up, so an unreachable IP reports while the user is still
    /// looking. Auto can spend it twice, which is the price of finding out from a filtered port.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The probe's client id — never the publisher's, see the class note.</summary>
    public static string ProbeClientId(string nodeId) => $"{nodeId}_probe";

    /// <summary>Never throws: every failure comes back as an outcome. <paramref name="ct"/> is the
    /// caller's cancellation (window closing), with the timeout above applied on top.</summary>
    public static async Task<MqttProbeReport> RunAsync(MqttProbeTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Host)) return new([]);

        var attempts = new List<MqttTransportAttempt>();
        while (MqttTransportPlan.Next(target.Transport, target.LastSuccessful, attempts) is { } transport)
        {
            // A fresh budget per transport: one dead endpoint must not eat the other's chance to answer.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Timeout);
            var result = await RunOneAsync(target, transport, budget.Token, ct).ConfigureAwait(false);
            attempts.Add(new(transport, result));

            if (ct.IsCancellationRequested) break;   // window closed; the remaining transports are moot
        }
        return new(attempts);
    }

    /// <summary>Both stages against one transport.</summary>
    private static async Task<MqttProbeResult> RunOneAsync(
        MqttProbeTarget target, MqttTransport transport, CancellationToken budget, CancellationToken ct)
    {
        var (host, port) = MqttTransportEndpoint.Reachability(target.Host, target.Port, transport);
        var tcp = await ProbeTcpAsync(host, port, budget, ct).ConfigureAwait(false);
        return tcp ?? await ProbeConnectAsync(target, transport, budget, ct).ConfigureAwait(false);
    }

    /// <summary>Stage 1 — can a socket be opened at all. Returns null when it can (i.e. carry on).</summary>
    private static async Task<MqttProbeResult?> ProbeTcpAsync(
        string host, int port, CancellationToken budget, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, budget).ConfigureAwait(false);
            return null;
        }
        catch (SocketException ex) { return ClassifySocketError(ex.SocketErrorCode); }
        catch (OperationCanceledException) { return Cancelled(ct); }
        catch (Exception ex) { return new(MqttProbeOutcome.Failed, Describe(ex)); }
    }

    /// <summary>Stage 2 — does the broker accept a CONNECT with these credentials.</summary>
    private static async Task<MqttProbeResult> ProbeConnectAsync(
        MqttProbeTarget target, MqttTransport transport, CancellationToken budget, CancellationToken ct)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            // Same option shape as HomeAssistantService.ApplyAsync, transport and protocol version
            // included, so a passing probe says something about the connection the publisher will
            // make. Minus the will/retain machinery: this session must leave no trace on the broker.
            var ob = new MqttClientOptionsBuilder()
                .WithTransport(transport, target.Host, target.Port, target.UseTls)
                .WithClientId(target.ClientId)
                .WithCleanSession()
                .WithTimeout(Timeout);
            if (!string.IsNullOrEmpty(target.Username))
                ob = ob.WithCredentials(target.Username, target.Password);

            var result = await client.ConnectAsync(ob.Build(), budget).ConfigureAwait(false);
            return ClassifyConnack(result?.ResultCode ?? MqttClientConnectResultCode.UnspecifiedError,
                                   result?.ReasonString);
        }
        catch (OperationCanceledException) { return Cancelled(ct); }
        catch (Exception ex) { return ClassifyConnectException(ex, ct); }
        finally
        {
            // Not on the budget token: a cancelled budget must still let the throwaway session close
            // rather than leaving the broker to time it out.
            try { if (client.IsConnected) await client.DisconnectAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Maps a CONNACK reason code to an outcome. Pure.</summary>
    internal static MqttProbeResult ClassifyConnack(MqttClientConnectResultCode code, string? reason) => code switch
    {
        MqttClientConnectResultCode.Success => new(MqttProbeOutcome.Success, ""),

        // A broker with anonymous access disabled answers NotAuthorized to a blank username, which is
        // the same user error as a wrong password.
        MqttClientConnectResultCode.BadUserNameOrPassword or
        MqttClientConnectResultCode.NotAuthorized =>
            new(MqttProbeOutcome.AuthRejected, Reason(code, reason)),

        // Everything else the broker can say no with — a real answer, so never "unreachable".
        _ => new(MqttProbeOutcome.Rejected, Reason(code, reason)),
    };

    /// <summary>Maps an OS socket error to an outcome. Pure.</summary>
    internal static MqttProbeResult ClassifySocketError(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            new(MqttProbeOutcome.Unreachable, "host name could not be resolved"),
        SocketError.ConnectionRefused =>
            new(MqttProbeOutcome.Unreachable, "nothing is listening on that port"),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
            new(MqttProbeOutcome.Unreachable, "no route to that host"),
        SocketError.TimedOut =>
            new(MqttProbeOutcome.TimedOut, "no answer"),
        _ => new(MqttProbeOutcome.Failed, error.ToString()),
    };

    /// <summary>Prefers an inner <see cref="SocketException"/> over the wrapper's own type — MQTTnet
    /// wraps one when the link dies mid-handshake.</summary>
    internal static MqttProbeResult ClassifyConnectException(Exception ex, CancellationToken ct)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException se) return ClassifySocketError(se.SocketErrorCode);
            if (e is OperationCanceledException) return Cancelled(ct);
        }
        return new(MqttProbeOutcome.Failed, Describe(ex));
    }

    // A cancelled budget with the caller's token still live means the timeout fired, not the user.
    private static MqttProbeResult Cancelled(CancellationToken ct) => ct.IsCancellationRequested
        ? new(MqttProbeOutcome.Failed, "cancelled")
        : new(MqttProbeOutcome.TimedOut, "no answer");

    private static string Reason(MqttClientConnectResultCode code, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? code.ToString() : $"{code}: {reason.Trim()}";

    // Type and message only, mirroring HomeAssistantService.Sanitise: both are broker/OS-generated, so
    // no staged credential can ride out of here into the UI.
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>How a transport is named to the user.</summary>
    public static string Name(MqttTransport transport) =>
        transport == MqttTransport.Tcp ? "TCP" : "WebSocket";

    /// <summary>The sentence the page shows for one transport's result. Pure, so the tests pin the
    /// wording. Every branch names the transport: under Auto that is the answer the user came for.</summary>
    public static string Describe(MqttProbeResult result, MqttTransport transport) => result.Outcome switch
    {
        MqttProbeOutcome.Success      => $"Connected over {Name(transport)}. The broker accepted these settings.",
        MqttProbeOutcome.Unreachable  => $"Could not reach the broker over {Name(transport)} — {Detail(result)}.",
        MqttProbeOutcome.TimedOut     => $"The broker did not answer over {Name(transport)} within {(int)Timeout.TotalSeconds} seconds.",
        MqttProbeOutcome.AuthRejected => $"The broker answered over {Name(transport)} but rejected these credentials ({Detail(result)}).",
        MqttProbeOutcome.Rejected     => $"The broker refused the connection over {Name(transport)} ({Detail(result)}).",
        _                             => $"The connection over {Name(transport)} failed — {Detail(result)}.",
    };

    /// <summary>The sentence for a whole run. Pure.</summary>
    /// <remarks>
    /// The shapes differ because the user's next move does. Reaching the broker makes that attempt
    /// the story and any earlier one mere context ("connected over WebSocket, TCP was refused" sends
    /// nobody to check their password). Reaching nothing at all is the opposite: no single attempt
    /// explains it, so every transport tried is listed.
    /// </remarks>
    public static string Describe(MqttProbeReport report)
    {
        if (report.Attempts.Count == 0) return "No broker host set.";

        var last = report.Attempts[^1];
        string verdict = Describe(last.Result, last.Transport);
        if (report.Attempts.Count == 1) return verdict;

        if (MqttTransportPlan.Answered(last.Outcome))
            return $"{verdict} {Fragment(report.Attempts[^2])}.";

        return "Neither transport reached the broker. " +
               string.Join("; ", report.Attempts.Select(Fragment)) + ".";
    }

    /// <summary>One attempt as a clause, for the sentences that name more than one transport.</summary>
    private static string Fragment(MqttTransportAttempt attempt) => attempt.Outcome switch
    {
        MqttProbeOutcome.Success      => $"{Name(attempt.Transport)} connected",
        MqttProbeOutcome.Unreachable  => $"{Name(attempt.Transport)} could not be reached ({Detail(attempt.Result)})",
        MqttProbeOutcome.TimedOut     => $"{Name(attempt.Transport)} did not answer",
        MqttProbeOutcome.AuthRejected => $"{Name(attempt.Transport)} rejected these credentials",
        MqttProbeOutcome.Rejected     => $"{Name(attempt.Transport)} was refused ({Detail(attempt.Result)})",
        _                             => $"{Name(attempt.Transport)} failed ({Detail(attempt.Result)})",
    };

    // An exception message usually ends in its own full stop; the sentences above supply one.
    private static string Detail(MqttProbeResult result) => result.Detail.TrimEnd('.', ' ');

    /// <summary>Whether a result should be shown in the error colour rather than as plain status.</summary>
    public static bool IsFailure(MqttProbeReport report) => !report.Succeeded;
}
