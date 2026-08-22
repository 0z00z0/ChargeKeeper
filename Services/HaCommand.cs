using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>The kind of command received over MQTT. The charge-control kinds reach the vendor RPC;
/// the rest write a setting the Settings window also writes.</summary>
internal enum HaCommandKind
{
    SmartCharge,   // switch → BoolValue
    ChargeStart,   // number → IntValue (%)
    ChargeStop,    // number → IntValue (%)
    ChargeToFull,  // button (no value)
    SetPreset,     // select → StringValue (preset name)

    KeepAwake,           // switch → BoolValue
    KeepAwakeFor,        // text   → Request (parsed span or clock time)
    KeepAwakeDisplayOn,  // switch → BoolValue
    LidDelay,            // switch → BoolValue
    LidDelayMinutes,     // number → IntValue (min)
    LidDelayLock,        // switch → BoolValue
    SmartStandby,        // switch → BoolValue
    LowBatteryWarning,   // switch → BoolValue
    LowBatteryLevel,     // number → IntValue (%)
    HighBatteryWarning,  // switch → BoolValue
    HighBatteryLevel,    // number → IntValue (%)
    DrainWarning,        // switch → BoolValue
    DrainRate,           // number → IntValue (%/h)
    NetworkProfiles,     // switch → BoolValue
    UnknownNetworkPreset,// select → StringValue (preset name or the "do nothing" sentinel)
    StartupDelay,        // number → IntValue (s)
    IconMode,            // select → StringValue (TrayIconMode)
    DowntimeGap,         // number → IntValue (min)
}

