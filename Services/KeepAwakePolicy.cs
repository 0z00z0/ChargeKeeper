using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>What ends a keep-awake session (issue #90).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum KeepAwakeKind
{
    /// <summary>Runs for <see cref="KeepAwakeRequest.Duration"/> from the moment it starts.</summary>
    Duration,
    /// <summary>Runs until the next occurrence of <see cref="KeepAwakeRequest.Until"/> on the clock.</summary>
    UntilTime,
    /// <summary>Runs until the detected network location changes — leaving is the off switch.</summary>
    UntilNetworkChange,
    /// <summary>Runs until turned off by hand.</summary>
    Indefinite,
}

/// <summary>
/// One keep-awake request (issue #90). Also the persisted shape of a
/// <see cref="AppSettings.KeepAwakePresets"/> entry — the unused field is null for every kind except
/// its own.
/// <para><see cref="Name"/> is an optional label for a SAVED preset ("End of day"): a span already
/// describes itself, so it defaults to null and every ad-hoc request leaves it unset. Last and
/// defaulted so an older settings.json — and every existing positional construction — is unchanged.</para>
/// </summary>
internal sealed record KeepAwakeRequest(KeepAwakeKind Kind, TimeSpan? Duration, TimeOnly? Until, string? Name = null);

/// <summary>
/// A running keep-awake session: what was asked for, when it started, and the instant it ends
/// (null for the two kinds that have no clock expiry). Runtime-only — deliberately never persisted,
/// see <see cref="AppSettings.KeepAwakePresets"/>.
/// </summary>
internal sealed record KeepAwakeSession(KeepAwakeRequest Request, DateTimeOffset StartedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// PURE clock/expiry rules behind keep-awake (issue #90), kept free of the P/Invoke and the timer so
/// the fiddly parts — the until-time rollover, the remaining-time wording — are unit-testable without
/// touching the OS (house style; see <see cref="HaDiscovery"/>/<c>PresetEditValidator</c>).
/// <see cref="KeepAwakeService"/> owns the actual hold.
/// </summary>
internal static class KeepAwakePolicy
{
    /// <summary>
    /// The instant a request ends, or null when it has no clock expiry
    /// (<see cref="KeepAwakeKind.UntilNetworkChange"/>/<see cref="KeepAwakeKind.Indefinite"/>, or a
    /// malformed request whose own field is unset — treated as "no expiry" rather than an instant one).
    /// </summary>
    public static DateTimeOffset? ExpiryFor(KeepAwakeRequest request, DateTimeOffset now) => request.Kind switch
    {
        KeepAwakeKind.Duration  => request.Duration is { } d && d > TimeSpan.Zero ? now + d : null,
        KeepAwakeKind.UntilTime => request.Until is { } t ? NextOccurrenceOf(t, now) : null,
        _                       => null,
    };

    /// <summary>
    /// Today at <paramref name="time"/>, or TOMORROW when that instant has already passed — "until
    /// 17:00" asked at 17:05 means the next 17:00, which is the only reading that isn't an immediate
    /// expiry. Resolved in <paramref name="now"/>'s own UTC offset: a wall-clock time that straddles a
    /// DST switch therefore lands an hour off, which is the deliberate trade for keeping this a pure
    /// function instead of a time-zone-rule lookup.
    /// </summary>
    private static DateTimeOffset NextOccurrenceOf(TimeOnly time, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Year, now.Month, now.Day,
                                       time.Hour, time.Minute, time.Second, now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>Whether a session with <paramref name="expiry"/> is due to end at <paramref name="now"/>.</summary>
    public static bool ShouldExpire(DateTimeOffset now, DateTimeOffset? expiry) => expiry is { } e && now >= e;

    /// <summary>
    /// What a bare "on" means when the user picked no span — the tray toggle and the dashboard's
    /// switch. The FIRST preset in <see cref="AppSettings.KeepAwakePresets"/>, because that order is
    /// the priority order; "until turned off" when the list is empty, so a hand-edited settings.json
    /// still does something sensible rather than refusing the toggle that was just flipped.
    /// </summary>
    public static KeepAwakeRequest DefaultRequest(IEnumerable<KeepAwakeRequest> presets) =>
        presets.FirstOrDefault() ?? new KeepAwakeRequest(KeepAwakeKind.Indefinite, null, null);

    /// <summary>
    /// How a running session reads as one line — "2 h 12 m left", "until 17:00", "until network
    /// changes". ONE formatter so the dashboard, Settings and the tray tooltip cannot drift apart, the
    /// same reasoning as <see cref="ThresholdPreset.FormatLabel"/>.
    /// </summary>
    public static string DescribeRemaining(DateTimeOffset now, KeepAwakeSession session)
    {
        switch (session.Request.Kind)
        {
            case KeepAwakeKind.UntilNetworkChange:
                return "until network changes";
            case KeepAwakeKind.UntilTime when session.Request.Until is { } t:
                return $"until {t.ToString("HH\\:mm", CultureInfo.InvariantCulture)}";
        }

        if (session.ExpiresAt is not { } expiry) return "until turned off";

        var left = expiry - now;
        if (left <= TimeSpan.Zero) return "expiring";
        // Round the partial minute UP: a session started as "90 m" must read "1 h 30 m left" on the
        // very first render, not "1 h 29 m left" because a few milliseconds have gone.
        int total = (int)Math.Ceiling(left.TotalMinutes);
        return total switch
        {
            < 60           => $"{total} m left",
            _ when total % 60 == 0 => $"{total / 60} h left",
            _              => $"{total / 60} h {total % 60} m left",
        };
    }

    /// <summary>
    /// A request as a chip-sized label — "30m", "1h", "1h30", "17:00", "Net". Separate from
    /// <see cref="DescribeRemaining"/> because that describes a RUNNING session's remaining time,
    /// while a chip names the span itself and has ~50 DIP to do it in; same ONE-formatter reasoning
    /// though, so the dashboard chips and any Settings list of the same presets cannot drift.
    /// </summary>
    public static string ShortLabel(KeepAwakeRequest request)
    {
        switch (request.Kind)
        {
            case KeepAwakeKind.UntilNetworkChange:
                return "Net";
            case KeepAwakeKind.UntilTime when request.Until is { } t:
                return t.ToString("HH\\:mm", CultureInfo.InvariantCulture);
        }

        // Indefinite — and any malformed request, which ExpiryFor also reads as "no expiry".
        if (request.Kind != KeepAwakeKind.Duration || request.Duration is not { } d || d <= TimeSpan.Zero)
            return "∞";

        int total = (int)Math.Ceiling(d.TotalMinutes);
        return total switch
        {
            < 60                   => $"{total}m",
            _ when total % 60 == 0 => $"{total / 60}h",
            _                      => $"{total / 60}h{total % 60}",
        };
    }
}

/// <summary>
/// Fast-entry parser for a keep-awake duration or end time (issue #90) — the whole entry story, since
/// a Windows-style time picker was rejected as too heavy for "keep this thing awake till five".
/// <list type="bullet">
/// <item>Explicit units are a DURATION: <c>3h</c>, <c>90m</c>, <c>90min</c>, <c>1h30</c>, <c>1h30m</c>.</item>
/// <item>A colon or 3–4 digits is a clock TIME: <c>17:00</c>, <c>7:30</c>, <c>1700</c>, <c>930</c>.</item>
/// <item>A bare 1–2 digit number reads as a clock time when it CAN be an hour (<c>17</c> → 17:00) and
///   as minutes when it can't (<c>45</c> → 45 m). Explicit units always beat this guess, so <c>17m</c>
///   is unambiguously 17 minutes.</item>
/// </list>
/// Anything else — garbage, an out-of-range time, a zero/negative duration — returns false.
/// </summary>
internal static class KeepAwakeInputParser
{
    public static bool TryParse(string? input, [NotNullWhen(true)] out KeepAwakeRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim().ToLowerInvariant().Replace(" ", "");

        if (s.Contains(':')) return TryClockTime(s, out request);

        // An "h" anywhere makes it a duration; whatever follows is the minutes part ("1h30", "1h30m").
        int h = s.IndexOf('h');
        if (h >= 0)
        {
            if (!TryNonNegative(s[..h], out int hours)) return false;
            string tail = StripMinuteSuffix(s[(h + 1)..]);
            int minutes = 0;
            if (tail.Length > 0 && !TryNonNegative(tail, out minutes)) return false;
            return Duration(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes), out request);
        }

        string stripped = StripMinuteSuffix(s);
        if (stripped.Length != s.Length)
            return TryNonNegative(stripped, out int m) && Duration(TimeSpan.FromMinutes(m), out request);

        if (!s.All(char.IsAsciiDigit)) return false;
        return s.Length switch
        {
            1 or 2 => BareNumber(int.Parse(s, CultureInfo.InvariantCulture), out request),
            3      => TryClockTime($"{s[..1]}:{s[1..]}", out request),
            4      => TryClockTime($"{s[..2]}:{s[2..]}", out request),
            _      => false,
        };
    }

    // A number that fits the 24-hour clock is far more likely to be "till five" than a duration; one
    // that doesn't can only be minutes.
    private static bool BareNumber(int n, out KeepAwakeRequest? request) =>
        n <= 23
            ? Time(new TimeOnly(n, 0), out request)
            : Duration(TimeSpan.FromMinutes(n), out request);

    private static bool TryClockTime(string s, out KeepAwakeRequest? request)
    {
        request = null;
        var parts = s.Split(':');
        if (parts.Length != 2) return false;
        if (!TryNonNegative(parts[0], out int h) || !TryNonNegative(parts[1], out int m)) return false;
        if (h > 23 || m > 59) return false;
        return Time(new TimeOnly(h, m), out request);
    }

    private static string StripMinuteSuffix(string s) =>
        s.EndsWith("min", StringComparison.Ordinal) ? s[..^3]
        : s.EndsWith('m')                           ? s[..^1]
        : s;

    private static bool TryNonNegative(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool Duration(TimeSpan span, out KeepAwakeRequest? request)
    {
        request = span > TimeSpan.Zero ? new(KeepAwakeKind.Duration, span, null) : null;
        return request is not null;
    }

    private static bool Time(TimeOnly time, out KeepAwakeRequest? request)
    {
        request = new(KeepAwakeKind.UntilTime, null, time);
        return true;
    }
}
