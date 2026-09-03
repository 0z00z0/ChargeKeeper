namespace ChargeKeeper.Services;

/// <summary>
/// Decides whether a temperature reading is trustworthy enough to publish, per issue #157's own
/// requirement: a zone can be present, readable and constant, and a constant is the classic false
/// positive — worse than no reading at all, because a safeguard built on it would fire on a lie or
/// never fire at all. Existing is not enough; a reading also has to sit in a plausible range and
/// move over several samples before anything downstream may publish it.
/// </summary>
/// <remarks>
/// Pure state and rules — no timer, no hardware call, no settings — fed one reading at a time by
/// whatever polls the zone (<see cref="ThermalStatusService"/>), the same shape
/// <see cref="LidDischargeWatch"/> uses for the battery-target hold.
/// </remarks>
internal sealed class ThermalReadingGate
{
    /// <summary>Bounds a laptop thermal zone cannot honestly sit outside. A reading past either end
    /// is a broken sensor or a broken read, never a very cold or very hot machine.</summary>
    internal const double MinPlausibleCelsius = -10.0;
    internal const double MaxPlausibleCelsius = 125.0;

    /// <summary>Readings kept to judge movement. Long enough that an ACPI zone's own sampling
    /// plateau — measured identical across eight reads over 8 s on the development machine — reads
    /// as "not enough evidence yet" rather than "stuck"; short enough that a genuinely dead zone is
    /// caught within a few minutes at the caller's sampling cadence.</summary>
    internal const int WindowSize = 5;

    private readonly Queue<double> _window = new(WindowSize);

    /// <summary>
    /// Feeds one reading and returns whether it — and anything derived from it, such as a recommended
    /// maximum — should publish right now.
    /// </summary>
    /// <remarks>A missing reading (null: no source, or the read failed) or an out-of-range one always
    /// returns false and is never added to the window, so a transient failure or a bad sample cannot
    /// be mistaken for "not varying" and cannot poison an otherwise healthy sequence.</remarks>
    public bool Observe(double? celsius)
    {
        if (celsius is not { } value) return false;
        if (value < MinPlausibleCelsius || value > MaxPlausibleCelsius) return false;

        if (_window.Count == WindowSize) _window.Dequeue();
        _window.Enqueue(value);

        // Not enough evidence yet to call it varying — a freshly filling window withholds rather
        // than guessing from a partial run.
        if (_window.Count < WindowSize) return false;

        return _window.Distinct().Count() > 1;
    }
}
