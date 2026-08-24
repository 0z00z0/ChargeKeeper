using MQTTnet;

namespace ChargeKeeper.Services;

/// <summary>Turns the broker host and port into whatever the chosen transport needs, and wires that
/// onto an MQTTnet options builder. The one place either transport is spelled out, so the live
/// publisher and the page's connection check cannot drift apart.</summary>
/// <remarks>
/// TCP takes the host and port as given. WebSocket takes a URI, so the same pair is folded into one:
/// the port picks the authority and, with the encryption switch, the scheme. A host typed with a
/// <c>ws://</c> or <c>wss://</c> scheme is honoured exactly as written, which covers a broker behind
/// a path the port alone cannot express.
/// </remarks>
internal static class MqttTransportEndpoint
{
    /// <summary>Ports whose WebSocket listener is served over TLS by convention. A bare host on one
    /// of them resolves to <c>wss</c> without the encryption switch having to be found first — a
    /// broker published through a CDN or reverse proxy is only ever reachable that way.</summary>
    private static readonly int[] SecureWebSocketPorts = [443, 8084, 8883];

    /// <summary>Whether the link will actually be encrypted, as opposed to whether encryption was
    /// asked for. The two differ on a WebSocket port whose scheme is fixed by convention: the address
    /// decides there, and no setting can undo it. Pure, and the one place the difference is worked
    /// out, so the plan, the cache and what the page says cannot disagree about it.</summary>
    public static bool Encrypts(MqttTransport transport, int port, bool requested) =>
        requested || (transport == MqttTransport.WebSocket && SecureWebSocketPorts.Contains(ClampPort(port)));

    /// <summary>The URI the WebSocket transport connects to. Pure.</summary>
    public static string WebSocketUri(string host, int port, bool useTls)
    {
        string trimmed = (host ?? "").Trim();
        if (HasWebSocketScheme(trimmed)) return trimmed;

        int    resolved = ClampPort(port);
        bool   secure   = useTls || SecureWebSocketPorts.Contains(resolved);
        string scheme   = secure ? "wss" : "ws";
        // The scheme's own default port is left off, so the common case reads as the plain host it is.
        int implied = secure ? 443 : 80;
        return resolved == implied ? $"{scheme}://{trimmed}" : $"{scheme}://{trimmed}:{resolved}";
    }

    /// <summary>Host and port a plain socket must open before any MQTT is spoken — the stage that
    /// makes "nothing is listening" a verdict from the OS rather than a library exception. Pure.</summary>
    public static (string Host, int Port) Reachability(
        string host, int port, MqttTransport transport, bool useTls)
    {
        if (transport == MqttTransport.Tcp) return ((host ?? "").Trim(), ClampPort(port));

        // Uri knows ws/wss default to 80/443, so an authority without a port resolves on its own.
        return Uri.TryCreate(WebSocketUri(host, port, useTls), UriKind.Absolute, out var uri)
            ? (uri.Host, uri.Port)
            : ((host ?? "").Trim(), ClampPort(port));
    }

    /// <summary>Applies the transport to a client options builder. TLS on the WebSocket side is the
    /// URI scheme's business — <c>wss</c> means the handshake runs over TLS with the platform's own
    /// certificate validation, which is left alone.</summary>
    public static MqttClientOptionsBuilder WithTransport(
        this MqttClientOptionsBuilder builder, MqttTransport transport, string host, int port, bool useTls)
    {
        if (transport == MqttTransport.Tcp)
        {
            // Clamped here as well as in the settings UI: WithTcpServer throws on a port outside
            // 0..65535, and a hand-edited settings.json reaches this unchecked.
            builder = builder.WithTcpServer((host ?? "").Trim(), ClampPort(port));
            return useTls ? builder.WithTlsOptions(o => { }) : builder;
        }

        string uri = WebSocketUri(host, port, useTls);
        builder = builder.WithWebSocketServer(o => o.WithUri(uri));
        return uri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            ? builder.WithTlsOptions(o => { })
            : builder;
    }

    private static bool HasWebSocketScheme(string host) =>
        host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
        host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

    private static int ClampPort(int port) => Math.Clamp(port, 1, 65535);
}
