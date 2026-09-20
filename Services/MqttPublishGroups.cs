using ZeroZero.Mqtt;

namespace ChargeKeeper.Services;

/// <summary>The nine publishing groups the MQTT page toggles, one per Settings page. Smart Standby rides
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
    public const string Screen         = "screen";
    public const string Focus          = "focus";
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
        new(LidClose, "Lid delay",
            Info: "The lid-close delay and whether the computer locks when the lid shuts."),
        new(Screen, "Screen",
            Info: "The display brightness, and the button that puts back what it was."),
        // The one group whose entities are the only way in or out: a focus session is armed and
        // cancelled from here and from nowhere else, so switching this off leaves the feature with
        // no control at all.
        new(Focus, "Focus session",
            Info: "The session switch, how long it runs, which levers it uses, and how far a cancel "
                + "has got."),
        new(Notifications, "Notifications",
            Info: "The battery and drain warning thresholds, and whether each is armed."),
        new(Network, "Network",
            Info: "The detected network location and the profile matching it."),
        // The one group a new install has to opt into: diagnostics describe ChargeKeeper rather than
        // the battery, so they are noise on a single machine and only earn their place across a fleet.
        new(AppDiagnostics, "App diagnostics",
            Description: "Off by default — these describe ChargeKeeper, not the battery.",
            DefaultOn: false,
            Info: "The app version, the startup delay, the tray icon style and the downtime gap, "
                + "plus what the app last did and what its lid-close wait and keep-awake hold are "
                + "doing now."),
    ];
}
