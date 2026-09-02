namespace ChargeKeeper.Services;

/// <summary>Which condition ended a lid-close wait.</summary>
internal enum LidWaitEnd
{
    /// <summary>Neither the delay nor a battery target was outstanding, so there was nothing left
    /// to wait for.</summary>
    NothingToWaitFor,

    /// <summary>The configured delay ran out.</summary>
    DelayElapsed,

    /// <summary>The battery came down to its target level.</summary>
    BatteryTarget,
}

/// <summary>
/// What one lid-close wait says while it runs and at the moment it ends: which reports are due, and
/// the wording of each. State and sentences only — no timer, no log, no settings and no OS — so
/// what a reader ends up seeing is testable without closing a lid.
/// <see cref="LidDelayService"/> owns the wait itself and feeds this its readings.
/// </summary>
/// <remarks>
/// A wait that says nothing while it runs cannot be told apart from one that never started, and the
/// two have opposite answers to "did the application put this machine to sleep". Silence settles
/// that question only once a running wait would have been heard.
///
/// The two reports are independent: a wait may have a delay, a battery target, or both, and each
/// condition reports on its own terms. Neither can flood the file, because both are bounded by the
/// wait itself — the delay is capped at <see cref="LidDelayPolicy.MaxMinutes"/>, forty-eight
/// reports at the longest setting, and a battery falling the whole way from
/// <see cref="LidDischargeWatch.MaxPercent"/> to <see cref="LidDischargeWatch.MinPercent"/> is
/// eighteen more.
/// </remarks>
internal sealed class LidWaitTrail
{
    /// <summary>Minutes between reports while the delay is running.</summary>
    public const int MinutesBetweenTimeReports = 5;

    /// <summary>Percentage points the battery has to fall before the next report.</summary>
    public const int PercentBetweenBatteryReports = 5;

    /// <inheritdoc cref="MinutesBetweenTimeReports"/>
    public static TimeSpan TimeReportInterval => TimeSpan.FromMinutes(MinutesBetweenTimeReports);

    /// <summary>The line for a one-off delay standing itself down, written before the suspend the
    /// stand-down is owed to.</summary>
    public const string SwitchedOffBeforeSleeping =
        "The lid-close delay was set to run once, so it switched itself off before putting the " +
        "machine to sleep.";

    private readonly System.Threading.Lock _sync = new();

    private bool _timeSet;
    private int  _delayMinutes;
    private int? _targetPercent;      // null means no battery target is being waited for
    private int  _lastReportedLevel;
    private LidWaitEnd? _endedBy;

    /// <summary>
    /// Starts the trail for one lid close. The values are taken once, here, rather than read back
    /// from the settings at the end: a setting changed mid-wait would otherwise make the closing
    /// line describe a wait that never ran.
    /// </summary>
    public void Start(bool timeSet, int delayMinutes, int? targetPercent, int? levelNow)
    {
        lock (_sync)
        {
            _timeSet           = timeSet;
            _delayMinutes      = delayMinutes;
            _targetPercent     = targetPercent;
            _lastReportedLevel = levelNow ?? 0;
            _endedBy           = null;
        }
    }

    /// <summary>
    /// Records a condition arriving. The first to arrive is the one that ended the wait, so a
    /// second arriving in the same moment does not take the credit from it.
    /// </summary>
    public void Arrived(LidWaitEnd end)
    {
        lock (_sync) _endedBy ??= end;
    }

    /// <summary>Forgets the wait. A report asked for after this says nothing.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _timeSet       = false;
            _targetPercent = null;
            _endedBy       = null;
        }
    }

    /// <summary>
    /// Progress through the delay, or null when there is no delay to report on, or the tick falls
    /// outside the wait: the last tick lands with the end of the wait, and "thirty minutes into the
    /// thirty minute delay" beside the line announcing the end says nothing that line does not.
    /// </summary>
    public string? OnElapsed(TimeSpan elapsed)
    {
        lock (_sync)
        {
            if (!_timeSet) return null;

            int minutes = (int)Math.Round(elapsed.TotalMinutes);
            if (minutes <= 0 || minutes >= _delayMinutes) return null;

            return $"Still waiting with the lid closed: {Plural(minutes, "minute")} into the " +
                   $"{_delayMinutes} minute delay.";
        }
    }

    /// <summary>
    /// Progress towards the battery target, or null when no target is being waited for or the level
    /// has not fallen far enough since the last report. Measured against the level last reported
    /// rather than the level at the start, so a long drain reports at a steady spacing.
    /// </summary>
    public string? OnBatteryReading(int percent)
    {
        lock (_sync)
        {
            if (_targetPercent is not { } target) return null;
            if (percent > _lastReportedLevel - PercentBetweenBatteryReports) return null;

            _lastReportedLevel = percent;
            return $"Still waiting with the lid closed: the battery is at {percent} %, on its way " +
                   $"down to the {target} % target.";
        }
    }

    /// <summary>
    /// The line for the moment the wait ends, naming the condition that ended it and the value it
    /// ended on. Written before the machine is put to sleep: a line written after the suspend call
    /// may never reach disk, and its absence would then prove nothing.
    /// </summary>
    public string End(int? levelNow)
    {
        lock (_sync)
        {
            return (_endedBy ?? LidWaitEnd.NothingToWaitFor) switch
            {
                LidWaitEnd.DelayElapsed  =>
                    $"The lid-close wait ended because the {_delayMinutes} minute delay ran out.",
                LidWaitEnd.BatteryTarget =>
                    $"The lid-close wait ended because the battery reached its target of " +
                    $"{_targetPercent ?? 0} %, standing at {levelNow ?? 0} %.",
                _ =>
                    "The lid-close wait ended because there was nothing left to wait for: neither " +
                    "the delay nor a battery target was set.",
            };
        }
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
