using System.Globalization;
using System.Text.Json;

namespace ChargeKeeper.Services;

/// <summary>
/// The settings, network and diagnostic values behind every entity that does not come off a battery
/// reading. Published to its own retained topic, so a settings change does not have to wait for a
/// battery tick and a battery tick does not re-send the settings.
/// </summary>
/// <remarks>The broker credentials are deliberately absent, and there is no field they could reach:
/// publishing them over the very broker they authenticate to would put them in plain text in the
/// consumer's log and in a retained topic.</remarks>
internal readonly record struct HaSurfaceState(
    bool TravelOverrideActive,
    bool KeepAwakeActive,
    string KeepAwakeFor,
    DateTimeOffset? KeepAwakeExpires,
    bool KeepAwakeDisplayOn,
    bool LidDelayEnabled,
    int LidDelayMinutes,
    bool LidDelayLockOnClose,
    bool SmartStandbyRunning,
    bool LowBatteryWarning,
    int LowBatteryLevel,
    bool HighBatteryWarning,
    int HighBatteryLevel,
    bool DrainWarning,
    int DrainRate,
    bool NetworkProfilesEnabled,
    string UnknownNetworkPreset,
    string? NetworkAlias,
    string? NetworkIpAddress,
    string? NetworkAdapterName,
    string? MatchedNetworkProfile,
    string AppVersion,
    int StartupDelaySeconds,
    TrayIconMode IconMode,
    int DowntimeGapMinutes);

/// <summary>Pure payload builder for the settings topic, alongside <see cref="HaDiscovery"/>'s builder
/// for the battery one. Same conventions: an unknown optional is omitted so its entity reads
/// "unknown", and anything backing a <c>select</c> is always present, because a consumer ignores an
/// empty payload on one and would go on showing the last option it saw.</summary>
internal static class HaSurfacePayload
{
    /// <summary>Nothing matched, for the profile sensor. A matched-nothing reading is known, not
    /// unknown, so the field is published rather than omitted.</summary>
    public const string NoProfile = HaDiscovery.PresetNone;

    public static string Build(HaSurfaceState s)
    {
        var payload = new Dictionary<string, object>
        {
            ["travel_override"]        = s.TravelOverrideActive,
            ["keep_awake"]             = s.KeepAwakeActive,
            ["keep_awake_for"]         = s.KeepAwakeFor,
            ["keep_awake_display_on"]  = s.KeepAwakeDisplayOn,
            ["lid_delay"]              = s.LidDelayEnabled,
            ["lid_delay_minutes"]      = s.LidDelayMinutes,
            ["lid_delay_lock"]         = s.LidDelayLockOnClose,
            ["smart_standby"]          = s.SmartStandbyRunning,
            ["low_battery_warning"]    = s.LowBatteryWarning,
            ["low_battery_level"]      = s.LowBatteryLevel,
            ["high_battery_warning"]   = s.HighBatteryWarning,
            ["high_battery_level"]     = s.HighBatteryLevel,
            ["drain_warning"]          = s.DrainWarning,
            ["drain_rate"]             = s.DrainRate,
            ["network_profiles"]       = s.NetworkProfilesEnabled,
            ["unknown_network_preset"] = s.UnknownNetworkPreset,
            ["network_profile"]        = s.MatchedNetworkProfile ?? NoProfile,
            ["app_version"]            = s.AppVersion,
            ["startup_delay"]          = s.StartupDelaySeconds,
            ["icon_mode"]              = s.IconMode.ToString(),
            ["downtime_gap"]           = s.DowntimeGapMinutes,
        };
        // An HA timestamp sensor wants a full ISO 8601 instant with an offset; no session with a
        // clock expiry means no value at all.
        if (s.KeepAwakeExpires is { } expiry)
            payload["keep_awake_expires"] = expiry.ToString("o", CultureInfo.InvariantCulture);
        if (s.NetworkAlias      is { Length: > 0 } alias)   payload["network_alias"]   = alias;
        if (s.NetworkIpAddress  is { Length: > 0 } ip)      payload["network_ip"]      = ip;
        if (s.NetworkAdapterName is { Length: > 0 } adapter) payload["network_adapter"] = adapter;
        return JsonSerializer.Serialize(payload);
    }
}
