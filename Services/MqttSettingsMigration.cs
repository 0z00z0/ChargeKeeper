using System.Text.Json;
using ZeroZero.Mqtt;

namespace ChargeKeeper.Services;

/// <summary>
/// Carries the broker block out of <c>settings.json</c> and into the module's own <c>mqtt.json</c>,
/// once. The module owns that file's name and its shape; ChargeKeeper owns only the move.
/// </summary>
/// <remarks>
/// <para><b>Absent configuration means already migrated.</b> A settings file with no MQTT keys at all
/// is a fresh installation or one whose keys have already been carried across, and either way there
/// is nothing to move — so the module's own defaults stand and nothing is written. Reading an absent
/// key as an explicit value is what would turn "nothing was said" into "the user chose the default",
/// and it would overwrite a broker block a user has since edited in the new file.</para>
/// <para><b>Nothing is ever cleared.</b> A key that is present is carried; a key that is absent leaves
/// the target's own value alone. The two are decided per key, not per file, because a settings.json
/// synced in from another machine can legitimately carry half the block.</para>
/// <para>The two one-shot repairs the old block carried are folded in here rather than replayed
/// afterwards, because both are questions about a value that only exists in the old file: the port
/// inherited from the days when 1883 was the default, and the two-state TLS switch the three-valued
/// encryption mode replaced.</para>
/// </remarks>
internal static class MqttSettingsMigration
{
    /// <summary>The port the broker setting defaulted to before Automatic existed. A settings.json
    /// written by an older build carries it forward, where it reads as a pinned port.</summary>
    internal const int RetiredDefaultPort = 1883;

    /// <summary>Every key the old broker block used. The presence of any one of them is what says
    /// there is something to move.</summary>
    private static readonly string[] _keys =
    [
        "HomeAssistantEnabled", "MqttBrokerHost", "MqttBrokerPort", "MqttPortDefaultRetiredForAutomatic",
        "MqttUsername", "MqttPassword", "MqttUseTls", "MqttEncryptionMode", "MqttTransportMode",
        "MqttDiscoveryPrefix", "MqttDeviceName", "MqttNodeId",
        "MqttPublishBatteryStatus", "MqttPublishSmartCharge", "MqttPublishKeepAwake",
        "MqttPublishLidClose", "MqttPublishNotifications", "MqttPublishNetwork",
        "MqttPublishAppDiagnostics",
    ];

    /// <summary>Which old publish flag becomes which group key.</summary>
    private static readonly (string Key, string Group)[] _groups =
    [
        ("MqttPublishBatteryStatus",  MqttPublishGroups.BatteryStatus),
        ("MqttPublishSmartCharge",    MqttPublishGroups.SmartCharge),
        ("MqttPublishKeepAwake",      MqttPublishGroups.KeepAwake),
        ("MqttPublishLidClose",       MqttPublishGroups.LidClose),
        ("MqttPublishNotifications",  MqttPublishGroups.Notifications),
        ("MqttPublishNetwork",        MqttPublishGroups.Network),
        ("MqttPublishAppDiagnostics", MqttPublishGroups.AppDiagnostics),
    ];

    /// <summary>
    /// Runs the move if it has not already run. The presence of the module's own file is the whole of
    /// the "already migrated" test: it is written the first time anything commits a broker setting, and
    /// carrying a stale settings.json over it afterwards would undo whatever has been edited since.
    /// </summary>
    /// <returns>True when something was carried across.</returns>
    public static bool Run(string legacySettingsPath, string dataDirectory, IMqttSettingsStore store)
    {
        try
        {
            if (File.Exists(Path.Combine(dataDirectory, MqttSettingsFile.DefaultFileName))) return false;
            if (!File.Exists(legacySettingsPath)) return false;

            using var document = JsonDocument.Parse(File.ReadAllText(legacySettingsPath));
            if (!Carries(document.RootElement)) return false;

            // Cloned out of the document so the mutation below outlives the using block.
            var root = document.RootElement.Clone();
            bool moved = false;
            store.Update(target => moved = Apply(root, target));

            if (moved)
                AppLog.Info($"MQTT settings moved from settings.json into "
                          + $"{MqttSettingsFile.DefaultFileName}.");
            return moved;
        }
        catch (Exception ex)
        {
            // The broker block is recoverable by hand and nothing else depends on it, so a failure
            // here must not take startup with it. Publishing stays off until it is set up again.
            AppLog.Error("MqttSettingsMigration.Run", ex);
            return false;
        }
    }

