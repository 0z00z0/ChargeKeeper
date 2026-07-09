namespace ChargeKeeper.Features;

/// <summary>
/// A user-toggleable on/off capability surfaced in the tray menu (e.g. Smart Charge,
/// Smart Standby, auto-start).  Abstracting the three toggles behind one interface lets
/// <see cref="ChargeKeeper.UI.TrayMenu"/> build and refresh them uniformly instead of
/// duplicating read-state / flip / write logic per feature.
/// </summary>
internal interface IToggleFeature
{
    /// <summary>Display label shown in the menu.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the feature is available on this system (driver present, service installed, etc.).
    /// When false, <see cref="IsEnabled"/> and <see cref="SetEnabled"/> are meaningless — the
    /// menu item should be greyed out rather than shown as an unchecked toggle.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Reads the feature's current state from the OS. May perform I/O.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Applies the requested state. May block (service/RPC calls) — call off the UI thread.
    /// Returns <c>true</c> if the write succeeded, <c>false</c> on failure.
    /// </summary>
    bool SetEnabled(bool enabled);
}
