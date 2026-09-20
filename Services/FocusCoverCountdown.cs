using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>What the cover's ring draws: how much of the session is left, as a whole number of
/// minutes and as the fraction of the ring still filled, with the colour that fraction reads
/// as.</summary>
/// <param name="FractionLeft">1 at the moment the session is armed, falling to 0 at its end
/// time.</param>
internal readonly record struct FocusCoverReading(int MinutesLeft, double FractionLeft, uint Argb);

/// <summary>
/// The countdown ring as a value. Separate from the window that draws it so the reading is exercised
/// without a display: the cover itself is never shown under test.
/// </summary>
/// <remarks>The ring empties rather than fills — it is whole at the start of a session and gone at
/// its end — and takes its colour from the same draining scale the battery gauge uses, so a session
/// running down reads in the application's existing colour language rather than a second one.</remarks>
internal static class FocusCoverCountdown
{
    /// <summary>The reading for a running session, or null when none is.</summary>
    internal static FocusCoverReading? For(FocusSnapshot session, DateTimeOffset now)
    {
        if (!session.IsRunning || session.EndsAt is not { } ends) return null;

        var started = session.StartedAt ?? ends;
        double total = (ends - started).TotalSeconds;
        double left  = (ends - now).TotalSeconds;

        // A session with no recorded length — a record written before the start time was kept —
        // draws a whole ring rather than dividing by zero.
        double fraction = total > 0 ? Math.Clamp(left / total, 0, 1) : 1;

        return new FocusCoverReading(
            SurfaceReader.MinutesUntil(ends, now) ?? 0,
            fraction,
            GaugePalette.Sample(GaugePalette.Draining, (int)Math.Round(fraction * 100)));
    }
}
