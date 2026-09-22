namespace ChargeKeeper.Services;

/// <summary>
/// The screen brightness as one mechanism: what the display reports, what sets it, and the level
/// waiting to be put back. The Settings page and an MQTT command both resolve through here, so
/// neither can set the display differently from the other.
/// </summary>
internal static class ScreenBrightnessService
{
    // One display object, shared with the park, so both read the same cached answer rather than
    // each paying its own WMI query.
    private static readonly WindowsScreenBrightness _display = new();

    private static readonly ScreenBrightnessPark _park =
        new(_display, new SettingsScreenBrightnessRecord(),
            (what, cause) => PowerLog.Event(what, cause));

    /// <summary>Raised after the level moves, so every surface reflects it without waiting for its
    /// own refresh.</summary>
    public static event Action? Changed;

    /// <summary>Whether this machine has a display Windows can set the brightness of. False on a
    /// machine whose only screen is an external monitor — the interface does not reach one.</summary>
    public static bool IsSupported => _display.CanSet();

    /// <summary>The level the display reports, or null when none reports one.</summary>
    public static int? Current => _display.Read();

    /// <summary>Whether a level is waiting to be put back.</summary>
    public static bool Holding => _park.Holding;

    /// <summary>Puts back a level a run that ended never restored, and keeps the record alive across
    /// a settings reload. Called once at startup: Windows keeps a brightness across a restart, so a
    /// dimmed screen would otherwise stay dimmed.</summary>
    public static void Start()
    {
        // settings.json roams, so it can arrive from another machine with no saved level while this
        // machine's display still carries the one this run set.
        SettingsService.Reloaded += KeepRecord;

        if (!_park.Holding) return;
        _park.Restore(ActionCause.StartupRestore("a dimmed screen"));
        Changed?.Invoke();
    }

    public static bool Set(int percent, ActionCause cause)
    {
        bool set = _park.Set(percent, cause);
        if (set) Changed?.Invoke();
        return set;
    }

    public static bool Restore(ActionCause cause)
    {
        bool restored = _park.Restore(cause);
        if (restored) Changed?.Invoke();
        return restored;
    }

    /// <summary>Re-saves the displaced level when a reloaded settings document has lost the record.</summary>
    public static void KeepRecord() => _park.KeepRecord();
}
