using System.Diagnostics;
using System.Management;

namespace ChargeKeeper.Services;

/// <summary>
/// The two hardware reads behind the system-temperature entities. Read from the ACPI thermal zone
/// the firmware exposes through the "Thermal Zone Information" performance-counter set — the only
/// route measured to work without administrator rights — and, separately, the firmware's own
/// passive-cooling trip point over <c>MSAcpi_ThermalZoneTemperature</c> in <c>root\WMI</c>, which
/// needs the administrator rights ChargeKeeper already runs with.
/// </summary>
/// <remarks>
/// <para>The two reads have unrelated failure modes and must not share one. The performance counter
/// answers in a few milliseconds once warmed, but its first use in a fresh process costs roughly
/// 30 s — <see cref="WarmUp"/> pays that once, off the caller's thread, so <see cref="ReadCelsius"/>
/// never does. The WMI class is refused or absent on some machines entirely; that failure is
/// memoised for the process lifetime by <see cref="RecommendedMaximumCelsius"/> so it is attempted
/// once per session rather than on every call, and it never prevents the temperature itself from
/// being read.</para>
/// <para>The single instance under the counter category is discovered by name rather than assumed
/// to be <c>\_TZ.THM0</c> (the one instance measured on the development machine): a different
/// vendor or model may spell it differently, or expose none, which the "no source" branch of the
/// gate this feeds exists to handle.</para>
/// </remarks>
internal static class ThermalZoneReader
{
    private const string CategoryName = "Thermal Zone Information";

    // Deci-kelvin: 0,1 K resolution. The plain "Temperature" counter is whole kelvin only; the
    // development machine's readings were always whole-kelvin-equivalent on both, but a different
    // machine may not be, so the finer counter is the one read.
    private const string CounterName = "High Precision Temperature";

    private static readonly Lock Gate = new();
    private static PerformanceCounter? _counter;
    private static bool _unavailable;

    // WMI trip point: 0 = not attempted, 1 = value in hand, -1 = tried and unavailable this session.
    private static int _tripPointState;
    private static double? _passiveTripPointCelsius;

    /// <summary>
    /// Opens the counter and pays its one-off warm-up cost. Safe to call more than once — a second
    /// call is a no-op — and safe to call from a background thread, which is the only place it
    /// should ever be called from: this blocks for roughly 30 s the first time a process calls it.
    /// </summary>
    public static void WarmUp()
    {
        lock (Gate)
        {
            if (_counter is not null || _unavailable) return;

            try
            {
                var category = new PerformanceCounterCategory(CategoryName);
                string[] instances = category.GetInstanceNames();
                if (instances.Length == 0)
                {
                    _unavailable = true;
                    return;
                }

                var counter = new PerformanceCounter(CategoryName, CounterName, instances[0], readOnly: true);
                counter.NextValue();   // the first read is what actually costs the ~30 s, not construction
                _counter = counter;
            }
            catch (Exception ex)
            {
                // No thermal zone category at all is an expected shape on some machines, not a fault
                // — but still logged, since it is the one piece of evidence that explains why the
                // system-temperature entities never appear on this installation.
                _unavailable = true;
                AppLog.Error("ThermalZoneReader.WarmUp", ex);
            }
        }
    }

    /// <summary>
    /// The zone's current temperature in Celsius, or null when the source has not been warmed up,
    /// does not exist on this machine, or the read itself failed. Never throws.
    /// </summary>
    public static double? ReadCelsius()
    {
        PerformanceCounter? counter;
        lock (Gate) counter = _counter;
        if (counter is null) return null;

        try
        {
            float deciKelvin = counter.NextValue();
            return deciKelvin / 10.0 - 273.15;
        }
        catch (Exception ex)
        {
            AppLog.Error("ThermalZoneReader.ReadCelsius", ex);
            return null;
        }
    }

    /// <summary>
    /// The firmware's own passive-cooling trip point in Celsius — the temperature at which it starts
    /// throttling on its own — or null when the WMI class is refused, absent, or reports nothing
    /// usable. Attempted once per process; every call after the first returns the memoised answer
    /// without touching WMI again. Never throws.
    /// </summary>
    public static double? RecommendedMaximumCelsius()
    {
        lock (Gate)
        {
            if (_tripPointState == 1) return _passiveTripPointCelsius;
            if (_tripPointState == -1) return null;

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT PassiveTripPoint FROM MSAcpi_ThermalZoneTemperature");

                foreach (ManagementBaseObject zone in searcher.Get())
                {
                    using (zone)
                    {
                        if (zone["PassiveTripPoint"] is null) continue;
                        uint deciKelvin = Convert.ToUInt32(zone["PassiveTripPoint"]);
                        if (deciKelvin == 0) continue;   // unpopulated field, not a genuine 0 K trip point

                        _passiveTripPointCelsius = deciKelvin / 10.0 - 273.15;
                        _tripPointState = 1;
                        return _passiveTripPointCelsius;
                    }
                }

                _tripPointState = -1;
                return null;
            }
            catch (Exception ex)
            {
                // Expected on a machine that denies or lacks the class — logged so the absence of a
                // recommended maximum has a reason on record, but never allowed to reach the caller
                // as a fault: the temperature reading must publish regardless.
                _tripPointState = -1;
                AppLog.Error("ThermalZoneReader.RecommendedMaximumCelsius", ex);
                return null;
            }
        }
    }
}
