using ChargeKeeper.Vendors;
using ChargeKeeper.Vendors.Hp;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Tests for the HP vendor module's decision logic.
///
/// Deliberately hardware-free: these exercise the pure mapping helpers
/// (<c>MapState</c>, <c>TryMapToLimiting</c>) rather than <c>Read</c>/<c>SetThresholds</c>,
/// which talk to <c>root\HP\InstrumentedBIOS</c>. Calling the write path from a test would
/// change real firmware settings on an HP machine, so the split exists precisely so these can
/// run anywhere — CI, a ThinkPad, or a non-HP dev box.
/// </summary>
public class HpChargeThresholdTests
{
    // The three modes exactly as HP's firmware spells them. Hardcoded rather than referenced
    // from the implementation so a typo introduced there fails a test instead of silently
    // agreeing with itself.
    private const string Maximize = "Maximize Battery Health Management";
    private const string Adaptive = "Let HP Manage My Battery Health";
    private const string Minimize = "Minimize Battery Health Management";

    // ── MapState: BIOS mode → vendor-neutral state ────────────────────────────

    [Theory]
    [InlineData(Maximize)]
    [InlineData(Adaptive)]
    public void MapState_LimitingModes_ReportEnabledWithNominalCap(string mode)
    {
        var state = HpChargeThreshold.MapState(mode, isReadOnly: false);

        Assert.True(state.Enabled);
        Assert.Equal(80, state.Stop);
    }

    [Fact]
    public void MapState_Minimize_ReportsDisabledAndChargesToFull()
    {
        // "Minimize Battery Health Management" is HP's charge-to-100% mode, i.e. no limit.
        var state = HpChargeThreshold.MapState(Minimize, isReadOnly: false);

        Assert.False(state.Enabled);
        Assert.Equal(100, state.Stop);
    }

    [Fact]
    public void MapState_ModeComparison_IsCaseInsensitive()
    {
        // Firmware casing has varied across HP BIOS revisions; a case flip must not be read as
        // "some other mode", which would invert Enabled.
        var state = HpChargeThreshold.MapState(Minimize.ToUpperInvariant(), isReadOnly: false);

        Assert.False(state.Enabled);
    }

    [Fact]
    public void MapState_AlwaysReportsZeroStart()
    {
        // HP has no charge-START threshold. Start is 0 by definition, and the dashboard relies
        // on that to suppress the start tick on the gauge.
        Assert.Equal(0, HpChargeThreshold.MapState(Maximize, isReadOnly: false).Start);
        Assert.Equal(0, HpChargeThreshold.MapState(Minimize, isReadOnly: false).Start);
    }

    [Fact]
    public void MapState_ReadOnlySetting_IsReachableButNotCapable()
    {
        // The contract distinguishes "unavailable" (Read returns null) from "reachable but the
        // firmware refuses writes" (non-null, Capable false). A read-only setting is the latter.
        var state = HpChargeThreshold.MapState(Maximize, isReadOnly: true);

        Assert.NotNull(state);
        Assert.False(state.Capable);
        Assert.True(state.Enabled);   // still reports what the firmware has selected
    }

    [Fact]
    public void MapState_WritableSetting_IsCapable()
    {
        Assert.True(HpChargeThreshold.MapState(Maximize, isReadOnly: false).Capable);
    }

    [Fact]
    public void MapState_UnrecognisedMode_TreatedAsLimiting()
    {
        // Fail safe: an unknown mode on newer firmware should not be reported as "no limit",
        // which would tell the user their battery is unprotected when it may not be.
        var state = HpChargeThreshold.MapState("Some Future HP Mode", isReadOnly: false);

        Assert.True(state.Enabled);
    }

    // ── TryMapToLimiting: numeric request → coarse mode ───────────────────────

    [Theory]
    [InlineData(-1, 80)]    // negative start
    [InlineData(0, 101)]    // stop above 100
    [InlineData(80, 80)]    // zero gap
    [InlineData(90, 80)]    // inverted
    public void TryMapToLimiting_InvalidRange_Rejected(int start, int stop)
    {
        // Rejection must happen BEFORE any firmware contact — that is the whole reason this is
        // a separate pure function.
        Assert.False(HpChargeThreshold.TryMapToLimiting(start, stop, out _));
    }

    [Fact]
    public void TryMapToLimiting_ZeroStart_IsAccepted()
    {
        // HP reports Start as 0, so a round-trip of its own state must be a legal request.
        // (The Lenovo module rejects start < 1; HP cannot inherit that guard.)
        Assert.True(HpChargeThreshold.TryMapToLimiting(0, 80, out _));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(0, 80)]
    [InlineData(75, 94)]
    public void TryMapToLimiting_BelowNearFull_SnapsToLimiting(int start, int stop)
    {
        Assert.True(HpChargeThreshold.TryMapToLimiting(start, stop, out bool limiting));
        Assert.True(limiting);
    }

