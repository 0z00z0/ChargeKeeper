using System.Management;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>The display brightness Windows can set, as a whole percentage.</summary>
internal interface IScreenBrightnessSetting
{
    /// <summary>Whether any display on this machine accepts a brightness from Windows. False on a
    /// machine whose only screen is an external monitor, which the interface does not reach.</summary>
    bool CanSet();

    /// <summary>The level in force, or null when no display reports one.</summary>
    int? Read();

    /// <summary>Sets the level on every display that accepts one. False when nothing was written.</summary>
    bool Write(int percent);
}

/// <summary>Where the level displaced by a dim is kept so a crash cannot lose it.</summary>
internal interface IScreenBrightnessRecord
{
    int? Read();

    /// <summary>False when the record did not reach disk.</summary>
    bool Save(int percent);

    void Clear();
}

/// <summary>
/// Sets the display brightness and remembers the level that was in force before the first change, so
/// one press puts the screen back exactly as it was.
/// </summary>
/// <remarks>
/// The same shape as <see cref="BatterySleepPark"/>: the displaced level reaches the record before
/// the display changes, and is never re-captured while the record stands, so a second dim cannot
/// overwrite the level the first one displaced. A record left behind by a run that died is put back
/// at the next start — Windows keeps a brightness across a restart, so nothing else would.
/// </remarks>
internal sealed class ScreenBrightnessPark(
    IScreenBrightnessSetting setting,
    IScreenBrightnessRecord record,
    Action<string, ActionCause> log)
{
    public const int Minimum = 0;
    public const int Maximum = 100;

    private readonly Lock _gate = new();

    // What this process displaced, kept so a settings document replaced underneath the process
    // cannot lose the original — the same rule the battery sleep timeout follows.
    private int? _parked;

    /// <summary>Whether a level is waiting to be put back.</summary>
    public bool Holding
    {
        get { lock (_gate) return record.Read() is not null; }
    }

    /// <summary>Sets the display to <paramref name="percent"/>, remembering what it was on the first
    /// change. False when nothing was written, which is also what an unsupported display gives.</summary>
    public bool Set(int percent, ActionCause cause)
    {
        int wanted = Math.Clamp(percent, Minimum, Maximum);

        lock (_gate)
        {
            if (!setting.CanSet())
            {
                log("Screen brightness left as it is: no display on this machine accepts one", cause);
                return false;
            }

            if (record.Read() is null)
            {
                if (setting.Read() is not { } original)
                {
                    log("Screen brightness left as it is: the level in force could not be read", cause);
                    return false;
                }

                // Nothing is displaced by setting the level it already carries, so nothing is owed
                // back and no record is written.
                if (original == wanted)
                {
                    log($"Screen brightness is already {wanted} %, so it is left as it is", cause);
                    return true;
                }

                if (!record.Save(original))
                {
                    log($"Screen brightness left at {original} %: the level in force could not be saved " +
                        "first, so it could not be put back after a crash", cause);
                    return false;
                }

                _parked = original;
            }

            if (!setting.Write(wanted))
            {
                // The record stays: whatever the display carries now, writing the original back is right.
                log($"Screen brightness could not be set to {wanted} %", cause);
                return false;
            }

            log($"Screen brightness set to {wanted} %", cause);
            return true;
        }
    }

    /// <summary>Puts the remembered level back. True when nothing is owed. False only when the write
    /// failed, which leaves the record for the next start.</summary>
    public bool Restore(ActionCause cause)
    {
        lock (_gate)
        {
            if (record.Read() is not { } saved)
            {
                _parked = null;
                return true;
            }

            if (!setting.Write(saved))
            {
                log($"Screen brightness could not be put back to {saved} % — retrying at next start", cause);
                return false;
            }

            record.Clear();
            _parked = null;
            log($"Screen brightness back to {saved} %", cause);
            return true;
        }
    }

    /// <summary>Re-saves what this process displaced when the record has gone missing — settings.json
    /// can be replaced underneath the process while the display still carries the new level.</summary>
    public void KeepRecord()
    {
        lock (_gate)
        {
            if (_parked is { } parked && record.Read() is null && record.Save(parked))
                AppLog.Info("Screen: reloaded settings carried no saved brightness while one is still " +
                            "displaced — restoring the record from this session.");
        }
    }
}

