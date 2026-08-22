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
    // Hardcoded rather than read from the implementation, so a typo there fails a test instead of
    // silently agreeing with itself.
    private const string Limit = "Enabled";
    private const string Full = "Disabled";

    // MapState: Battery Limit state → vendor-neutral state

    [Fact]
    public void MapState_LimitOn_ReportsEnabledWithNominalCap()
    {
        var state = SurfaceChargeThreshold.MapState(limitEnabled: true, isReadOnly: false);

        Assert.True(state.Enabled);
        Assert.Equal(50, state.Stop);
    }

    [Fact]
    public void MapState_LimitOff_ReportsDisabledAndChargesToFull()
    {
        var state = SurfaceChargeThreshold.MapState(limitEnabled: false, isReadOnly: false);

        Assert.False(state.Enabled);
        Assert.Equal(100, state.Stop);
    }

    [Fact]
    public void MapState_NominalCapIsFifty_NotEighty()
    {
        // Battery Limit is a firmware-fixed 50 % cap; the ~80 % figure associated with Surface is
        // Smart Charging, a different feature.
        Assert.Equal(50, SurfaceChargeThreshold.MapState(true, isReadOnly: false).Stop);
    }

    [Fact]
    public void MapState_AlwaysReportsZeroStart()
    {
        // Surface has no charge-start threshold, and the dashboard relies on 0 to suppress the
        // start tick on the gauge.
        Assert.Equal(0, SurfaceChargeThreshold.MapState(true, isReadOnly: false).Start);
        Assert.Equal(0, SurfaceChargeThreshold.MapState(false, isReadOnly: false).Start);
    }

    [Fact]
    public void MapState_ReadOnlySetting_IsReachableButNotCapable()
    {
        // A SEMM-locked device is reachable but write-refused: non-null with Capable false, as
        // opposed to "unavailable", which is a null Read.
        var state = SurfaceChargeThreshold.MapState(limitEnabled: true, isReadOnly: true);

        Assert.NotNull(state);
        Assert.False(state.Capable);
        Assert.True(state.Enabled);   // still reports what the firmware has selected
    }

    [Fact]
    public void MapState_WritableSetting_IsCapable()
    {
        Assert.True(SurfaceChargeThreshold.MapState(true, isReadOnly: false).Capable);
    }

    // TryMapToLimiting: numeric request → on/off

    [Theory]
    [InlineData(-1, 80)]    // negative start
    [InlineData(0, 101)]    // stop above 100
    [InlineData(50, 50)]    // zero gap
    [InlineData(90, 50)]    // inverted
    public void TryMapToLimiting_InvalidRange_Rejected(int start, int stop)
    {
        // Rejection has to happen before any firmware contact, which is why this is a pure function.
        Assert.False(SurfaceChargeThreshold.TryMapToLimiting(start, stop, out _));
    }

    [Fact]
    public void TryMapToLimiting_ZeroStart_IsAccepted()
    {
        // Surface reports Start as 0, so a round-trip of its own state must be a legal request.
        Assert.True(SurfaceChargeThreshold.TryMapToLimiting(0, 50, out _));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(0, 80)]
    [InlineData(75, 94)]
    public void TryMapToLimiting_BelowNearFull_SnapsToLimiting(int start, int stop)
    {
        // 80 snaps to the 50 % cap: Surface cannot express anything finer, which is why
        // SupportsNumericThresholds is false and callers must re-read.
        Assert.True(SurfaceChargeThreshold.TryMapToLimiting(start, stop, out bool limiting));
        Assert.True(limiting);
    }

    [Theory]
    [InlineData(0, 95)]
    [InlineData(0, 100)]
    public void TryMapToLimiting_AtOrAboveNearFull_SnapsToNoLimit(int start, int stop)
    {
        // Asking to charge to (near) full is the user saying "don't limit".
        Assert.True(SurfaceChargeThreshold.TryMapToLimiting(start, stop, out bool limiting));
        Assert.False(limiting);
    }

    // Module surface

    [Fact]
    public void Module_DeclaresNoNumericThresholdSupport()
    {
        // The flag hides the dashboard's percentage picker; a slider over one on/off switch would
        // do nothing.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SupportsNumericThresholds);
    }

    [Fact]
    public void Module_ReportsSurfaceAsVendorName()
    {
        Assert.Equal("Surface", new SurfacePowerModule().VendorName);
    }

    [Fact]
    public void Module_DoesNotClaimStandbySupport()
    {
        // Claiming support would render an enabled toggle that silently does nothing.
        var surface = new SurfacePowerModule();

        Assert.False(surface.Standby.IsSupported);
        Assert.False(surface.Standby.IsRunning());
        Assert.False(surface.Standby.SetEnabled(true));
    }

    [Fact]
    public void Module_ReportsNoAdapterWattage()
    {
        Assert.Null(new SurfacePowerModule().ChargerInfo.GetRatedWattage());
    }

    [Fact]
    public void Module_ProvidersAreNonNull()
    {
        // VendorCatalog probes candidates inside a static initialiser, so a null provider kills
        // startup with a TypeInitializationException instead of failing cleanly.
        var surface = new SurfacePowerModule();

        Assert.NotNull(surface.ChargeThreshold);
        Assert.NotNull(surface.Standby);
        Assert.NotNull(surface.ChargerInfo);
    }

    // Discrete charge modes

    [Fact]
    public void AvailableModes_ExposesBothStatesInOrder()
    {
        // Order matters: it is the order the radio group renders, most protective first.
        var ids = new SurfacePowerModule().ChargeThreshold.AvailableModes.Select(m => m.Id).ToArray();

        Assert.Equal([Limit, Full], ids);
    }

    [Fact]
    public void AvailableModes_AllHaveDisplayTextThatIsNotTheRawFirmwareId()
    {
        // Ids are firmware strings and must never be shown to the user.
        foreach (var mode in new SurfacePowerModule().ChargeThreshold.AvailableModes)
        {
            Assert.False(string.IsNullOrWhiteSpace(mode.Label));
            Assert.False(string.IsNullOrWhiteSpace(mode.Description));
            Assert.NotEqual(mode.Id, mode.Label);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not A Real Mode")]
    [InlineData("Enable")]   // close but not the setting's exact spelling
    public void SetMode_UnknownId_RejectedWithoutFirmwareContact(string id)
    {
        // Must return false on the id check alone, so this stays true once the transport is real.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetMode(id));
    }

    [Fact]
    public void ModesAndNumericThresholds_AreMutuallyExclusive()
    {
        // The contract says a vendor exposes EITHER numeric thresholds OR modes.
        var surface = new SurfacePowerModule().ChargeThreshold;

        Assert.False(surface.SupportsNumericThresholds);
        Assert.NotEmpty(surface.AvailableModes);
    }

    // Inertness: the stub transport

    [Fact]
    public void StubTransport_ReadReturnsNull_SoTheCatalogSkipsThisModule()
    {
        // A null Read is the contract's "unsupported hardware", so VendorCatalog moves past Surface
        // on every machine until a real transport replaces the stub.
        Assert.Null(new SurfacePowerModule().ChargeThreshold.Read());
    }

    [Fact]
    public void StubTransport_ReadModeReturnsNull()
    {
        Assert.Null(new SurfacePowerModule().ChargeThreshold.ReadMode());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StubTransport_WritesFailWithoutThrowing(bool enable)
    {
        // A true return would make the UI show a cap the hardware never applied.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetEnabled(enable));
    }

    [Fact]
    public void StubTransport_SetThresholdsFails_EvenForAValidRange()
    {
        // The range is legal, so the failure comes from the transport rather than the guard.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetThresholds(0, 50));
    }

    [Fact]
    public void StubTransport_SetModeFails_EvenForAKnownId()
    {
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetMode(Limit));
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

    [Fact]
    public void SelectFrom_AllCandidatesUnavailable_FallsBackToTheFirst()
    {
        // Nothing answers, so the app must still get a module back rather than null.
        var first = new ThrowingModule();

        Assert.Same(first, VendorCatalog.SelectFrom([first, new SurfacePowerModule()]));
    }

    [Fact]
    public void SelectFrom_StubSurface_IsNeverSelected()
    {
        // A selectable Surface would displace a real vendor's Unavailable reporting.
        var other = new ThrowingModule();

        Assert.IsNotType<SurfacePowerModule>(VendorCatalog.SelectFrom([other, new SurfacePowerModule()]));
    }

    [Fact]
    public void Active_ResolvesWithoutThrowing()
    {
        // Touching Active runs the real static initialiser against the real candidate list — the
        // closest a unit test gets to app startup without elevation.
        Assert.NotNull(VendorCatalog.Active);
        Assert.False(string.IsNullOrWhiteSpace(VendorCatalog.Active.VendorName));
    }
}
