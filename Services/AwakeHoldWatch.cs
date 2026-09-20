namespace ChargeKeeper.Services;

/// <summary>What counts as holding the machine awake, and for how long before it is worth saying.</summary>
internal static class AwakeHoldPolicy
{
    /// <summary>Hours before an unbroken hold is worth a notification, where the setting names none.
    /// Four: long enough that an evening's video or a long build passes without a word, short enough
    /// that a hold left behind overnight is caught before the battery is.</summary>
    internal const int DefaultWarnAfterHours = 4;

    /// <summary>
    /// Whether a request keeps the machine from sleeping. The two performance categories hold
    /// neither the machine nor the screen, so a program appearing only under one of them is not
    /// what the warning is about. A display hold counts: on a Modern Standby machine the sequence
    /// that ends in sleep begins when the display turns off, so holding the screen on holds the
    /// machine up with it.
    /// </summary>
    internal static bool HoldsTheMachineAwake(PowerRequestEntry entry) =>
        entry.Category is "DISPLAY" or "SYSTEM" or "AWAYMODE" or "EXECUTION";

    /// <summary>One hold's identity across readings. The holder and what it is holding, never the
    /// stated reason, which a holder is free to reword without letting go.</summary>
    internal static string Key(PowerRequestEntry entry) =>
        $"{entry.Category}|{entry.Kind}|{entry.Holder}";

    /// <summary>
    /// Which holds have been in place long enough to warn about. This application's own holds are
    /// never among them: it knows what it is holding and why, and its Keep Awake and lid-close waits
    /// already say so in the log.
    /// </summary>
    internal static IReadOnlyList<PowerRequestEntry> WorthWarningAbout(
        IReadOnlyList<PowerRequestEntry> reading,
        IReadOnlyDictionary<string, DateTimeOffset> firstSeen,
        DateTimeOffset now, TimeSpan threshold) =>
        [.. reading.Where(entry =>
              !entry.IsThisApplication
              && HoldsTheMachineAwake(entry)
              && firstSeen.TryGetValue(Key(entry), out var since)
              && now - since >= threshold)];
}

/// <summary>
/// Keeps the reading of what is holding the machine awake, and warns once a stranger's hold has been
/// in place without a break for longer than the threshold.
/// </summary>
/// <remarks>
/// Reading the list means starting a console process, which is far too expensive to do at the
/// dashboard's own cadence, so it is taken periodically and every surface shows the last one with
/// the time it was taken. That is the honest answer to whether it can be polled live: it cannot.
/// </remarks>
internal static class AwakeHoldWatch
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

    private static readonly Lock _sync = new();
    private static readonly Dictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private static System.Threading.Timer? _timer;
    private static IReadOnlyList<PowerRequestEntry>? _reading;
    private static DateTimeOffset? _readingAt;

    /// <summary>Raised off the UI thread whenever a fresh reading has landed.</summary>
    public static event Action? Updated;

    /// <summary>The last reading and when it was taken. A null reading means the list could not be
    /// read, which every surface states rather than showing as an empty list.</summary>
    public static (IReadOnlyList<PowerRequestEntry>? Holds, DateTimeOffset? At) Current
    {
        get { lock (_sync) return (_reading, _readingAt); }
    }

    /// <summary>Starts the periodic reading. Called once at startup; the timer lives for the process.</summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_timer is not null) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), Cadence);
        }
    }

    private static void Tick()
    {
        try
        {
            Apply(PowerRequestReader.Read(), DateTimeOffset.Now);
            Updated?.Invoke();
        }
        catch (Exception ex) { AppLog.Error($"{nameof(AwakeHoldWatch)}.{nameof(Tick)}", ex); }
    }

    /// <summary>Records a reading and warns about anything that has outstayed the threshold.
    /// Internal rather than private so the timing can be exercised without a five-minute wait.</summary>
    internal static void Apply(IReadOnlyList<PowerRequestEntry>? reading, DateTimeOffset now)
    {
        // Read before the lock: the settings store takes one of its own, and nothing here needs the
        // two held together.
        var threshold = Threshold();

        List<(PowerRequestEntry Entry, TimeSpan Held)> warn = [];
        lock (_sync)
        {
            _reading   = reading;
            _readingAt = now;

            // A failed read is not evidence that a hold let go, so nothing is forgotten on one.
            if (reading is null) return;

            var present = reading.Select(AwakeHoldPolicy.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string gone in _firstSeen.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _firstSeen.Remove(gone);
                _warned.Remove(gone);
            }
            foreach (string key in present) _firstSeen.TryAdd(key, now);

            foreach (var entry in AwakeHoldPolicy.WorthWarningAbout(reading, _firstSeen, now, threshold))
                if (_warned.Add(AwakeHoldPolicy.Key(entry)))
                    warn.Add((entry, now - _firstSeen[AwakeHoldPolicy.Key(entry)]));
        }

        foreach (var (entry, held) in warn) ToastService.NotifyAwakeHold(entry.ShortHolder, held);
    }

    private static TimeSpan Threshold() =>
        TimeSpan.FromHours(SettingsService.Read(s => s.AwakeHoldWarningHours) ??
                           AwakeHoldPolicy.DefaultWarnAfterHours);
}