/// <summary>
/// The live display, over the brightness classes in <c>root\WMI</c> — the same interface the Windows
/// brightness slider uses.
/// </summary>
/// <remarks>
/// <para>Reading needs no administrator rights (measured); the application runs elevated in any case.
/// The classes cover integrated panels only, so an external monitor on DisplayPort or HDMI is absent
/// from them and a machine with nothing but one reports no support at all.</para>
/// <para>Both answers are cached for a short window. The announcement layer asks for the capability
/// once a second and for the reading once per surface pass, and a WMI query per pass would cost far
/// more than the value moves.</para>
/// </remarks>
internal sealed class WindowsScreenBrightness : IScreenBrightnessSetting
{
    private const string Namespace = @"root\WMI";
    private const string ReadClass = "WmiMonitorBrightness";
    private const string WriteClass = "WmiMonitorBrightnessMethods";
    private const string WriteMethod = "WmiSetBrightness";

    /// <summary>Seconds the display is given to take the new level. Zero asks for no grace at all,
    /// which some panels refuse.</summary>
    private const uint WriteTimeoutSeconds = 1;

    private static readonly TimeSpan SupportMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadingMaxAge = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private bool _canSet;
    private DateTimeOffset _canSetTaken = DateTimeOffset.MinValue;
    private int? _reading;
    private DateTimeOffset _readingTaken = DateTimeOffset.MinValue;

    public bool CanSet()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _canSetTaken < SupportMaxAge) return _canSet;
            _canSet = Count(WriteClass) > 0;
            _canSetTaken = DateTimeOffset.UtcNow;
            return _canSet;
        }
    }

    public int? Read()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _readingTaken < ReadingMaxAge) return _reading;
            _reading = ReadLive();
            _readingTaken = DateTimeOffset.UtcNow;
            return _reading;
        }
    }

    public bool Write(int percent)
    {
        bool written = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM {WriteClass}");
            foreach (var instance in searcher.Get().Cast<ManagementObject>())
                using (instance)
                {
                    // Snapped to a level the panel declares: a panel offering a coarse set refuses a
                    // value that is not one of them, and the caller's slider knows nothing about it.
                    object[] arguments = [WriteTimeoutSeconds, (byte)Nearest(percent)];
                    instance.InvokeMethod(WriteMethod, arguments);
                    written = true;
                }
        }
        catch (Exception ex)
        {
            AppLog.Error("WindowsScreenBrightness.Write", ex);
            return false;
        }

        lock (_gate) _readingTaken = DateTimeOffset.MinValue;
        return written;
    }

    /// <summary>The declared level nearest <paramref name="percent"/>, or the value itself where the
    /// levels cannot be read.</summary>
    private static int Nearest(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM {ReadClass}");
            foreach (var instance in searcher.Get().Cast<ManagementObject>())
                using (instance)
                {
                    if (instance["Level"] is not byte[] { Length: > 0 } levels) continue;
                    return levels.OrderBy(level => Math.Abs(level - percent)).First();
                }
        }
        catch (Exception ex) { AppLog.Error("WindowsScreenBrightness.Nearest", ex); }

        return percent;
    }

    private static int? ReadLive()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM {ReadClass}");
            int? first = null;
            foreach (var instance in searcher.Get().Cast<ManagementObject>())
                using (instance)
                {
                    if (instance["CurrentBrightness"] is not byte level) continue;
                    // The active panel is the one a person is looking at; a first instance that is
                    // not active is still better than nothing.
                    if (instance["Active"] is true) return level;
                    first ??= level;
                }
            return first;
        }
        catch (Exception ex)
        {
            AppLog.Error("WindowsScreenBrightness.Read", ex);
            return null;
        }
    }

    private static int Count(string className)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Namespace, $"SELECT * FROM {className}");
            int count = 0;
            foreach (var instance in searcher.Get().Cast<ManagementObject>())
                using (instance) count++;
            return count;
        }
        catch (Exception ex)
        {
            AppLog.Error($"WindowsScreenBrightness.Count({className})", ex);
            return 0;
        }
    }
}

/// <summary>The record in settings.json, in the Screen section.</summary>
internal sealed class SettingsScreenBrightnessRecord : IScreenBrightnessRecord
{
    public int? Read() => SettingsService.Read(s => s.ScreenSavedBrightness);

    public bool Save(int percent) => SettingsService.Update(s => s.ScreenSavedBrightness = percent);

    public void Clear() => SettingsService.Update(s => s.ScreenSavedBrightness = null);
}