/// <summary>A parsed, validated inbound command. Carries only typed values, so the dispatch side
/// never re-parses strings.</summary>
internal readonly record struct HaCommand(
    HaCommandKind Kind, bool BoolValue, int IntValue, string StringValue, KeepAwakeRequest? Request = null)
{
    /// <summary>The button entity's press payload — matches the discovery <c>payload_press</c>.</summary>
    public const string ButtonPress = "PRESS";

    /// <summary>
    /// Parses a command from the entity's object-id and the raw payload. Returns false for an unknown
    /// object-id or a payload that fails validation, so the caller simply refuses it. Every bound
    /// enforced here is the one the Settings window enforces, taken from the same constant — a remote
    /// write can reach nothing the UI cannot. Membership in a list that can change while the app runs
    /// (preset names) is checked at dispatch instead, against the live list.
    /// </summary>
    public static bool TryParse(string objectId, string payload, out HaCommand cmd)
    {
        cmd = default;
        string p = (payload ?? "").Trim();

        switch (objectId)
        {
            case HaEntityCatalog.SmartCharge:
                return Flag(HaCommandKind.SmartCharge, p, out cmd);

            case HaEntityCatalog.ChargeStart:
                return Bounded(HaCommandKind.ChargeStart, p,
                               PresetEditValidator.MinThreshold, PresetEditValidator.MaxThreshold, out cmd);

            case HaEntityCatalog.ChargeStop:
                return Bounded(HaCommandKind.ChargeStop, p,
                               PresetEditValidator.MinThreshold, PresetEditValidator.MaxThreshold, out cmd);

            case HaEntityCatalog.ChargeToFull:
                // Exact match only: a kick to 100 % must not fire on a stray or retained payload.
                if (!string.Equals(p, ButtonPress, StringComparison.Ordinal)) return false;
                cmd = new HaCommand(HaCommandKind.ChargeToFull, false, 0, "");
                return true;

            case HaEntityCatalog.Preset:
                return Text(HaCommandKind.SetPreset, p, out cmd);

            case HaEntityCatalog.KeepAwake:
                return Flag(HaCommandKind.KeepAwake, p, out cmd);

            case HaEntityCatalog.KeepAwakeFor:
                // The same parser the Settings box uses, so "1h30", "17:00" and "45" mean here exactly
                // what they mean there, and everything else is refused.
                if (!KeepAwakeInputParser.TryParse(p, out var request)) return false;
                cmd = new HaCommand(HaCommandKind.KeepAwakeFor, false, 0, p, request);
                return true;

            case HaEntityCatalog.KeepAwakeDisplayOn:
                return Flag(HaCommandKind.KeepAwakeDisplayOn, p, out cmd);

            case HaEntityCatalog.LidDelay:
                return Flag(HaCommandKind.LidDelay, p, out cmd);

            case HaEntityCatalog.LidDelayMinutes:
                return Bounded(HaCommandKind.LidDelayMinutes, p,
                               LidDelayPolicy.MinMinutes, LidDelayPolicy.MaxMinutes, out cmd);

            case HaEntityCatalog.LidDelayLock:
                return Flag(HaCommandKind.LidDelayLock, p, out cmd);

            case HaEntityCatalog.SmartStandby:
                return Flag(HaCommandKind.SmartStandby, p, out cmd);

            case HaEntityCatalog.LowBatteryWarning:
                return Flag(HaCommandKind.LowBatteryWarning, p, out cmd);

            case HaEntityCatalog.LowBatteryLevel:
                return Bounded(HaCommandKind.LowBatteryLevel, p,
                               SettingRanges.LowBatteryMin, SettingRanges.LowBatteryMax, out cmd);

            case HaEntityCatalog.HighBatteryWarning:
                return Flag(HaCommandKind.HighBatteryWarning, p, out cmd);

            case HaEntityCatalog.HighBatteryLevel:
                return Bounded(HaCommandKind.HighBatteryLevel, p,
                               SettingRanges.HighBatteryMin, SettingRanges.HighBatteryMax, out cmd);

            case HaEntityCatalog.DrainWarning:
                return Flag(HaCommandKind.DrainWarning, p, out cmd);

            case HaEntityCatalog.DrainRate:
                return Bounded(HaCommandKind.DrainRate, p,
                               SettingRanges.DrainRateMin, SettingRanges.DrainRateMax, out cmd);

            case HaEntityCatalog.NetworkProfiles:
                return Flag(HaCommandKind.NetworkProfiles, p, out cmd);

            case HaEntityCatalog.UnknownNetworkPreset:
                return Text(HaCommandKind.UnknownNetworkPreset, p, out cmd);

            case HaEntityCatalog.StartupDelay:
                return Bounded(HaCommandKind.StartupDelay, p,
                               SettingRanges.StartupDelayMin, SettingRanges.StartupDelayMax, out cmd);

            case HaEntityCatalog.IconMode:
                // Enum.TryParse alone accepts any integer as a "valid" enum value, so the result is
                // checked against the defined members too.
                if (!Enum.TryParse<TrayIconMode>(p, ignoreCase: true, out var mode) ||
                    !Enum.IsDefined(mode)) return false;
                cmd = new HaCommand(HaCommandKind.IconMode, false, (int)mode, mode.ToString());
                return true;

            case HaEntityCatalog.DowntimeGap:
                return Bounded(HaCommandKind.DowntimeGap, p,
                               SettingRanges.DowntimeGapMin, SettingRanges.DowntimeGapMax, out cmd);

            default:
                return false;
        }
    }

    private static bool Flag(HaCommandKind kind, string p, out HaCommand cmd)
    {
        cmd = default;
        if (!TryParseBool(p, out bool on)) return false;
        cmd = new HaCommand(kind, on, 0, "");
        return true;
    }

    private static bool Bounded(HaCommandKind kind, string p, int min, int max, out HaCommand cmd)
    {
        cmd = default;
        if (!TryParseInteger(p, min, max, out int value)) return false;
        cmd = new HaCommand(kind, false, value, "");
        return true;
    }

    private static bool Text(HaCommandKind kind, string p, out HaCommand cmd)
    {
        cmd = default;
        if (p.Length == 0) return false;
        cmd = new HaCommand(kind, false, 0, p);
        return true;
    }

    private static bool TryParseBool(string p, out bool value)
    {
        switch (p.ToLowerInvariant())
        {
            case "on": case "true": case "1": case "yes": value = true;  return true;
            case "off": case "false": case "0": case "no": value = false; return true;
            default: value = false; return false;
        }
    }

    /// <summary>A bounded integer. Out of range is refused outright rather than clamped: a clamp would
    /// silently apply a value the sender never asked for.</summary>
    private static bool TryParseInteger(string p, int min, int max, out int value)
    {
        value = 0;
        // HA number entities may publish an integer ("80") or a float ("80.0"); accept both.
        if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return false;
        int rounded = (int)Math.Round(d);
        if (rounded < min || rounded > max) return false;
        value = rounded;
        return true;
    }
}
