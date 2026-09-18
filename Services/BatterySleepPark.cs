using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>A battery "Sleep after" value and the power scheme it belongs to. Seconds, zero meaning
/// never.</summary>
internal readonly record struct BatterySleepValue(Guid Scheme, uint Seconds);

/// <summary>The Windows battery sleep timeout, read from and written to a power scheme.</summary>
internal interface IBatterySleepSetting
{
    /// <summary>The active scheme's value, or null when it cannot be read.</summary>
    BatterySleepValue? ReadActive();

    /// <summary>Writes the value into its own scheme. False when any step failed.</summary>
    bool Write(BatterySleepValue value);
}

/// <summary>Where the value displaced by a park is kept so a crash cannot lose it.</summary>
internal interface IBatterySleepRecord
{
    BatterySleepValue? Read();

    /// <summary>False when the record did not reach disk.</summary>
    bool Save(BatterySleepValue value);

    void Clear();
}

/// <summary>
/// Sets the battery sleep timeout to never for the length of a lid-close wait and puts the exact
/// previous value back when the wait ends. On battery a Modern Standby machine honours a keep-awake
/// hold only until that timeout plus five minutes, so a timeout that never expires is what lets the
/// wait run its full length.
/// </summary>
/// <remarks>
/// The previous value reaches the record before the setting changes and is never re-captured while
/// held, so a crash between the two, or a restore that failed, cannot turn "never" into the value
/// restored later. A record left behind is restored at the next start.
/// </remarks>
internal sealed class BatterySleepPark(
    IBatterySleepSetting setting,
    IBatterySleepRecord record,
    Func<bool> waitIsRunning,
    Action<string, string> log)
{
    public const uint Never = 0;

    private readonly Lock _gate = new();

    // What this process parked, kept so a reloaded settings document that lacks the record cannot
    // lose the original — the same rule the lid-close action follows.
    private BatterySleepValue? _parked;

    /// <summary>Sets the timeout to never, if a wait is still running when this gets the lock.</summary>
    public void Park(string cause)
    {
        lock (_gate)
        {
            if (!waitIsRunning()) return;

            var active = setting.ReadActive();
            if (record.Read() is { } held)
            {
                if (active is null || active.Value.Scheme == held.Scheme)
                {
                    if (setting.Write(held with { Seconds = Never }))
                    {
                        _parked = held;
                        log($"Windows battery sleep timeout set to never, {Describe(held.Seconds)} kept to put back", cause);
                    }
                    else log("Windows battery sleep timeout could not be set to never", cause);
                    return;
                }

                // The record belongs to a plan no longer active. It goes back first: one record
                // cannot describe two plans, and overwriting it would strand that plan on never.
                if (!setting.Write(held))
                {
                    log($"Windows battery sleep timeout left as it is: the earlier plan's {Describe(held.Seconds)} " +
                        "could not be put back first — retrying at next start", cause);
                    return;
                }
                record.Clear();
                log($"Windows battery sleep timeout back to {Describe(held.Seconds)} in the plan that is no longer active", cause);
            }

            if (active is not { } original)
            {
                log("Windows battery sleep timeout left as it is: it could not be read", cause);
                return;
            }

            if (original.Seconds == Never)
            {
                log("Windows battery sleep timeout is already never, so it is left as it is", cause);
                return;
            }

            if (!record.Save(original))
            {
                log($"Windows battery sleep timeout left at {Describe(original.Seconds)}: " +
                    "its value could not be saved first, so it could not be put back after a crash", cause);
                return;
            }

            if (!setting.Write(original with { Seconds = Never }))
            {
                // The record stays: whatever the scheme holds now, writing the original back is right.
                log("Windows battery sleep timeout could not be set to never", cause);
                return;
            }

            _parked = original;
            log($"Windows battery sleep timeout set to never, was {Describe(original.Seconds)}", cause);
        }
    }

    /// <summary>Puts the recorded value back into its scheme. Does nothing while a wait is running,
    /// which then owns the park, or when nothing is recorded. False only when a write failed, which
    /// leaves the record for the next start.</summary>
    public bool Restore(string cause)
    {
        lock (_gate)
        {
            if (waitIsRunning()) return true;
            if (record.Read() is not { } saved)
            {
                _parked = null;
                return true;
            }

            if (!setting.Write(saved))
            {
                log($"Windows battery sleep timeout could not be put back to {Describe(saved.Seconds)} — " +
                    "retrying at next start", cause);
                return false;
            }

            record.Clear();
            _parked = null;
            log($"Windows battery sleep timeout back to {Describe(saved.Seconds)}", cause);
            return true;
        }
    }

    /// <summary>Re-saves what this process parked when the record has gone missing — settings.json can
    /// be replaced underneath the process while the scheme still holds "never".</summary>
    public void KeepRecord()
    {
        lock (_gate)
        {
            if (_parked is { } parked && record.Read() is null && record.Save(parked))
                AppLog.Info("LidDelay: reloaded settings carried no saved battery sleep timeout while it is " +
                            "set to never — restoring the record from this session.");
        }
    }

    /// <summary>Seconds as the Windows Settings page names them.</summary>
    internal static string Describe(uint seconds) => seconds switch
    {
        Never                   => "never",
        < 60                    => $"{seconds} s",
        _ when seconds % 60 > 0 => $"{seconds} s",
        _                       => $"{seconds / 60} min",
    };
}

/// <summary>The live power scheme.</summary>
internal sealed class WindowsBatterySleepSetting : IBatterySleepSetting
{
    public BatterySleepValue? ReadActive() =>
        NativeMethods.ReadActiveBatterySleepDelay() is { } read
            ? new BatterySleepValue(read.Scheme, read.DcSeconds)
            : null;

    public bool Write(BatterySleepValue value) => NativeMethods.WriteBatterySleepDelay(value.Scheme, value.Seconds);
}

/// <summary>The record in settings.json, beside the saved lid-close action.</summary>
internal sealed class SettingsBatterySleepRecord : IBatterySleepRecord
{
    public BatterySleepValue? Read()
    {
        var (seconds, scheme) = SettingsService.Read(s => (s.LidDelaySavedBatterySleepSeconds, s.LidDelaySavedBatterySleepScheme));
        if (seconds is not { } value) return null;

        // A record without a readable scheme still names the value; the active scheme is the best
        // remaining guess, as for the lid-close action.
        if (Guid.TryParse(scheme, out var stored)) return new BatterySleepValue(stored, value);
        return NativeMethods.ReadActiveBatterySleepDelay() is { } active
            ? new BatterySleepValue(active.Scheme, value)
            : null;
    }

    public bool Save(BatterySleepValue value) => SettingsService.Update(s =>
    {
        s.LidDelaySavedBatterySleepSeconds = value.Seconds;
        s.LidDelaySavedBatterySleepScheme  = value.Scheme.ToString();
    });

    public void Clear() => SettingsService.Update(s =>
    {
        s.LidDelaySavedBatterySleepSeconds = null;
        s.LidDelaySavedBatterySleepScheme  = null;
    });
}
