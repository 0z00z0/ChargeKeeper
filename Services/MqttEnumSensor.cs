using ZeroZero.Mqtt.Discovery;

namespace ChargeKeeper.Services;

/// <summary>
/// A read-only reading drawn from a closed list of words, announced with the list so a receiver can
/// hold the state against it. The one shape the shared component set has no type of its own for:
/// every list it offers is a select, and a select is writable.
/// </summary>
/// <remarks>The component hierarchy is closed to the library that owns it, so the list is declared
/// through <see cref="MqttEntity.Extra"/> rather than as a typed property. Composed in one place
/// because the list and the device class that gives it meaning must never be declared apart. The
/// declared list holds only the words the reading can genuinely be: <see cref="MqttPayload.None"/>
/// is never one of them, and a reading absent from it — sentinel included — reads as unknown at the
/// receiver regardless.</remarks>
internal static class MqttEnumSensor
{
    /// <summary>The receiver's device class for a reading from a declared list.</summary>
    public const string DeviceClass = "enum";

    /// <summary>One entity over a fixed list of words, in the category the caller already publishes
    /// it under.</summary>
    /// <param name="description">The help text published as the entity's description attribute.</param>
    /// <param name="read">The word in force, or null when there is none.</param>
    /// <param name="include">Capability gating, or null for an entity that is always published.</param>
    public static MqttSensor Of(
        string entityId, string name, string description, string group, MqttEntityCategory category,
        string icon, IReadOnlyList<string> words, Func<string?> read, Func<bool>? include = null) =>
        new()
        {
            EntityId = entityId,
            Name = name,
            Description = description,
            Group = group,
            Category = category,
            Icon = icon,
            DeviceClass = DeviceClass,
            Extra = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["options"] = words,
            },
            Include = include,
            Read = read,
        };
}
