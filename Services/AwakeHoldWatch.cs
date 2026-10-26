namespace ChargeKeeper.Services;

/// <summary>What counts as holding the machine awake.</summary>
internal static class AwakeHoldPolicy
{
    /// <summary>The performance categories hold nothing; a display hold counts, because on Modern
    /// Standby the path to sleep begins when the display turns off.</summary>
    internal static bool HoldsTheMachineAwake(PowerRequestEntry entry) =>
        entry.Category is "DISPLAY" or "SYSTEM" or "AWAYMODE" or "EXECUTION";
}

/// <summary>The dashboard's reading of what holds the machine awake, taken every five minutes and
/// only while its section is shown, because each reading starts a console process.</summary>
internal static class AwakeHoldWatch
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

    private static readonly Lock _sync = new();
    private static System.Threading.Timer? _timer;
    private static IReadOnlyList<PowerRequestEntry>? _reading;
    private static DateTimeOffset? _readingAt;

    /// <summary>Raised off the UI thread whenever a fresh reading has landed.</summary>
    public static event Action? Updated;

    /// <summary>The last reading and when it was taken; a null reading means it could not be read.</summary>
    public static (IReadOnlyList<PowerRequestEntry>? Holds, DateTimeOffset? At) Current
    {
        get { lock (_sync) return (_reading, _readingAt); }
    }

    public static void Start()
    {
        SettingsService.Changed  += Follow;
        SettingsService.Reloaded += Follow;
        Follow(TimeSpan.FromSeconds(20));
    }

    // Switched on later, the section is about to be looked at, so the first reading is taken at once.
    private static void Follow() => Follow(TimeSpan.Zero);

    private static void Follow(TimeSpan firstReadingIn)
    {
        bool shown = SettingsService.Read(s => s.ShowPowerActivityInDashboard);
        lock (_sync)
        {
            if (shown == (_timer is not null)) return;
            if (shown)
            {
                _timer = new System.Threading.Timer(_ => Tick(), null, firstReadingIn, Cadence);
                return;
            }
            _timer!.Dispose();
            _timer     = null;
            _reading   = null;
            _readingAt = null;
        }
    }

    private static void Tick()
    {
        try
        {
            var reading = PowerRequestReader.Read();
            lock (_sync)
            {
                // A tick already running when the section was switched off lands nothing.
                if (_timer is null) return;
                _reading   = reading;
                _readingAt = DateTimeOffset.Now;
            }
            Updated?.Invoke();
        }
        catch (Exception ex) { AppLog.Error($"{nameof(AwakeHoldWatch)}.{nameof(Tick)}", ex); }
    }
}
