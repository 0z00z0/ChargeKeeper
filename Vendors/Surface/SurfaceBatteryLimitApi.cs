namespace ChargeKeeper.Vendors.Surface;

/// <summary>The Battery Limit UEFI setting as reported by a transport.</summary>
/// <param name="Enabled">True when the firmware is capping the charge.</param>
/// <param name="IsReadOnly">
/// True when the setting is visible but refuses writes. Expected on SEMM-enrolled devices, where
/// an IT policy owns the UEFI settings and locks them against local changes, and on consumer
/// SKUs if a read-only path is ever found before a write path is.
/// </param>
internal sealed record SurfaceBatteryLimitSetting(bool Enabled, bool IsReadOnly);

/// <summary>
/// The seam between <see cref="SurfaceChargeThreshold"/> and whatever mechanism turns out to
/// drive Battery Limit from Windows. Implementations must NOT throw — the module's callers rely
/// on "null/false means unavailable", and <see cref="SurfaceBatteryLimitApi"/> is the only
/// backstop.
///
/// UNVERIFIED — no implementation here talks to hardware yet. Candidate mechanisms, in the order
/// worth trying on a real Surface:
///
/// 1. The SurfaceUefiManager managed API (<c>SurfaceUefiManager.dll</c>, type
///    <c>Microsoft.Surface.FirmwareOption</c>), installed by SurfaceUEFIManagerSetup.msi as part
///    of SEMM. It enumerates UEFI settings by name and can unlock them with the SEMM password.
///    Caveat: documented as COMMERCIAL Surface only — Microsoft support states consumer SKUs can
///    change Battery Limit from the UEFI screen alone.
/// 2. A WMI or registry surface from the Surface Integration / Surface System Aggregator driver.
///    Enumerate <c>root\wmi</c> and the Surface service registry keys on-device; nothing is
///    documented, so this is a search, not a lookup.
/// 3. Worst case the setting is firmware-menu-only from Windows and this module stays read-only,
///    or never becomes writable at all.
///
/// The Linux kernel drives Battery Limit through the Surface Aggregator Module, so the embedded
/// controller does accept a runtime command. Finding the WINDOWS path to that EC command is the
/// open research item — the mechanism exists, the managed entry point is what is missing.
/// </summary>
internal interface ISurfaceBatteryLimitTransport
{
    /// <summary>Current setting, or null when unavailable.</summary>
    SurfaceBatteryLimitSetting? Read();

    /// <summary>Turns the cap on or off. False on any failure.</summary>
    bool Write(bool enable);
}

/// <summary>
/// The transport that ships today: reports unavailable, always.
///
/// This is what keeps the module inert. <c>Read</c> returning null makes
/// <see cref="SurfaceChargeThreshold.Read"/> return null, which is the contract's "unsupported
/// hardware", so <c>VendorCatalog</c> skips the Surface candidate on every machine including a
/// real Surface. Shipping it costs nothing and changes no behaviour; replacing it is the entire
/// remaining work.
/// </summary>
internal sealed class StubSurfaceBatteryLimitTransport : ISurfaceBatteryLimitTransport
{
    public SurfaceBatteryLimitSetting? Read() => null;

    public bool Write(bool enable) => false;
}

/// <summary>
/// Thin wrapper over Surface's Battery Limit setting — the <c>HpBios</c> analogue, minus a
/// confirmed mechanism.
///
/// The difference from <c>HpBios</c> is deliberate: HP's surface is a known WMI namespace that
/// can be called directly, whereas Surface's is unknown, so the call is indirected through
/// <see cref="ISurfaceBatteryLimitTransport"/> and this class owns only the non-throwing
/// guarantee. Swapping <see cref="Transport"/> for a real implementation is the one-line change
/// that makes the module live.
///
/// Never throws: a probe that escapes runs inside <c>VendorCatalog</c>'s static initializer and
/// would surface as a TypeInitializationException at app startup. The catch here is a backstop
/// for a transport that breaks its own no-throw contract, not a licence to write one that does.
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
    /// Turns the cap on or off, false on any failure. Never throws.
    ///
    /// CAUTION for whoever implements the transport: a UEFI setting write is expected to need
    /// elevation, to need the SEMM password on an enrolled device, and — like HP's BIOS settings
    /// — to take effect only after a reboot. A true return will not mean the cap is live yet.
    /// </summary>
    internal static bool SetEnabled(bool enable)
    {
        try { return Transport.Write(enable); }
        catch { return false; }
    }
}
