namespace ChargeKeeper.Services;

/// <summary>What Windows recorded about the last time the machine woke.</summary>
/// <param name="At">When the entry was written.</param>
/// <param name="Source">Windows' own wording for the cause, taken whole — "Wake Source: Unknown",
/// "Reason: Input Keyboard".</param>
/// <param name="Detail">The device or the program that owned a wake timer, where the entry names
/// one; null otherwise.</param>
internal readonly record struct WakeRecord(DateTimeOffset At, string Source, string? Detail);

/// <summary>
/// Why the machine woke, from the System event log.
/// </summary>
/// <remarks>
/// Two providers record it and neither covers both cases: Power-Troubleshooter writes one entry per
/// resume from true sleep or hibernation, and Kernel-Power writes one per modern-standby session
/// ending. The newer of the two is the answer, so a machine that does both is read correctly.
/// <para>Reading needs no administrator rights — measured from a session that had none, against both
/// providers. The console command for the same thing does run unelevated but reported nothing on the
/// machine measured, which is why the log is read instead.</para>
/// </remarks>
internal static class WakeSourceReader
{
    // One query over both providers: the answer is whichever entry is newest, and asking the log
    // twice would mean merging two ordered reads for no gain.
    private const string Query =
        "*[System[(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1) " +
        "or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=507)]]";

    /// <summary>The last recorded wake, or null when the log holds none or cannot be read. Null is
    /// never shown as "nothing woke it" — there is simply no reading.</summary>
    internal static WakeRecord? LastWake()
    {
        var entries = SystemEventLog.Newest(Query, 1);
        try
        {
            if (entries.Count == 0) return null;
            var entry = entries[0];
            if (SystemEventLog.LastRenderedLine(entry) is not { Length: > 0 } source) return null;

            var data = SystemEventLog.Data(entry);
            string? detail = Named(data, "WakeSourceText") ?? Named(data, "WakeTimerOwner");

            return new WakeRecord(entry.TimeCreated ?? DateTimeOffset.Now.LocalDateTime, source, detail);
        }
        finally
        {
            foreach (var entry in entries) entry.Dispose();
        }
    }

    private static string? Named(IReadOnlyDictionary<string, string> data, string field) =>
        data.TryGetValue(field, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>The line written to the log at each resume, and shown on the dashboard. The device
    /// or timer owner is added only where Windows named one, so a bare source never gains an empty
    /// bracket.</summary>
    internal static string Describe(WakeRecord wake) =>
        wake.Detail is { Length: > 0 } detail ? $"{wake.Source} ({detail})" : wake.Source;
}
