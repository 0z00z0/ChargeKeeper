namespace ChargeKeeper.Vendors.Surface;

/// <summary>The Battery Limit UEFI setting as reported by a transport.</summary>
/// <param name="Enabled">True when the firmware is capping the charge.</param>
/// <param name="IsReadOnly">
/// True when the setting is visible but refuses writes, as on a SEMM-enrolled device whose UEFI
/// settings an IT policy owns.
/// </param>
internal sealed record SurfaceBatteryLimitSetting(bool Enabled, bool IsReadOnly);

/// <summary>
/// The seam between <see cref="SurfaceChargeThreshold"/> and whatever mechanism drives Battery
/// Limit from Windows. No user-mode mechanism is confirmed: Battery Limit is UEFI setting 407,
/// "Battery Profile", reachable only from the firmware menu at boot or by a signed SEMM package.
/// Implementations must not throw — callers rely on "null/false means unavailable".
/// </summary>
internal interface ISurfaceBatteryLimitTransport
{
    /// <summary>Current setting, or null when unavailable.</summary>
    SurfaceBatteryLimitSetting? Read();

    /// <summary>Turns the cap on or off. False on any failure.</summary>
    bool Write(bool enable);
}

/// <summary>
/// The transport that ships today: always reports unavailable, so <c>VendorCatalog</c> skips the
/// Surface candidate on every machine and the module stays inert.
/// </summary>
internal sealed class StubSurfaceBatteryLimitTransport : ISurfaceBatteryLimitTransport
{
    public SurfaceBatteryLimitSetting? Read() => null;

    public bool Write(bool enable) => false;
}

/// <summary>
/// Non-throwing wrapper over Surface's Battery Limit setting. An escaping exception would run
/// inside <c>VendorCatalog</c>'s static initializer and fail app startup, so the catches here
/// backstop a transport that breaks its own no-throw contract.
/// </summary>
internal static class SurfaceBatteryLimitApi
{
    private static readonly ISurfaceBatteryLimitTransport Transport = new StubSurfaceBatteryLimitTransport();

    /// <summary>The current setting, or null when unavailable. Never throws.</summary>
    internal static SurfaceBatteryLimitSetting? Read()
    {
        try { return Transport.Read(); }
        catch { return null; }
    }

    /// <summary>
    /// Turns the cap on or off, false on any failure. Never throws. A UEFI write is expected to
    /// take effect only after a reboot, so true does not mean the cap is live yet.
    /// </summary>
    internal static bool SetEnabled(bool enable)
    {
        try { return Transport.Write(enable); }
        catch { return false; }
    }
}
