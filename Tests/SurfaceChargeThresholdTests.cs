using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using ChargeKeeper.Vendors.Surface;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Surface vendor module decision logic, exercised through the pure mapping helpers rather than
/// <c>Read</c>/<c>SetThresholds</c>. The split has to survive the stub transport becoming real, at
/// which point calling the write path from a test would rewrite a UEFI setting.
/// </summary>
public class SurfaceChargeThresholdTests
{
    // Discrete charge modes

    [Theory]
    [InlineData("Enable")]   // close but not the setting's exact spelling
    public void SetMode_UnknownId_RejectedWithoutFirmwareContact(string id)
    {
        // Must return false on the id check alone, so this stays true once the transport is real.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetMode(id));
    }

    // Inertness: the stub transport

    [Theory]
    [InlineData(true)]
    public void StubTransport_WritesFailWithoutThrowing(bool enable)
    {
        // A true return would make the UI show a cap the hardware never applied.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetEnabled(enable));
    }
}

/// <summary>
/// Startup safety for <c>VendorCatalog</c>. Its probe loop runs inside a static initialiser, so an
/// escaping exception becomes a TypeInitializationException that kills app startup instead of
/// degrading to "Unavailable".
/// </summary>
public class VendorCatalogSelectionTests
{
    /// <summary>A module whose probe throws — the failure mode the catch in SelectFrom exists for.</summary>
    private sealed class ThrowingModule : IVendorPowerModule
    {
        public string VendorName => "Throwing";
        public IChargeThresholdProvider ChargeThreshold { get; } = new ThrowingThreshold();
        public IStandbyProvider Standby { get; } = new SurfacePowerModule().Standby;
        public IChargerInfoProvider ChargerInfo { get; } = new SurfacePowerModule().ChargerInfo;

        private sealed class ThrowingThreshold : IChargeThresholdProvider
        {
            public bool SupportsNumericThresholds => false;
            public ChargeThresholdState? Read() => throw new InvalidOperationException("probe blew up");
            public bool SetEnabled(bool enable) => false;
            public bool SetThresholds(int start, int stop) => false;
            public IReadOnlyList<ChargeMode> AvailableModes => [];
            public string? ReadMode() => null;
            public bool SetMode(string id) => false;
        }
    }

    [Fact]
    public void SelectFrom_ThrowingProbe_DoesNotEscape()
    {
        // If this ever throws, app startup dies with a TypeInitializationException.
        var selected = VendorCatalog.SelectFrom([new ThrowingModule(), new SurfacePowerModule()]);

        Assert.NotNull(selected);
    }
}
