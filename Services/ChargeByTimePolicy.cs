using System.Globalization;
using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>The persisted part of a "full by a chosen time" charge. Null where none is set.</summary>
/// <param name="Testing">The cap is lifted to measure the charge rate.</param>
/// <param name="StartAt">When the lift is to start; set while waiting for it.</param>
/// <param name="ExpectedPct">The level expected at <paramref name="Target"/> where full cannot be reached.</param>
/// <param name="RateUnknown">The measurement found no usable gain, so the charge started at once.</param>
internal sealed record ChargeByTimeRecord(
    DateTimeOffset Target, bool Testing, DateTimeOffset? StartAt, int? ExpectedPct, bool RateUnknown)
{
    public static ChargeByTimeRecord? From(AppSettings s) =>
        s.ChargeByTimeTarget is { } target
            ? new(target, s.ChargeByTimeTesting, s.ChargeByTimeStartAt, s.ChargeByTimeExpectedPct, s.ChargeByTimeRateUnknown)
            : null;
}

/// <summary>One battery reading with the clock pair it was taken at.</summary>
internal readonly record struct ChargeReading(
    AwakeMark At, int Pct, BatteryStatus Status, int? RemainingMwh, int? FullMwh);

/// <summary>Where the rate measurement began, held in memory: a new run or a sleep starts it again.</summary>
internal readonly record struct ChargeBaseline(AwakeMark At, int RemainingMwh);

internal enum ChargeByTimeAction
{
    Hold,
    /// <summary>Take this reading as the start of the measurement.</summary>
    BeginMeasurement,
    /// <summary>The charger came out during the measurement: put the limit back and drop the time.</summary>
    EndTestNoCharger,
    /// <summary>Keep the cap lifted and charge from now on.</summary>
    ChargeNow,
    /// <summary>Record the start time, then put the limit back until then.</summary>
    Wait,
    /// <summary>The cap is still lifted although a start lies ahead: put the limit back.</summary>
    RestoreForWait,
    /// <summary>The start time has come: lift the cap.</summary>
    Start,
    /// <summary>A record no lift belongs to any more: drop it.</summary>
    Clear,
}

internal readonly record struct ChargeByTimeStep(
    ChargeByTimeAction Action,
    DateTimeOffset? StartAt = null,
    int? ExpectedPct = null,
    int? RateMw = null,
    TimeSpan? Estimate = null,
    string? Why = null);

/// <summary>Why a chosen time schedules nothing.</summary>
internal enum ChargeByTimeRefusal { NotAClockTime, NoCharger, AlreadyFull, NoLimit, AlreadyRunning }

/// <summary>Every decision of a timed charge except when a lift ends, which stays in
/// <see cref="TravelOverridePolicy"/>. Decided afresh from the persisted record on every tick, so a
/// crash at any point resumes from what is on disk.</summary>
internal static class ChargeByTimePolicy
{
    public static readonly TimeSpan TestLength = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Margin     = TimeSpan.FromMinutes(15);

    /// <summary>Above this level the learned taper applies instead of the measured rate.</summary>
    public const double TaperFrom = 0.8;

    /// <summary>Below either, the measurement shows no charge worth predicting from.</summary>
    public const int SmallestGainMwh = 100;
    public const int SmallestRateMw  = 1000;

    /// <summary>Without an awake reading, a tick this late means the machine slept.</summary>
    private static readonly TimeSpan LongestTickGap = TimeSpan.FromMinutes(2);

    public static ChargeByTimeRefusal? Refusal(bool onCharger, int pct, bool limiting, bool running)
    {
        if (running)    return ChargeByTimeRefusal.AlreadyRunning;
        if (!onCharger) return ChargeByTimeRefusal.NoCharger;
        if (pct >= 100) return ChargeByTimeRefusal.AlreadyFull;
        if (!limiting)  return ChargeByTimeRefusal.NoLimit;
        return null;
    }

    public static string Describe(ChargeByTimeRefusal refusal) => refusal switch
    {
        ChargeByTimeRefusal.NotAClockTime  => "Not a clock time. Try 07:30.",
        ChargeByTimeRefusal.NoCharger      => "No charger connected. Nothing scheduled.",
        ChargeByTimeRefusal.AlreadyFull    => "Battery already full. Nothing to do.",
        ChargeByTimeRefusal.NoLimit        => "No charge limit set. The battery charges to 100 % anyway.",
        _                                  => "Already charging to 100 %. Cancel first.",
    };

    /// <summary>Whether the machine slept, or the process was not running, between two marks.</summary>
    public static bool Gap(AwakeMark before, AwakeMark after)
    {
        if (before.Awake is { } a && after.Awake is { } b)
            return new SleepGap(after.Wall - before.Wall, b - a).MachineSlept;
        return after.Wall - before.Wall > LongestTickGap;
    }

