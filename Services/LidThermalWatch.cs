namespace ChargeKeeper.Services;

/// <summary>What one temperature reading means for an outstanding lid-close hold.</summary>
internal enum LidThermalDecision
{
    /// <summary>No ceiling is armed, so the reading decides nothing.</summary>
    NotWatching,

    /// <summary>Below the ceiling — the hold carries on.</summary>
    Hold,

    /// <summary>At or above the ceiling — the hold ends and the machine sleeps.</summary>
    CeilingReached,

    /// <summary>Nothing to judge: the machine offers no trusted reading, so the ceiling stands
    /// down rather than firing. A missing value disables the safeguard, never triggers it.</summary>
    NoReading,
}

/// <summary>
/// Ends a lid-close hold when the machine gets too hot. State and rules only — no timer, no sensor,
/// no settings and no OS — so the behaviour is testable without heating a laptop.
/// <see cref="LidDelayService"/> owns the hold and feeds this its readings.
/// </summary>
/// <remarks>
/// The hold is the application's own doing: a machine left to Windows sleeps when the lid shuts and
/// never runs hot inside a bag. Ending the hold on a temperature it can read is therefore the
/// application's responsibility rather than the operating system's.
/// <para>Sleep is the action and shutdown never is. Sleep is reversible, costs nothing that was
/// open, and is a safe state to be in inside a bag; a shutdown taken on a temperature reading throws
/// away unsaved work, and a temperature reading is the input least worth trusting that far.</para>
/// <para>A missing or untrusted reading stands the safeguard down. A constant handed to a ceiling
/// test either never fires or fires the moment the watch arms, and the second of those sleeps a
/// working machine repeatedly for no reason — a worse defect than the one being guarded against.
/// <see cref="ThermalReadingGate"/> is what withholds a stuck or implausible source upstream of
/// this.</para>
/// </remarks>
internal sealed class LidThermalWatch
{
    /// <summary>Bounds on the ceiling. Below the floor a machine at rest would trip it; above the
    /// cap the firmware's own protection has already acted, which is the last resort this exists to
    /// stay ahead of.</summary>
    public const int MinCelsius = 40;
    public const int MaxCelsius = 95;

    private readonly System.Threading.Lock _sync = new();

    // Null means nothing is being watched — either never armed, or released by a reading.
    private int? _ceiling;

    /// <summary>The configured ceiling clamped to <see cref="MinCelsius"/>…<see cref="MaxCelsius"/>.</summary>
    public static int Clamp(int celsius) => Math.Clamp(celsius, MinCelsius, MaxCelsius);

    /// <summary>Whether a ceiling is still outstanding.</summary>
    public bool IsWatching { get { lock (_sync) return _ceiling is not null; } }

    /// <summary>The ceiling being watched, or null when none is.</summary>
    public int? Ceiling { get { lock (_sync) return _ceiling; } }

    /// <summary>Starts watching for <paramref name="ceilingCelsius"/>. Re-arming replaces any
    /// outstanding ceiling rather than stacking a second one.</summary>
    public void Arm(int ceilingCelsius)
    {
        lock (_sync) _ceiling = Clamp(ceilingCelsius);
    }

    /// <summary>Abandons the ceiling without deciding anything — the lid reopening, or the hold
    /// ending for another reason.</summary>
    public void Disarm()
    {
        lock (_sync) _ceiling = null;
    }

    /// <summary>
    /// What <paramref name="celsius"/> means for the outstanding ceiling. A reading at or above it
    /// releases the watch, so the same reading cannot end the hold twice.
    /// </summary>
    public LidThermalDecision OnReading(double? celsius)
    {
        lock (_sync)
        {
            if (_ceiling is not { } ceiling) return LidThermalDecision.NotWatching;
            if (celsius is not { } now)      return LidThermalDecision.NoReading;

            if (now < ceiling) return LidThermalDecision.Hold;

            _ceiling = null;
            return LidThermalDecision.CeilingReached;
        }
    }
}
