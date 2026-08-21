namespace ChargeKeeper.Features;

/// <summary>
/// A user-toggleable on/off capability surfaced in the tray menu. One interface so
/// <see cref="ChargeKeeper.UI.TrayMenu"/> builds and refreshes every toggle the same way.
/// </summary>
internal interface IToggleFeature
{
    /// <summary>Display label shown in the menu.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the feature is available on this system. When false the menu item is greyed out
    /// rather than shown as an unchecked toggle.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Reads the feature's current state from the OS. May perform I/O.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Applies the requested state, <c>false</c> on failure. May block on service or RPC calls,
    /// so call it off the UI thread.
    /// </summary>
    bool SetEnabled(bool enabled);

    /// <summary>
    /// One combined (available, enabled) snapshot. A feature backed by a single expensive probe
    /// can override this to answer both from one round-trip.
    /// </summary>
    (bool Available, bool Enabled) ReadState() => (IsAvailable, IsEnabled);
}
