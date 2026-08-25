using ZeroZero.Mqtt;

namespace ChargeKeeper.Services;

/// <summary>The publishing groups the MQTT page toggles, one per Settings page. Smart Standby rides
/// with <see cref="LidClose"/> rather than taking a group of its own: the dashboard already pairs
/// them — one decides how the machine sleeps, the other when.</summary>
/// <remarks>The keys are persisted, per key and never per index, so inserting or reordering a group
/// cannot move a user's choices onto a different one. A key is therefore as permanent as an entity
/// id: rename the label freely, never the key.</remarks>
internal static class MqttPublishGroups
{
    public const string BatteryStatus  = "battery_status";
    public const string SmartCharge    = "smart_charge";
    public const string KeepAwake      = "keep_awake";
    public const string LidClose       = "lid_close";
    public const string Notifications  = "notifications";
    public const string Network        = "network";
    public const string AppDiagnostics = "app_diagnostics";

    /// <summary>The declarations the panel renders one row per, in the order the Settings pages run.</summary>
    public static IReadOnlyList<PublishGroup> Declared { get; } =
    [
        new(BatteryStatus, "Battery status",
            Info: "Level, charge state, power draw and the battery health figures."),
        new(SmartCharge, "Smart Charge",
            Info: "The charge limit, the preset in force and the one-shot charge to full."),
        new(KeepAwake, "Keep Awake",
            Info: "Whether a session is holding the computer awake, and when it expires."),
        new(LidClose, "Lid close",
            Info: "The lid-close delay and whether the computer locks when the lid shuts."),
        new(Notifications, "Notifications",
            Info: "The battery and drain warning thresholds, and whether each is armed."),
        new(Network, "Network",
            Info: "The detected network location and the profile matching it."),
        // The one group a new install has to opt into: diagnostics describe ChargeKeeper rather than
        // the battery, so they are noise on a single machine and only earn their place across a fleet.
        new(AppDiagnostics, "App diagnostics",
            Description: "Off by default — these describe ChargeKeeper, not the battery.",
            DefaultOn: false,
            Info: "The app version, the startup delay, the tray icon style and the downtime gap."),
    ];
}
