using MQTTnet;

namespace ChargeKeeper.Services;

/// <summary>Turns the broker host and port into whatever the chosen transport needs, and wires that
/// onto an MQTTnet options builder. The one place either transport is spelled out, so the live
/// publisher and the page's connection check cannot drift apart.</summary>
/// <remarks>
/// TCP takes the host and port as given. WebSocket takes a URI, and there is no second port box for
/// it: MQTT over WebSocket is served through an HTTPS front door, so the default is
/// <c>wss://&lt;host&gt;</c> on 443. A host typed with a <c>ws://</c> or <c>wss://</c> scheme is
/// honoured as written, which covers a broker on some other port or path without adding a field
/// whose only correct value is nearly always the default.
/// </remarks>
internal static class MqttTransportEndpoint
{
    /// <summary>The URI the WebSocket transport connects to. Pure.</summary>
    public static string WebSocketUri(string host)
    {
        string trimmed = (host ?? "").Trim();
        return HasWebSocketScheme(trimmed) ? trimmed : $"wss://{trimmed}";
    }

    /// <summary>Host and port a plain socket must open before any MQTT is spoken — the stage that
    /// makes "nothing is listening" a verdict from the OS rather than a library exception. Pure.</summary>
    public static (string Host, int Port) Reachability(string host, int port, MqttTransport transport)
    {
        if (transport == MqttTransport.Tcp) return ((host ?? "").Trim(), ClampPort(port));

        // Uri knows ws/wss default to 80/443, so an authority without a port resolves on its own.
        return Uri.TryCreate(WebSocketUri(host), UriKind.Absolute, out var uri)
            ? (uri.Host, uri.Port)
            : ((host ?? "").Trim(), 443);
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

        string uri = WebSocketUri(host);
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