    /// <summary>Whether the old file says anything at all about MQTT. A file that does not is either
    /// brand new or already migrated, and in both cases the defaults are what should stand.</summary>
    internal static bool Carries(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && _keys.Any(k => root.TryGetProperty(k, out _));

    /// <summary>The move itself, over a parsed document rather than a file. Pure but for the target it
    /// mutates, so every path is reachable from a test.</summary>
    /// <returns>True when at least one key was carried.</returns>
    internal static bool Apply(JsonElement root, MqttSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Carries(root)) return false;

        if (Flag(root, "HomeAssistantEnabled") is { } enabled) target.Enabled = enabled;
        if (Text(root, "MqttBrokerHost") is { } host) target.Host = host;
        if (Text(root, "MqttUsername") is { } username) target.Username = username;
        if (Text(root, "MqttPassword") is { } password) target.Password = password;
        if (Text(root, "MqttDiscoveryPrefix") is { } prefix) target.DiscoveryPrefix = prefix;
        if (Text(root, "MqttDeviceName") is { } deviceName) target.DeviceName = deviceName;
        // The node id is the device id under its old name, and it is the unique_id stem: carrying it
        // unchanged is what keeps every existing entity rather than announcing forty-eight new ones.
        if (Text(root, "MqttNodeId") is { } deviceId) target.DeviceId = deviceId;

        if (Enum<MqttTransportMode>(root, "MqttTransportMode") is { } transport)
            target.TransportMode = transport;

        if (Encryption(root) is { } encryption) target.EncryptionMode = encryption;
        if (Port(root) is { Present: true } port) target.Port = port.Value;

        foreach (var (key, group) in _groups)
            if (Flag(root, key) is { } on) target.Groups[group] = on;

        return true;
    }

    /// <summary>The port to carry. <c>Present</c> and <c>Value</c> are separate because "carry
    /// Automatic" and "carry nothing" are different answers and both read as a null port.</summary>
    internal static (bool Present, int? Value) Port(JsonElement root)
    {
        if (!root.TryGetProperty("MqttBrokerPort", out var element)) return (false, null);
        if (element.ValueKind == JsonValueKind.Null) return (true, null);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int port)) return (false, null);

        // Safe because Automatic sweeps TCP 1883 as its first candidate: an install that genuinely
        // wants that port loses nothing, and one that only inherited it gains the detection. A port
        // chosen after the retirement ran is a choice and is kept.
        bool retired = Flag(root, "MqttPortDefaultRetiredForAutomatic") ?? false;
        return (true, !retired && port == RetiredDefaultPort ? null : port);
    }

    /// <summary>The encryption mode to carry: the three-valued setting when the old file has one, else
    /// the two-state switch it replaced. An absent pair carries nothing, so the module's own Automatic
    /// stands rather than being written as a choice nobody made.</summary>
    internal static MqttEncryptionMode? Encryption(JsonElement root)
    {
        if (Enum<MqttEncryptionMode>(root, "MqttEncryptionMode") is { } explicitMode) return explicitMode;

        // An explicit value is a choice and is carried across exactly, so an upgrade cannot start
        // negotiating what was pinned; an absent key is not a false and must never be taken for one.
        return Flag(root, "MqttUseTls") switch
        {
            true  => MqttEncryptionMode.On,
            false => MqttEncryptionMode.Off,
            null  => null,
        };
    }

    private static bool? Flag(JsonElement root, string key) =>
        root.TryGetProperty(key, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static T? Enum<T>(JsonElement root, string key) where T : struct, Enum =>
        root.TryGetProperty(key, out var element)
        && element.ValueKind == JsonValueKind.String
        && System.Enum.TryParse<T>(element.GetString(), ignoreCase: true, out var value)
        && System.Enum.IsDefined(value)
            ? value
            : null;
}
