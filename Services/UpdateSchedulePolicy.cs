using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>How often the background check asks whether a newer version has been released. The
/// document stores the member name, so renaming a member resets every installation's
/// choice.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum UpdateCheckCadence
{
    /// <summary>Once an hour, for as long as the application runs.</summary>
    EveryHour,

    /// <summary>Once a day. What every installation does before the choice exists.</summary>
    EveryDay,

    /// <summary>The run shortly after start, and nothing after it.</summary>
    AtStartupOnly,
}

/// <summary>
/// Whether this tick of the background timer is one that asks GitHub. The timer runs at a fixed
/// <see cref="TickInterval"/> whatever the cadence is, because the shared scheduler takes its
/// interval once at construction and cannot be told to stop; the cadence is expressed here instead,
/// so a choice made on the Settings page takes effect at the next tick without rebuilding anything.
/// </summary>
/// <remarks>Pure, so every cadence is testable without waiting out a day.</remarks>
internal static class UpdateSchedulePolicy
{
    /// <summary>How often the timer ticks. Shorter than the shortest cadence because the same tick
    /// re-tests whether an automatic install may now go ahead — see
    /// <see cref="AutoInstallPolicy"/>.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    /// <summary>Delayed so the first check does not slow the cold-start path.</summary>
    internal static readonly TimeSpan FirstTickDelay = TimeSpan.FromSeconds(30);

    /// <summary>Tolerance on the comparison below. A tick landing a moment before the cadence has
    /// strictly elapsed would otherwise defer the check by a whole tick.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    /// <summary>The gap each cadence asks for, or null for the one that never repeats.</summary>
    internal static TimeSpan? Every(UpdateCheckCadence cadence) => cadence switch
    {
        UpdateCheckCadence.EveryHour => TimeSpan.FromHours(1),
        UpdateCheckCadence.EveryDay  => TimeSpan.FromHours(24),
        _                            => null,
    };

    /// <summary>Whether this tick asks GitHub.</summary>
    /// <param name="lastCheck">When the last check ran, or null where none has. The first check
    /// runs under every cadence, including the one that never repeats.</param>
    internal static bool IsDue(UpdateCheckCadence cadence, DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (lastCheck is not { } last) return true;
        if (Every(cadence) is not { } gap) return false;

        return now - last >= gap - Tolerance;
    }
}
