using System.Text.Json;
using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>A learned taper: the charge rate from 80 % to full as a share of the rate before 80 %.</summary>
internal sealed record TaperSample(DateTimeOffset At, double Factor, int TaperMinutes);

/// <summary>Follows one charge to 100 % and turns it into a <see cref="TaperSample"/>. Pure: readings
/// are handed in. A sleep or a restart during the charge spoils it, because the rate across a gap is
/// not the rate while awake.</summary>
internal sealed class ChargeTaperRecorder
{
    /// <summary>Spans shorter than these say too little about a rate.</summary>
    public const double SmallestSpanShare = 0.05;
    public static readonly TimeSpan SmallestSpan = TimeSpan.FromMinutes(5);

    private (AwakeMark At, int Mwh)? _anchor;
    private (AwakeMark At, int Mwh)? _at80;
    private AwakeMark? _last;
    private bool _spoiled;

    public void Reset()
    {
        _anchor = _at80 = null;
        _last = null;
        _spoiled = false;
    }

    public void Observe(ChargeReading r)
    {
        if (r is not { RemainingMwh: { } mwh, FullMwh: > 0 and { } full }) return;
        if (_last is { } last && ChargeByTimePolicy.Gap(last, r.At)) _spoiled = true;
        _last = r.At;
        if (r.Status != BatteryStatus.Charging) return;

        _anchor ??= (r.At, mwh);
        if (_at80 is null && mwh >= ChargeByTimePolicy.TaperFrom * full) _at80 = (r.At, mwh);
    }

    /// <summary>The sample this charge yields at full, or the reason it yields none.</summary>
    public (TaperSample? Sample, string Why) Finish(ChargeReading full)
    {
        try
        {
            if (_spoiled) return (null, "the machine slept or the application restarted during the charge");
            if (full is not { RemainingMwh: { } mwh, FullMwh: > 0 and { } cap } || full.Pct < 99)
                return (null, "the charge did not reach full");
            if (_anchor is not { } a || _at80 is not { } b) return (null, "the charge began above 80 %");

            var before = b.At.Wall - a.At.Wall;
            var taper  = full.At.Wall - b.At.Wall;
            int beforeGain = b.Mwh - a.Mwh, taperGain = mwh - b.Mwh;
            if (before < SmallestSpan || beforeGain < SmallestSpanShare * cap)
                return (null, "too little of the charge ran below 80 %");
            if (taper < SmallestSpan || taperGain < SmallestSpanShare * cap)
                return (null, "no real energy gain above 80 %");

            double factor = (taperGain / taper.TotalHours) / (beforeGain / before.TotalHours);
            if (factor is <= 0.05 or > 1.5) return (null, $"an implausible taper factor of {factor:0.00}");
            return (new TaperSample(full.At.Wall, Math.Round(factor, 3), (int)Math.Round(taper.TotalMinutes)), "");
        }
        finally { Reset(); }
    }
}

/// <summary>The learned tapers, kept in the data folder as <c>charge-taper.json</c>.</summary>
internal sealed class ChargeTaperStore(string path)
{
    public const int Kept = 5;

    public static ChargeTaperStore Installed { get; } = new(AppPaths.DataFile("charge-taper.json"));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<TaperSample> Read()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<TaperSample>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex)
        {
            AppLog.Error("ChargeTaperStore.Read", ex);
            return [];
        }
    }

    /// <summary>The median of the kept samples, or null where nothing is learned.</summary>
    public double? Factor() => Median(Read());

    public static double? Median(IReadOnlyList<TaperSample> samples)
    {
        if (samples.Count == 0) return null;
        var sorted = samples.Select(s => s.Factor).Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public IReadOnlyList<TaperSample> Add(TaperSample sample)
    {
        var kept = Read().Append(sample).TakeLast(Kept).ToList();
        try { File.WriteAllText(path, JsonSerializer.Serialize(kept, Json)); }
        catch (Exception ex) { AppLog.Error("ChargeTaperStore.Add", ex); }
        return kept;
    }
}
