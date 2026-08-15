using System.Net.Sockets;
using MQTTnet;

namespace ChargeKeeper.Services;

/// <summary>
/// What a connection test found. The three the user actually needs to tell apart are
/// <see cref="Unreachable"/> (nothing answered), <see cref="AuthRejected"/> (something answered and
/// said no to these credentials) and <see cref="Success"/>; the rest exist so the other two are not
/// quietly widened into catch-alls.
/// </summary>
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

/// <summary>The staged broker values a probe should try. Mirrors the fields the MQTT page stages.</summary>
internal readonly record struct MqttProbeTarget(
    string Host, int Port, string Username, string Password, bool UseTls, string ClientId);

/// <summary>
/// The MQTT page's "Test connection" (issue: connection check). Runs a throwaway CONNECT against the
/// broker and reports which of the three things happened — unreachable, reachable-but-rejected, or in.
///
/// <para>Deliberately its own short-lived client, never the live <see cref="HomeAssistantService"/>
/// connection: a test must not be able to bounce the publisher, restart its backoff, or change what
/// Home Assistant sees, and the live link's own health is already on the page as the last-publish
/// line. The probe therefore publishes nothing, subscribes to nothing, sets no Last Will, and uses a
/// client id DERIVED FROM but different to the publisher's — an MQTT broker kicks off any existing
/// session holding the same client id, so reusing the node id would drop the live connection every
/// time the user pressed the button.</para>
///
/// <para>Two stages, because one failure mode has to be told from the other. A plain TCP connect
/// first: that is what makes "unreachable" a precise verdict (unknown host vs refused vs no answer)
/// straight from the OS, instead of an MQTT-library exception that reads the same for a typo'd host
/// and a wrong password. Only if a socket opens do we send CONNECT, where a rejection can only be
/// about the credentials or the session.</para>
/// </summary>
internal static class MqttConnectionProbe
{
    /// <summary>
    /// Whole-probe budget, both stages together. 10 s matches the app's existing network timeout
    /// (<see cref="UpdateCheckService"/>'s HttpClient, <c>LenovoStandby.StatusTimeout</c>) and is
    /// chosen mainly to beat Windows' own ~21 s SYN-retry give-up: an unreachable IP has to report as
    /// unreachable while the user is still looking at the button.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The probe's client id — never the publisher's, see the class note.</summary>
    public static string ProbeClientId(string nodeId) => $"{nodeId}_probe";

    /// <summary>
    /// Connects, classifies, disconnects. Never throws: every failure comes back as an outcome.
    /// <paramref name="ct"/> is the caller's cancellation (window closing); the timeout above is
    /// applied on top of it.
    /// </summary>
    public static async Task<MqttProbeResult> RunAsync(MqttProbeTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Host))
            return new(MqttProbeOutcome.Failed, "no broker host set");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Timeout);

        var tcp = await ProbeTcpAsync(target.Host, target.Port, budget.Token, ct).ConfigureAwait(false);
        if (tcp is { } failure) return failure;

        return await ProbeConnectAsync(target, budget.Token, ct).ConfigureAwait(false);
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
        MqttProbeTarget target, CancellationToken budget, CancellationToken ct)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            // Same option shape as HomeAssistantService.ApplyAsync — including leaving the protocol
            // version at MQTTnet's default — so a probe that passes says something about the
            // connection the publisher will actually make. Minus the will/retain machinery: this
            // session must leave no trace on the broker.
            var ob = new MqttClientOptionsBuilder()
                .WithTcpServer(target.Host.Trim(), target.Port)
                .WithClientId(target.ClientId)
                .WithCleanSession()
                .WithTimeout(Timeout);
            if (!string.IsNullOrEmpty(target.Username))
                ob = ob.WithCredentials(target.Username, target.Password);
            if (target.UseTls)
                ob = ob.WithTlsOptions(o => { });

            var result = await client.ConnectAsync(ob.Build(), budget).ConfigureAwait(false);
            return ClassifyConnack(result?.ResultCode ?? MqttClientConnectResultCode.UnspecifiedError,
                                   result?.ReasonString);
        }
        catch (OperationCanceledException) { return Cancelled(ct); }
        catch (Exception ex) { return ClassifyConnectException(ex, ct); }
        finally
        {
            // Best-effort, and NOT on the budget token — a cancelled budget must still let the
            // throwaway session close rather than leaving the broker to time it out.
            try { if (client.IsConnected) await client.DisconnectAsync().ConfigureAwait(false); } catch { }
        }
    }

    // ── Pure classification (the testable seam) ──────────────────────────────────

    /// <summary>Maps a CONNACK reason code to an outcome. Pure.</summary>
    internal static MqttProbeResult ClassifyConnack(MqttClientConnectResultCode code, string? reason) => code switch
    {
        MqttClientConnectResultCode.Success => new(MqttProbeOutcome.Success, ""),

        // The two codes that mean "your credentials". A broker with anonymous access disabled answers
        // NotAuthorized to a blank username, which is the same user error as a wrong password.
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
            new(MqttProbeOutcome.Unreachable, "connection refused — nothing is listening on that port"),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
            new(MqttProbeOutcome.Unreachable, "no route to that host"),
        SocketError.TimedOut =>
            new(MqttProbeOutcome.TimedOut, "no answer"),
        _ => new(MqttProbeOutcome.Failed, error.ToString()),
    };

    /// <summary>
    /// Maps a thrown connect failure to an outcome, preferring an inner <see cref="SocketException"/>
    /// (MQTTnet wraps one when the link dies mid-handshake) over the wrapper's own type. Pure apart
    /// from reading <paramref name="ct"/>, which only distinguishes "user closed the window" from
    /// "the budget ran out".
    /// </summary>
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

    // Type + message only, mirroring HomeAssistantService.Sanitize: both are broker/OS-generated, so
    // no staged credential can ride out of here into the UI.
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// The sentence the page shows for a result. Pure, so the wording each outcome maps to is what the
    /// tests pin down.
    /// </summary>
    public static string Describe(MqttProbeResult result) => result.Outcome switch
    {
        MqttProbeOutcome.Success      => "Connected. The broker accepted these settings.",
        MqttProbeOutcome.Unreachable  => $"Could not reach the broker — {result.Detail}.",
        MqttProbeOutcome.TimedOut     => $"The broker did not answer within {(int)Timeout.TotalSeconds} seconds.",
        MqttProbeOutcome.AuthRejected => $"The broker answered but rejected these credentials ({result.Detail}).",
        MqttProbeOutcome.Rejected     => $"The broker refused the connection ({result.Detail}).",
        _                             => $"The connection failed — {result.Detail}.",
    };

    /// <summary>Whether a result should be shown in the error colour rather than as plain status.</summary>
    public static bool IsFailure(MqttProbeResult result) => result.Outcome != MqttProbeOutcome.Success;
}
