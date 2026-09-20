namespace ChargeKeeper.Services;

/// <summary>One modern-standby session as Windows recorded it ending.</summary>
/// <param name="EndedAt">When the session ended.</param>
/// <param name="Length">How long the machine was in standby.</param>
/// <param name="BatteryUsedMwh">What the session cost the battery, in milliwatt-hours; null where
/// the entry carried no usable pair of readings.</param>
/// <param name="FullCapacityMwh">The battery's full-charge capacity at the time, so the cost can be
/// read as a share of a full battery rather than as a raw number.</param>
/// <param name="Awake">How much of the session the machine spent running rather than in its
/// low-power state — the closest thing the entry carries to "what kept it up".</param>
/// <param name="EndedBecause">Windows' own wording for why the session ended.</param>
/// <param name="OnMains">Whether the machine was on mains for it.</param>
internal readonly record struct StandbySession(
    DateTimeOffset EndedAt, TimeSpan Length, int? BatteryUsedMwh, int? FullCapacityMwh,
    TimeSpan? Awake, string? EndedBecause, bool OnMains);

/// <summary>
/// The headline figures for each modern-standby session, from the Kernel-Power entry Windows writes
/// as one ends.
/// </summary>
/// <remarks>
/// Windows' own sleep-study report covers the same ground and adds a list of which programs kept the
/// machine up. That report refuses to run without administrator rights, so its shape could not be
/// measured while writing this and a reader for it would have shipped unverified; the entry read
/// here needs no rights at all and carries its figures as named fields. **The per-program list is
/// therefore not carried** — what stands in for it is how much of each session the machine spent
/// running rather than in its low-power state.
/// </remarks>
internal static class StandbySessionReader
{
    private const string Query =
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=507]]";

    /// <summary>The most recent sessions, newest first. Empty where the log holds none or cannot be
    /// read — which is never shown as "the machine has not slept".</summary>
    internal static IReadOnlyList<StandbySession> Recent(int take)
    {
        var entries = SystemEventLog.Newest(Query, take);
        try
        {
            return [.. entries.Select(Read).OfType<StandbySession>()];
        }
        finally
        {
            foreach (var entry in entries) entry.Dispose();
        }
    }

    private static StandbySession? Read(System.Diagnostics.Eventing.Reader.EventRecord entry)
    {
        var data = SystemEventLog.Data(entry);
        if (SystemEventLog.Number(data, "DurationInUs") is not { } durationUs) return null;

        return new StandbySession(
            EndedAt:         entry.TimeCreated ?? DateTimeOffset.Now.LocalDateTime,
            Length:          TimeSpan.FromMicroseconds(durationUs),
            BatteryUsedMwh:  UsedMwh(data),
            FullCapacityMwh: Positive(SystemEventLog.Number(data, "ScreenOffFullEnergyCapacityAtStart")),
            Awake:           SystemEventLog.Number(data, "ActiveResidencyInUs") is { } awakeUs
                                 ? TimeSpan.FromMicroseconds(awakeUs) : null,
            EndedBecause:    SystemEventLog.LastRenderedLine(entry),
            OnMains:         data.TryGetValue("PowerStateAc", out string? ac) &&
                             ac.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the session took out of the battery. The entry splits the session into a screen-off part
    /// and a sleeping part and reports each one's capacity at both ends, so both are counted: a
    /// machine that only ever reaches the first still reports a cost, and one that reaches both is
    /// not read as having used only half of it. A part whose starting capacity is zero was never
    /// entered, and a part that gained capacity was charging, so neither is counted.
    /// </summary>
    private static int? UsedMwh(IReadOnlyDictionary<string, string> data)
    {
        long? screenOff = Drop(data, "ScreenOffEnergyCapacityAtStart", "ScreenOffEnergyCapacityAtEnd");
        long? asleep    = Drop(data, "SleepEnergyCapacityAtStart",     "SleepEnergyCapacityAtEnd");
        if (screenOff is null && asleep is null) return null;
        return (int)((screenOff ?? 0) + (asleep ?? 0));
    }

    private static long? Drop(IReadOnlyDictionary<string, string> data, string startField, string endField)
    {
        if (SystemEventLog.Number(data, startField) is not { } start || start <= 0) return null;
        if (SystemEventLog.Number(data, endField) is not { } end) return null;
        return end < start ? start - end : 0;
    }

    private static int? Positive(long? value) => value is > 0 ? (int)value.Value : null;

    /// <summary>One session as a line a person reads: how long, what it cost, and why it ended. The
    /// cost is given as a share of a full battery as well as in milliwatt-hours, because the raw
    /// number means nothing without the capacity beside it.</summary>
    internal static string Describe(StandbySession session)
    {
        var parts = new List<string> { $"{SleepWatch.Duration(session.Length)} in standby" };

        if (session.BatteryUsedMwh is { } used)
            parts.Add(session.FullCapacityMwh is { } full and > 0
                          ? $"{used} mWh used ({used * 100.0 / full:0.0} % of a full battery)"
                          : $"{used} mWh used");

        if (session.Awake is { } awake && awake > TimeSpan.Zero)
            parts.Add($"{SleepWatch.Duration(awake)} of it running rather than in low power");

        if (session.EndedBecause is { Length: > 0 } reason) parts.Add(reason);

        parts.Add(session.OnMains ? "on mains" : "on battery");
        return string.Join(" · ", parts);
    }
}
