namespace ChargeKeeper.Services;

/// <summary>The most recent inbound command: what it was and when it landed, as one immutable pair.</summary>
internal sealed record MqttCommandRecord(DateTime WhenUtc, HaCommandKind Kind);

/// <summary>
/// The two facts the MQTT page's status lines report: when we last got something onto the broker, and
/// what the broker last told us to do. Static, like <see cref="NetworkLocationService"/> and
/// <c>KeepAwakeService</c>, because the writer is a background loop with no handle on the UI and the
/// reader is a window with no handle on <see cref="HomeAssistantService"/> — there is exactly one
/// publisher per process, so an instance would only be a wiring detour.
/// <para>
/// Both slots are written from the MQTT threads (publish path, receive callback) and read from the UI
/// thread. The publish slot is a single <c>long</c> swapped with <see cref="Interlocked"/>; the command
/// slot is a whole record swapped by reference, so a reader can never see a timestamp from one command
/// beside the kind of another.
/// </para>
/// </summary>
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

/// <summary>
/// Pure rendering of the MQTT page's status lines — every "never"/"2 min ago" decision lives here so
/// it is unit-tested without a clock or a broker (house style; see <see cref="DashboardIdlePolicy"/>,
/// <c>KeepAwakePolicy</c>). Callers pass <c>DateTime.UtcNow</c> in rather than the formatter reading it.
/// </summary>
internal static class MqttStatusFormatter
{
    /// <summary>
    /// "just now" / "5 min ago" / "3 hours ago" / "2 days ago", or <paramref name="never"/> when there
    /// is nothing to render. A timestamp slightly in the future (clock adjustment between the write and
    /// the read) reads as "just now" rather than a negative age.
    /// </summary>
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

    /// <summary>The "Last publish" line. Says what "nothing yet" means instead of showing a blank.</summary>
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
        _                          => kind.ToString(),
    };
}
