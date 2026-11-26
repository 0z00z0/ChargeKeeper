using ChargeKeeper.Helpers;
using Windows.Devices.Power;

namespace ChargeKeeper.Services;

/// <summary>Carries out a charge that is to be full by a chosen time: the rate measurement, the
/// wait and the start. What to do is read from <see cref="ChargeByTimePolicy"/>; the lift and every
/// ending are <see cref="TravelOverrideService"/>'s.</summary>
internal static class ChargeByTimeService
{
    /// <summary>A Modern Standby machine runs no timer, so a tick is also how a wake is noticed.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private static readonly Lock Gate = new();
    private static Timer? _timer;
    private static AwakeMark? _lastTick;
    private static ChargeBaseline? _baseline;

    public static void Start() =>
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TickInterval);

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Sets a time by which the battery is to be full. Returns the refusal wording, or null
    /// once the measurement has started.</summary>
    public static Task<string?> Arm(string? input, ActionCause cause) => Task.Run<string?>(() =>
    {
        try { return ArmNow(input, cause); }
        catch (Exception ex)
        {
            AppLog.Error("ChargeByTimeService.Arm", ex);
            return "The battery could not be read. Nothing scheduled.";
        }
    });

    private static string? ArmNow(string? input, ActionCause cause)
    {
        var now = DateTimeOffset.Now;
        if (!KeepAwakeInputParser.TryParse(input, out var request) || request.Kind != KeepAwakeKind.UntilTime ||
            KeepAwakePolicy.ExpiryFor(request, now) is not { } target)
            return ChargeByTimePolicy.Describe(ChargeByTimeRefusal.NotAClockTime);

        var report = Battery.AggregateBattery.GetReport();
        int pct = Percent(report) ?? 0;
        var device = ChargeThresholdService.Read();
        bool running = TravelOverrideService.IsActive || TravelOverrideService.HasSchedule;

        if (ChargeByTimePolicy.Refusal(BatteryStatsFormatter.IsOnAC(report.Status), pct,
                device is { IsLimiting: true }, running) is { } refusal)
        {
            string why = ChargeByTimePolicy.Describe(refusal);
            AppLog.Info($"ChargeByTime: full by {ChargeByTimePolicy.Clock(target)} not set at {pct} %: {why}" + cause.Clause);
            return why;
        }

        lock (Gate) _baseline = null;
        // The record goes to disk in the lift's own write, before the cap comes off.
        TravelOverrideService.Lift(cause, s =>
        {
            s.ChargeByTimeTarget      = target;
            s.ChargeByTimeTesting     = true;
            s.ChargeByTimeStartAt     = null;
            s.ChargeByTimeExpectedPct = null;
            s.ChargeByTimeRateUnknown = false;
        });
        AppLog.Info($"ChargeByTime: full by {ChargeByTimePolicy.Clock(target)} set at {pct} %; the cap is lifted " +
                    $"for {ChargeByTimePolicy.TestLength.TotalMinutes:0} min to measure the charge rate" + cause.Clause);
        return null;
    }

    private static int? Percent(BatteryReport r) =>
        r.FullChargeCapacityInMilliwattHours is > 0 and { } full && r.RemainingCapacityInMilliwattHours is { } rem
            ? Math.Clamp((int)Math.Round(100.0 * rem / full), 0, 100)
            : null;

    private static void Tick()
    {
        try
        {
            var settings = SettingsService.Current;
            if (ChargeByTimeRecord.From(settings) is not { } record) return;

            var report  = Battery.AggregateBattery.GetReport();
            var reading = new ChargeReading(AwakeClock.Mark(), Percent(report) ?? 0, report.Status,
                                            report.RemainingCapacityInMilliwattHours,
                                            report.FullChargeCapacityInMilliwattHours);
            double? taper = record.Testing ? ChargeTaperStore.Installed.Factor() : null;

            ChargeByTimeStep step;
            lock (Gate)
            {
                step = ChargeByTimePolicy.Decide(record, settings.TravelOverrideActive, reading, _lastTick, _baseline, taper);
                _lastTick = reading.At;
                if (step.Action == ChargeByTimeAction.BeginMeasurement)
                    _baseline = new ChargeBaseline(reading.At, reading.RemainingMwh!.Value);
                else if (step.Action != ChargeByTimeAction.Hold)
                    _baseline = null;
            }
            Carry(step, record, reading, taper);
        }
        catch (Exception ex)
        {
            AppLog.Error("ChargeByTimeService.Tick", ex);
        }
    }

    private static void Carry(ChargeByTimeStep step, ChargeByTimeRecord record, ChargeReading reading, double? taper)
    {
        string by = ChargeByTimePolicy.Clock(record.Target);
        switch (step.Action)
        {
            case ChargeByTimeAction.BeginMeasurement:
                AppLog.Info($"ChargeByTime: measuring the charge rate from {reading.RemainingMwh} mWh at {reading.Pct} %" +
                            ActionCause.Timer(step.Why ?? "the measurement starting").Clause);
                break;

            case ChargeByTimeAction.EndTestNoCharger:
                TravelOverrideService.EndLift("the charger being removed during the charge-rate measurement", keepSchedule: false);
                break;

            case ChargeByTimeAction.ChargeNow:
                SettingsService.Update(s =>
                {
                    s.ChargeByTimeTesting     = false;
                    s.ChargeByTimeStartAt     = null;
                    s.ChargeByTimeExpectedPct = step.ExpectedPct;
                    s.ChargeByTimeRateUnknown = step.Estimate is null;
                });
                AppLog.Info((step.Estimate is { } est
                    ? $"ChargeByTime: measured {step.RateMw} mW; {Minutes(est)} to full{TaperClause(taper)}, so charging " +
                      $"continues now for {by}" + (step.ExpectedPct is { } p ? $", about {p} % by then" : "")
                    : $"ChargeByTime: no usable charge measured ({step.Why}), so charging continues now for {by}")
                    + ActionCause.Timer("the charge-rate measurement ending").Clause);
                TravelOverrideService.NotifyChanged();
                break;

            case ChargeByTimeAction.Wait:
                // On disk before the device moves: a crash in between restores at the next start.
                SettingsService.Update(s =>
                {
                    s.ChargeByTimeTesting     = false;
                    s.ChargeByTimeStartAt     = step.StartAt;
                    s.ChargeByTimeExpectedPct = null;
                    s.ChargeByTimeRateUnknown = false;
                });
                AppLog.Info($"ChargeByTime: measured {step.RateMw} mW; {Minutes(step.Estimate!.Value)} to full" +
                            $"{TaperClause(taper)}; charging starts at {ChargeByTimePolicy.Clock(step.StartAt!.Value)} " +
                            $"for full by {by}" + ActionCause.Timer("the charge-rate measurement ending").Clause);
                TravelOverrideService.EndLift(ActionCause.Timer("the charge-rate measurement ending"), keepSchedule: true);
                break;

            case ChargeByTimeAction.RestoreForWait:
                TravelOverrideService.EndLift(
                    ActionCause.Timer($"the cap being lifted with charging due only at {ChargeByTimePolicy.Clock(record.StartAt!.Value)}"),
                    keepSchedule: true);
                break;

            case ChargeByTimeAction.Start:
                TravelOverrideService.Lift(ActionCause.Timer($"the scheduled start for full by {by}"), s =>
                {
                    s.ChargeByTimeTesting = false;
                    s.ChargeByTimeStartAt = null;
                });
                break;

            case ChargeByTimeAction.Clear:
                TravelOverrideService.DropSchedule(ActionCause.Timer(step.Why ?? "the timed charge ending"));
                break;
        }
    }

    private static string Minutes(TimeSpan span) => $"{span.TotalMinutes:0} min";

    private static string TaperClause(double? taper) =>
        taper is { } f ? $" with the learned taper {f:0.000} above 80 %" : " at the measured rate throughout";
}
