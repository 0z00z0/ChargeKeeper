using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>The most recent inbound command: what it was and when it landed.</summary>
internal sealed record MqttCommandRecord(DateTime WhenUtc, HaCommandKind Kind);

/// <summary>When something last reached the broker, and what the broker last asked for. Written from
/// the MQTT threads and read from the UI thread, so each slot is swapped atomically — the command as a
/// whole record, so a reader never pairs one command's timestamp with another's kind.</summary>
internal static class MqttActivity
{
    private static long _lastPublishTicks;   // UTC ticks; 0 = nothing published yet
    private static MqttCommandRecord? _lastCommand;

    /// <summary>Records a publish that the broker actually acknowledged. Call only on success.</summary>
    public static void RecordPublish() => Interlocked.Exchange(ref _lastPublishTicks, DateTime.UtcNow.Ticks);

    /// <summary>Records a recognised, accepted inbound command — not a rejected or retained payload.</summary>
    public static void RecordCommand(HaCommandKind kind) =>
        Volatile.Write(ref _lastCommand, new MqttCommandRecord(DateTime.UtcNow, kind));

    public static DateTime? LastPublishUtc =>
        Interlocked.Read(ref _lastPublishTicks) is var ticks && ticks != 0
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;

    public static MqttCommandRecord? LastCommand => Volatile.Read(ref _lastCommand);
}

/// <summary>Pure rendering of the MQTT page's status lines. Callers pass <c>DateTime.UtcNow</c> in
/// rather than the formatter reading it, so the wording is testable without a clock.</summary>
internal static class MqttStatusFormatter
{
    /// <summary>"just now" / "5 min ago" / "3 hours ago" / "2 days ago", or <paramref name="never"/>
    /// when there is nothing to render. A future timestamp reads as "just now", not a negative age.</summary>
    public static string Relative(DateTime? whenUtc, DateTime nowUtc, string never)
    {
        if (whenUtc is not { } when) return never;

        var age = nowUtc - when;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1))   return $"{(int)age.TotalMinutes} min ago";   // "min" is the abbreviation, so it doesn't pluralise
        if (age < TimeSpan.FromDays(1))    return Plural((int)age.TotalHours, "hour");
        return Plural((int)age.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";

    /// <summary>The "Broker in use" line: the saved host with whatever port and transport are in
    /// force, whether pinned by hand or found by probing. Pure.</summary>
    /// <remarks>A cache entry is only ever read for its own host and user name, so a machine that
    /// has moved reports "not connected yet" rather than the address it used somewhere else.</remarks>
    public static string DescribeBroker(MqttEndpointRequest request, MqttEndpointMemory? memory)
    {
        string host = (request.Host ?? "").Trim();
        if (host.Length == 0) return "Not set";

        var found = MqttTransportPlan.Reusable(request, memory);
        int? port = request.Port ?? found?.Port;
        MqttTransport? transport = request.Transport switch
        {
            MqttTransportSetting.Tcp       => MqttTransport.Tcp,
            MqttTransportSetting.WebSocket => MqttTransport.WebSocket,
            _ => found?.Transport,
        };

        // Half an answer is worse than none: "host:— over —" reads as a broken value rather than as
        // a question that has not been answered yet.
        if (port is not { } p || transport is not { } t) return $"{host} — not connected yet";
        return $"{host}:{p.ToString(CultureInfo.InvariantCulture)} over {MqttConnectionProbe.Name(t)}";
    }

    /// <summary>The "Last publish" line.</summary>
    public static string DescribeLastPublish(DateTime? lastUtc, DateTime nowUtc) =>
        Relative(lastUtc, nowUtc, never: "Nothing published yet");

    /// <summary>The "Last command received" line — which command, and how long ago.</summary>
    public static string DescribeLastCommand(MqttCommandRecord? last, DateTime nowUtc) =>
        last is { } c ? $"{CommandLabel(c.Kind)} — {Relative(c.WhenUtc, nowUtc, never: "")}"
                      : "Nothing received yet";

    /// <summary>The entity name a user would recognise from Home Assistant, not the enum spelling.</summary>
    public static string CommandLabel(HaCommandKind kind) => kind switch
    {
        HaCommandKind.SmartCharge  => "Smart Charge",
        HaCommandKind.ChargeStart  => "Charge start",
        HaCommandKind.ChargeStop   => "Charge stop",
        HaCommandKind.ChargeToFull => "Charge to full once",
        HaCommandKind.SetPreset    => "Charge preset",
        HaCommandKind.KeepAwake            => "Keep awake",
        HaCommandKind.KeepAwakeFor         => "Keep awake for",
        HaCommandKind.KeepAwakeDisplayOn   => "Keep the display on",
        HaCommandKind.LidDelay             => "Lid-close delay",
        HaCommandKind.LidDelayMinutes      => "Lid-close delay length",
        HaCommandKind.LidDelayLock         => "Lock on lid close",
        HaCommandKind.SmartStandby         => "Smart Standby",
        HaCommandKind.LowBatteryWarning    => "Low battery warning",
        HaCommandKind.LowBatteryLevel      => "Low battery level",
        HaCommandKind.HighBatteryWarning   => "High battery warning",
        HaCommandKind.HighBatteryLevel     => "High battery level",
        HaCommandKind.DrainWarning         => "Standby drain warning",
        HaCommandKind.DrainRate            => "Standby drain rate",
        HaCommandKind.NetworkProfiles      => "Network profiles",
        HaCommandKind.UnknownNetworkPreset => "Preset on an unknown network",
        HaCommandKind.StartupDelay         => "Startup delay",
        HaCommandKind.IconMode             => "Tray icon style",
        HaCommandKind.DowntimeGap          => "Downtime gap threshold",
        _                          => kind.ToString(),
    };
}
