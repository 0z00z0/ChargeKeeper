using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using ChargeKeeper.Vendors.Surface;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Tests for the Surface vendor module's decision logic.
///
/// Deliberately hardware-free, same split as <see cref="HpChargeThresholdTests"/>: these exercise
/// the pure mapping helpers (<c>MapState</c>, <c>TryMapToLimiting</c>) rather than
/// <c>Read</c>/<c>SetThresholds</c>. Today the write path cannot reach hardware anyway — the stub
/// transport refuses everything — but the split must survive the transport becoming real, at
/// which point calling it from a test would rewrite a UEFI setting.
/// </summary>
public class SurfaceChargeThresholdTests
{
    // The Battery Limit setting's two values, as the module spells them. Hardcoded rather than
    // referenced from the implementation so a typo there fails a test instead of silently
    // agreeing with itself.
    private const string Limit = "Enabled";
    private const string Full = "Disabled";

    // ── MapState: Battery Limit state → vendor-neutral state ──────────────────

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
        // Guards the most likely wrong assumption about this module. Microsoft documents Battery
        // Limit as a fixed 50 % cap; the ~80 % figure people associate with Surface is Smart
        // Charging, a different feature. Reporting 80 here would show the user a cap the
        // firmware never applies.
        Assert.Equal(50, SurfaceChargeThreshold.MapState(true, isReadOnly: false).Stop);
    }

    [Fact]
    public void MapState_AlwaysReportsZeroStart()
    {
        // Surface has no charge-START threshold. Start is 0 by definition, and the dashboard
        // relies on that to suppress the start tick on the gauge.
        Assert.Equal(0, SurfaceChargeThreshold.MapState(true, isReadOnly: false).Start);
        Assert.Equal(0, SurfaceChargeThreshold.MapState(false, isReadOnly: false).Start);
    }

    [Fact]
    public void MapState_ReadOnlySetting_IsReachableButNotCapable()
    {
        // The contract distinguishes "unavailable" (Read returns null) from "reachable but the
        // firmware refuses writes" (non-null, Capable false). A SEMM-locked device is the latter.
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

    // ── TryMapToLimiting: numeric request → on/off ────────────────────────────

    [Theory]
    [InlineData(-1, 80)]    // negative start
    [InlineData(0, 101)]    // stop above 100
    [InlineData(50, 50)]    // zero gap
    [InlineData(90, 50)]    // inverted
    public void TryMapToLimiting_InvalidRange_Rejected(int start, int stop)
    {
        // Rejection must happen BEFORE any firmware contact — that is the whole reason this is
        // a separate pure function.
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
        // Note 80 snaps to a 50 % cap. Surface cannot express anything finer, which is exactly
        // why SupportsNumericThresholds is false and callers must re-Read.
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

    // ── Module surface ────────────────────────────────────────────────────────

    [Fact]
    public void Module_DeclaresNoNumericThresholdSupport()
    {
        // This flag is what hides the dashboard's percentage picker. Battery Limit is one on/off
        // switch with a firmware-fixed cap; a slider here would do nothing.
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
        // Surface has no LenovoSmartStandby equivalent. Claiming support would render an enabled
        // toggle that silently does nothing.
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
        // VendorCatalog probes candidates inside a static initializer, so a null provider would
        // surface as a TypeInitializationException at startup rather than a clean failure.
        var surface = new SurfacePowerModule();

        Assert.NotNull(surface.ChargeThreshold);
        Assert.NotNull(surface.Standby);
        Assert.NotNull(surface.ChargerInfo);
    }

    // ── Discrete charge modes ─────────────────────────────────────────────────

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

    // ── Inertness: the stub transport ─────────────────────────────────────────

    [Fact]
    public void StubTransport_ReadReturnsNull_SoTheCatalogSkipsThisModule()
    {
        // The single assertion that makes shipping this module safe. Read() returning null is the
        // contract's "unsupported hardware", so VendorCatalog moves past Surface on every machine
        // until a real transport replaces the stub.
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
        // Inert means writes report failure rather than pretending to succeed — a true return
        // would make the UI show a cap the hardware never applied.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetEnabled(enable));
    }

    [Fact]
    public void StubTransport_SetThresholdsFails_EvenForAValidRange()
    {
        // The range is legal, so TryMapToLimiting passes and the failure comes from the transport
        // rather than the guard. Proves inertness reaches the whole write path.
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetThresholds(0, 50));
    }

    [Fact]
    public void StubTransport_SetModeFails_EvenForAKnownId()
    {
        Assert.False(new SurfacePowerModule().ChargeThreshold.SetMode(Limit));
    }
}

/// <summary>
/// Startup safety for <c>VendorCatalog</c>. Its probe loop runs inside a STATIC INITIALIZER, so
/// an exception escaping it becomes a TypeInitializationException that kills app startup instead
/// of degrading to "Unavailable".
///
/// These stand in for launching the tray app, which needs elevation.
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
        // Nothing answers: the app must still run and report Unavailable, which means returning a
        // module rather than null.
        var first = new ThrowingModule();

        Assert.Same(first, VendorCatalog.SelectFrom([first, new SurfacePowerModule()]));
    }

    [Fact]
    public void SelectFrom_StubSurface_IsNeverSelected()
    {
        // Surface is registered last precisely because its stub probe cannot answer. Were it
        // selectable today, it would displace a real vendor's Unavailable reporting.
        var other = new ThrowingModule();

        Assert.IsNotType<SurfacePowerModule>(VendorCatalog.SelectFrom([other, new SurfacePowerModule()]));
    }

    [Fact]
    public void Active_ResolvesWithoutThrowing()
    {
        // Touching Active runs the REAL static initializer with the real candidate list, Surface
        // included — the closest a unit test gets to app startup without elevation.
        Assert.NotNull(VendorCatalog.Active);
        Assert.False(string.IsNullOrWhiteSpace(VendorCatalog.Active.VendorName));
    }
}
