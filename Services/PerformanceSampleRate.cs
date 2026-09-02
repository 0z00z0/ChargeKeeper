using System.Globalization;
using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>
/// How often the self-measurement graph samples processor time. Named steps rather than a free
/// number: every other numeric setting in the window is a fixed dropdown for the same reason, and a
/// finite set is the only shape a remote write cannot push outside the range the UI offers.
/// </summary>
/// <remarks>APPEND new members, never insert: the Settings ComboBox casts between SelectedIndex and
/// this enum by position, so the two orders have to stay in lockstep.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum PerformanceSampleRate
{
    TenHz, FiveHz, TwoHz, OneHz, HalfHz, FifthHz, TenthHz,
}

/// <summary>
/// The bounds of the rate range and the period each step means. The two ends are declared as their
/// own constants rather than read off the enum, so a step added outside the advertised 10 Hz to
/// 0.1 Hz range fails a test instead of silently widening what the feature offers.
/// </summary>
internal static class PerformanceSampleRates
{
    /// <summary>The fast end of the advertised range: a sample every 100 ms.</summary>
    public const int FastestMilliseconds = 100;

    /// <summary>The slow end: a sample every 10 s.</summary>
    public const int SlowestMilliseconds = 10_000;

    /// <summary>The step a rate falls back to when the stored value names no member. Settings enums
    /// round-trip as strings but the converter also accepts integers, so a hand-edited number lands
    /// here undefined instead of failing the whole file's load.</summary>
    public const PerformanceSampleRate Default = PerformanceSampleRate.OneHz;

    /// <summary>Every step, fast to slow, in the order the dropdown lists them.</summary>
    public static IReadOnlyList<PerformanceSampleRate> All { get; } =
        (PerformanceSampleRate[])Enum.GetValues(typeof(PerformanceSampleRate));

    public static PerformanceSampleRate Normalise(PerformanceSampleRate rate) =>
        Enum.IsDefined(rate) ? rate : Default;

    /// <summary>The gap between two processor samples at this rate. Always inside the advertised
    /// range, because an undefined value resolves to <see cref="Default"/> first.</summary>
    public static int PeriodMilliseconds(this PerformanceSampleRate rate) => Normalise(rate) switch
    {
        PerformanceSampleRate.TenHz   => 100,
        PerformanceSampleRate.FiveHz  => 200,
        PerformanceSampleRate.TwoHz   => 500,
        PerformanceSampleRate.OneHz   => 1_000,
        PerformanceSampleRate.HalfHz  => 2_000,
        PerformanceSampleRate.FifthHz => 5_000,
        _                             => 10_000,
    };

    public static TimeSpan Period(this PerformanceSampleRate rate) =>
        TimeSpan.FromMilliseconds(rate.PeriodMilliseconds());

    /// <summary>Samples per second, for the legend and the dropdown label.</summary>
    public static double Hertz(this PerformanceSampleRate rate) => 1000.0 / rate.PeriodMilliseconds();

    /// <summary>What the reader is shown, in the machine's own culture.</summary>
    public static string Label(this PerformanceSampleRate rate) =>
        string.Create(CultureInfo.CurrentCulture, $"{rate.Hertz():0.###} Hz");
}
