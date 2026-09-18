using ZeroZero.Mqtt;

namespace ChargeKeeper.Services;

/// <summary>
/// Turns the connection's state changes into two log lines: one when the broker stops being
/// reachable and one when it is reachable again. Searching and connecting are attempts rather than
/// outcomes, so they say nothing; the retries in between say nothing either. Pure — the caller
/// supplies the clock.
/// </summary>
internal sealed class MqttLinkWatch
{
    private bool _down;
    private long _downSince;

    /// <param name="state">The state the connection moved to.</param>
    /// <param name="timestamp">A <see cref="System.Diagnostics.Stopwatch"/> timestamp for now.</param>
    /// <returns>The line to log, or null.</returns>
    public string? OnState(MqttConnectionState state, long timestamp)
    {
        switch (state)
        {
            case MqttConnectionState.Connected:
                if (!_down) return null;
                _down = false;
                return "MQTT: connected to the broker again after " +
                       Span(System.Diagnostics.Stopwatch.GetElapsedTime(_downSince, timestamp)) +
                       " — publishing resumes, and the current state is sent again.";

            case MqttConnectionState.Retrying or MqttConnectionState.Failed:
                if (_down) return null;
                _down = true;
                _downSince = timestamp;
                return "MQTT: not connected to the broker — publishing is paused until the connection returns.";

            case MqttConnectionState.Disabled:
                // Switched off on purpose: nothing is owed a "connected again" line.
                _down = false;
                return null;

            default:
                return null;
        }
    }

    private static string Span(TimeSpan span) =>
        span.TotalSeconds < 60
            ? $"{Math.Max(1, (int)span.TotalSeconds)} s"
            : span.TotalMinutes < 120
                ? $"{(int)span.TotalMinutes} min"
                : $"{(int)span.TotalHours} h {span.Minutes} min";
}
