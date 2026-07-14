using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>The kind of charge-control command received over MQTT (issue #30).</summary>
internal enum HaCommandKind
{
    SmartCharge,   // switch → BoolValue
    ChargeStart,   // number → IntValue (%)
    ChargeStop,    // number → IntValue (%)
    ChargeToFull,  // button (no value)
    SetPreset,     // select → StringValue (preset name)
}

/// <summary>
/// A parsed, validated inbound charge-control command (issue #30). Produced by
/// <see cref="TryParse"/> from a raw MQTT (object-id, payload) pair; carries only typed values so
/// the dispatch side never re-parses strings. PURE — no side effects — so the parsing/validation
/// (the part that must defend against a malformed broker payload) is unit-tested directly.
/// </summary>
internal readonly record struct HaCommand(HaCommandKind Kind, bool BoolValue, int IntValue, string StringValue)
{
    /// <summary>The button entity's press payload — matches the discovery <c>payload_press</c>.</summary>
    public const string ButtonPress = "PRESS";

    /// <summary>
    /// Parses a command from the entity's object-id and the raw payload string, applying defensive,
    /// typed/enumerated validation (never free-text side effects):
    /// <list type="bullet">
    /// <item><c>smart_charge</c>: ON/OFF, true/false, 1/0, yes/no (case-insensitive) → bool.</item>
    /// <item><c>charge_start</c>/<c>charge_stop</c>: a number in
    /// [<see cref="PresetEditValidator.MinThreshold"/>, <see cref="PresetEditValidator.MaxThreshold"/>]
    /// %, rounded from a possible float; out-of-range or non-numeric is rejected.</item>
    /// <item><c>charge_to_full</c>: accepts ONLY the exact <see cref="ButtonPress"/> payload
    /// ("PRESS") — the most consequential command (kick to 100 %) must not fire on a stray or
    /// retained payload.</item>
    /// <item><c>preset</c>: a non-empty name (membership is checked at dispatch against the live
    /// preset list, not here).</item>
    /// </list>
    /// Returns false (and a default <paramref name="cmd"/>) for an unknown object-id or a payload
    /// that fails validation, so the caller simply ignores it.
    /// </summary>
    public static bool TryParse(string objectId, string payload, out HaCommand cmd)
    {
        cmd = default;
        string p = (payload ?? "").Trim();

        switch (objectId)
        {
            case HaDiscovery.CmdSmartCharge:
                if (!TryParseBool(p, out bool on)) return false;
                cmd = new HaCommand(HaCommandKind.SmartCharge, on, 0, "");
                return true;

            case HaDiscovery.CmdChargeStart:
                if (!TryParseThreshold(p, out int start)) return false;
                cmd = new HaCommand(HaCommandKind.ChargeStart, false, start, "");
                return true;

            case HaDiscovery.CmdChargeStop:
                if (!TryParseThreshold(p, out int stop)) return false;
                cmd = new HaCommand(HaCommandKind.ChargeStop, false, stop, "");
                return true;

            case HaDiscovery.CmdChargeToFull:
                // Exact-match the discovery payload_press; anything else (empty, stray, retained) is ignored.
                if (!string.Equals(p, ButtonPress, StringComparison.Ordinal)) return false;
                cmd = new HaCommand(HaCommandKind.ChargeToFull, false, 0, "");
                return true;

            case HaDiscovery.CmdPreset:
                if (p.Length == 0) return false;
                cmd = new HaCommand(HaCommandKind.SetPreset, false, 0, p);
                return true;

            default:
                return false;
        }
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

    private static bool TryParseThreshold(string p, out int value)
    {
        value = 0;
        // HA number entities may publish an integer ("80") or a float ("80.0"); accept both.
        if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return false;
        int rounded = (int)Math.Round(d);
        if (rounded < PresetEditValidator.MinThreshold || rounded > PresetEditValidator.MaxThreshold)
            return false;
        value = rounded;
        return true;
    }
}