    [Theory]
    [InlineData(0, 95)]
    [InlineData(0, 100)]
    public void TryMapToLimiting_AtOrAboveNearFull_SnapsToNoLimit(int start, int stop)
    {
        // Asking to charge to (near) full is the user saying "don't limit", which maps to
        // Minimize rather than to a cap HP cannot express.
        Assert.True(HpChargeThreshold.TryMapToLimiting(start, stop, out bool limiting));
        Assert.False(limiting);
    }

    // ── Module surface ────────────────────────────────────────────────────────

    [Fact]
    public void Module_DeclaresNoNumericThresholdSupport()
    {
        // This flag is what hides the dashboard's percentage picker. If it ever flips to true
        // without HP gaining a numeric BIOS setting, users get a slider that does nothing.
        Assert.False(new HpPowerModule().ChargeThreshold.SupportsNumericThresholds);
    }

    [Fact]
    public void Module_ReportsHpAsVendorName()
    {
        Assert.Equal("HP", new HpPowerModule().VendorName);
    }

    [Fact]
    public void Module_DoesNotClaimStandbySupport()
    {
        // HP has no LenovoSmartStandby equivalent. Claiming support would render an enabled
        // toggle that silently does nothing.
        var hp = new HpPowerModule();

        Assert.False(hp.Standby.IsSupported);
        Assert.False(hp.Standby.IsRunning());
        Assert.False(hp.Standby.SetEnabled(true));
    }

    [Fact]
    public void Module_ReportsNoAdapterWattage()
    {
        Assert.Null(new HpPowerModule().ChargerInfo.GetRatedWattage());
    }

    // ── Discrete charge modes ─────────────────────────────────────────────────

    [Fact]
    public void AvailableModes_ExposesAllThreeFirmwareModesInOrder()
    {
        // Order matters: it is the order the radio group renders, and matches HP Power Manager
        // (most protective first).
        var ids = new HpPowerModule().ChargeThreshold.AvailableModes.Select(m => m.Id).ToArray();

        Assert.Equal([Maximize, Adaptive, Minimize], ids);
    }

    [Fact]
    public void AvailableModes_IncludesTheAdaptiveModeThatSetEnabledCannotReach()
    {
        // This is the whole reason the mode API exists. SetEnabled is a bool and can only reach
        // Maximize and Minimize, so without this the middle mode could be *reported* by the
        // firmware but never *selected* by the user.
        var modes = new HpPowerModule().ChargeThreshold.AvailableModes;

        Assert.Contains(modes, m => m.Id == Adaptive);
    }

    [Fact]
    public void AvailableModes_AllHaveDisplayTextThatIsNotTheRawFirmwareId()
    {
        // Ids are firmware strings and must never be shown to the user.
        foreach (var mode in new HpPowerModule().ChargeThreshold.AvailableModes)
        {
            Assert.False(string.IsNullOrWhiteSpace(mode.Label));
            Assert.False(string.IsNullOrWhiteSpace(mode.Description));
            Assert.NotEqual(mode.Id, mode.Label);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not A Real Mode")]
    [InlineData("Maximize Battery Health")]   // close but not the firmware's exact spelling
    public void SetMode_UnknownId_RejectedWithoutFirmwareContact(string id)
    {
        // Must return false on the id check alone. If this ever reached HpBios.SetSetting, the
        // test would be writing to real firmware on an HP machine.
        Assert.False(new HpPowerModule().ChargeThreshold.SetMode(id));
    }

    [Fact]
    public void ModesAndNumericThresholds_AreMutuallyExclusive()
    {
        // The contract says a vendor exposes EITHER numeric thresholds OR modes. Assert it for
        // both shipped vendors so a future one cannot quietly claim both.
        var hp = new HpPowerModule().ChargeThreshold;

        Assert.False(hp.SupportsNumericThresholds);
        Assert.NotEmpty(hp.AvailableModes);
    }

    [Fact]
    public void Module_ProvidersAreNonNull()
    {
        // VendorCatalog probes candidates inside a static initializer, so a null provider would
        // surface as a TypeInitializationException at startup rather than a clean failure.
        var hp = new HpPowerModule();

        Assert.NotNull(hp.ChargeThreshold);
        Assert.NotNull(hp.Standby);
        Assert.NotNull(hp.ChargerInfo);
    }
}