    public static ChargeByTimeStep Decide(
        ChargeByTimeRecord? record, bool liftActive, ChargeReading reading,
        AwakeMark? lastTick, ChargeBaseline? baseline, double? taperFactor)
    {
        if (record is null) return new(ChargeByTimeAction.Hold);
        var now = reading.At.Wall;

        if (record.Testing)
        {
            if (!liftActive) return new(ChargeByTimeAction.Clear, Why: "no lift is in force");
            if (reading.Status == BatteryStatus.Discharging) return new(ChargeByTimeAction.EndTestNoCharger);
            if (reading is not { RemainingMwh: { } mwh, FullMwh: > 0 and { } full })
                return new(ChargeByTimeAction.Hold);

            if (baseline is not { } b)
                return new(ChargeByTimeAction.BeginMeasurement, Why: "the measurement starting");
            if (lastTick is not { } last || Gap(last, reading.At))
                return new(ChargeByTimeAction.BeginMeasurement, Why: "the machine having slept during the measurement");

            var elapsed = now - b.At.Wall;
            if (elapsed < TestLength) return new(ChargeByTimeAction.Hold);

            int gain = mwh - b.RemainingMwh;
            int rate = (int)Math.Round(gain / elapsed.TotalHours);
            if (gain < SmallestGainMwh || rate < SmallestRateMw)
                return new(ChargeByTimeAction.ChargeNow, RateMw: rate,
                           Why: $"the measurement storing {gain} mWh in {elapsed.TotalMinutes:0} min, too little to predict from");

            var estimate = Estimate(mwh, full, rate, taperFactor);
            var start    = StartFor(record.Target, estimate);
            if (start > now)
                return new(ChargeByTimeAction.Wait, StartAt: start, RateMw: rate, Estimate: estimate);

            int? expected = now + estimate > record.Target
                ? ExpectedPct(mwh, full, rate, taperFactor, record.Target - now)
                : null;
            return new(ChargeByTimeAction.ChargeNow, ExpectedPct: expected, RateMw: rate, Estimate: estimate);
        }

        if (record.StartAt is { } startAt)
        {
            if (liftActive)
                return startAt > now ? new(ChargeByTimeAction.RestoreForWait) : new(ChargeByTimeAction.Hold);
            return now >= startAt ? new(ChargeByTimeAction.Start) : new(ChargeByTimeAction.Hold);
        }

        // Charging towards the target under the ordinary lift; its ending clears the record.
        return liftActive ? new(ChargeByTimeAction.Hold) : new(ChargeByTimeAction.Clear, Why: "the lift having ended");
    }

    /// <summary>The time the decision's start rests on.</summary>
    public static DateTimeOffset StartFor(DateTimeOffset target, TimeSpan estimate) => target - (estimate + Margin);

    /// <summary>Time to full from <paramref name="mwh"/>. Below the taper level the measured rate
    /// applies; above it the rate times the learned factor, or the measured rate where nothing is
    /// learned. A measurement taken above the taper level already is the taper, so it applies as it is.</summary>
    public static TimeSpan Estimate(int mwh, int full, int rateMw, double? taperFactor)
    {
        double taperAt = TaperFrom * full;
        if (taperFactor is not { } f || mwh >= taperAt)
            return TimeSpan.FromHours(Math.Max(0, full - mwh) / (double)rateMw);
        return TimeSpan.FromHours((taperAt - mwh) / rateMw + (full - taperAt) / (rateMw * f));
    }

    /// <summary>The level reached after <paramref name="left"/> on the same model.</summary>
    public static int ExpectedPct(int mwh, int full, int rateMw, double? taperFactor, TimeSpan left)
    {
        double hours = Math.Max(0, left.TotalHours);
        double taperAt = TaperFrom * full;
        double reached;
        if (taperFactor is not { } f || mwh >= taperAt)
            reached = mwh + rateMw * hours;
        else
        {
            double toTaper = (taperAt - mwh) / rateMw;
            reached = hours <= toTaper ? mwh + rateMw * hours : taperAt + rateMw * f * (hours - toTaper);
        }
        return Math.Clamp((int)Math.Floor(100 * Math.Min(reached, full) / full), 0, 100);
    }

    /// <summary>The one wording for a timed charge, used by the dashboard and the tray menu through
    /// <see cref="TravelOverridePolicy.Describe(bool, ChargeByTimeRecord?)"/>.</summary>
    public static string Describe(ChargeByTimeRecord record)
    {
        string by = Clock(record.Target);
        if (record.Testing) return $"Measuring the charge rate for full by {by}.";
        if (record.StartAt is { } start) return $"Full by {by}. Charging starts at {Clock(start)}.";
        if (record.RateUnknown) return $"Charging now for {by}; no charge rate measured.";
        if (record.ExpectedPct is { } pct) return $"Charging now. About {pct} % by {by}.";
        return $"Charging for full by {by}. The limit returns when full.";
    }

    public static string Clock(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH\\:mm", CultureInfo.InvariantCulture);
}
