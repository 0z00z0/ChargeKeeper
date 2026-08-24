using System;
using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// ChargeControlService is the single place the tray menu and the MQTT command path funnel through.
// The static-service primitives are faked so every branch runs without a live vendor RPC or settings
// file, and each test restores the global Primitives and StateChanged in a finally.
public class ChargeControlServiceTests
{
    private sealed class FakePrimitives : IChargeControlPrimitives
    {
        public bool OverrideActive;
        public bool SavedRevertThresholds;
        public int  CancelOverrideCalls;
        public bool? SetEnabledArg;
        public (int Start, int Stop)? ApplyThresholdsArg;
        public bool ApplyThresholdsResult = true;
        public readonly Dictionary<string, ThresholdPreset> Presets = new();

        // What the device would report back. Only a successful write moves it, so a test can ask
        // which preset the thresholds derive to after a failed one.
        public (int Start, int Stop) DeviceRange = (60, 80);
        public ChargeThresholdState DeviceState =>
            new(Capable: true, Enabled: true, Start: DeviceRange.Start, Stop: DeviceRange.Stop);
        public string? DerivedPreset =>
            ActivePresetPolicy.Match(Presets.Values.ToList(), DeviceState)?.Name;

        public bool IsOverrideActive => OverrideActive;
        public bool HasSavedRevertThresholds => SavedRevertThresholds;
        public void CancelOverride() => CancelOverrideCalls++;
        public void SetEnabled(bool enable) => SetEnabledArg = enable;
        public bool ApplyExplicitThresholds(int start, int stop)
        {
            ApplyThresholdsArg = (start, stop);
            if (ApplyThresholdsResult) DeviceRange = (start, stop);
            return ApplyThresholdsResult;
        }
        public ThresholdPreset? FindPreset(string name) => Presets.GetValueOrDefault(name);
    }

    // Swaps in the fake + a StateChanged counter, runs `body`, and always restores global state.
    private static void WithFake(FakePrimitives fake, Action<FakePrimitives, Func<int>> body)
    {
        var original = ChargeControlService.Primitives;
        int fired = 0;
        void Handler() => fired++;
        ChargeControlService.Primitives = fake;
        ChargeControlService.StateChanged += Handler;
        try { body(fake, () => fired); }
        finally
        {
            ChargeControlService.StateChanged -= Handler;
            ChargeControlService.Primitives = original;
        }
    }

    // Smart Charge enable/disable

    [Fact]
    public void SetSmartChargeEnabled_EnableWhileOverrideActive_WithSavedThresholds_CancelsOverride_NotSetEnabled()
    {
        WithFake(new FakePrimitives { OverrideActive = true, SavedRevertThresholds = true }, (fake, fired) =>
        {
            ChargeControlService.SetSmartChargeEnabled(true);
            Assert.Equal(1, fake.CancelOverrideCalls);
            Assert.Null(fake.SetEnabledArg);       // the restore IS the enable — no bare SetEnabled(true)
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetSmartChargeEnabled_EnableWhileOverrideActive_WithoutSavedThresholds_AlsoSetsEnabled()
    {
        // Activate() saves nothing when Smart Charge was already off, so the cancel's revert writes
        // nothing to the device — the enable must still reach it instead of being silently dropped.
        WithFake(new FakePrimitives { OverrideActive = true, SavedRevertThresholds = false }, (fake, fired) =>
        {
            ChargeControlService.SetSmartChargeEnabled(true);
            Assert.Equal(1, fake.CancelOverrideCalls);
            Assert.True(fake.SetEnabledArg);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetSmartChargeEnabled_EnableWithNoOverride_CallsSetEnabledTrue()
    {
        WithFake(new FakePrimitives { OverrideActive = false }, (fake, fired) =>
        {
            ChargeControlService.SetSmartChargeEnabled(true);
            Assert.Equal(0, fake.CancelOverrideCalls);
            Assert.True(fake.SetEnabledArg);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetSmartChargeEnabled_Disable_AlwaysSetEnabledFalse_EvenWithOverrideActive()
    {
        // Disabling is never the override's cancel path — the override-cancel branch is enable-only.
        WithFake(new FakePrimitives { OverrideActive = true }, (fake, fired) =>
        {
            ChargeControlService.SetSmartChargeEnabled(false);
            Assert.Equal(0, fake.CancelOverrideCalls);
            Assert.False(fake.SetEnabledArg);
            Assert.Equal(1, fired());
        });
    }

    // Explicit thresholds (MQTT number commands)

    [Fact]
    public void SetExplicitThresholds_WritesThroughAndReturnsResult()
    {
        WithFake(new FakePrimitives { ApplyThresholdsResult = true }, (fake, fired) =>
        {
            bool ok = ChargeControlService.SetExplicitThresholds(55, 75);
            Assert.True(ok);
            Assert.Equal((55, 75), fake.ApplyThresholdsArg);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetExplicitThresholds_RangeEqualToAPreset_DerivesToThatPreset()
    {
        // Nothing is stored, so a hand-picked range that happens to equal a preset is that preset.
        var fake = new FakePrimitives();
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 80, 100);
        WithFake(fake, (f, fired) =>
        {
            Assert.True(ChargeControlService.SetExplicitThresholds(80, 100));
            Assert.Equal((80, 100), f.ApplyThresholdsArg);
            Assert.Equal("Travel", f.DerivedPreset);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetExplicitThresholds_CustomRange_DerivesToNoPreset()
    {
        // The dashboard slider drag makes the range "custom": no preset carries these values.
        var fake = new FakePrimitives();
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 80, 100);
        WithFake(fake, (f, fired) =>
        {
            bool ok = ChargeControlService.SetExplicitThresholds(50, 80);
            Assert.True(ok);
            Assert.Equal((50, 80), f.ApplyThresholdsArg);
            Assert.Null(f.DerivedPreset);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetExplicitThresholds_FailedWrite_LeavesTheDerivedPresetOnTheUnmovedRange()
    {
        // A failed write must not leave the UI claiming "no preset" while the device never moved.
        var fake = new FakePrimitives { ApplyThresholdsResult = false, DeviceRange = (80, 100) };
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 80, 100);
        WithFake(fake, (f, fired) =>
        {
            bool ok = ChargeControlService.SetExplicitThresholds(50, 80);
            Assert.False(ok);
            Assert.Equal((50, 80), f.ApplyThresholdsArg);   // write attempted
            Assert.Equal("Travel", f.DerivedPreset);        // device still on Travel's range
            Assert.Equal(1, fired());                       // still an attempt → reconcile
        });
    }

    [Fact]
    public void MqttApplyThresholds_DerivesToNoPreset_LikeTheDashboardSlider()
    {
        // The HA charge_start/charge_stop numbers are the MQTT twin of the dashboard slider, so a
        // hand-picked range must resolve the same way on both surfaces: no preset.
        var fake = new FakePrimitives();
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 80, 100);
        WithFake(fake, (f, fired) =>
        {
            new ChargeControlActions().ApplyThresholds(45, 70);
            Assert.Equal((45, 70), f.ApplyThresholdsArg);
            Assert.Null(f.DerivedPreset);
            Assert.Equal(1, fired());
        });
    }

    // Apply preset

    [Fact]
    public void ApplyPresetByName_Known_WritesThresholds_WhichThenDeriveToThatPreset()
    {
        var fake = new FakePrimitives();
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 40, 60);
        WithFake(fake, (f, fired) =>
        {
            bool ok = ChargeControlService.ApplyPresetByName("Travel");
            Assert.True(ok);
            Assert.Equal((40, 60), f.ApplyThresholdsArg);
            Assert.Equal("Travel", f.DerivedPreset);   // the write alone makes it the active preset
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void ApplyPresetByName_WriteFails_DoesNotBecomeTheDerivedPreset()
    {
        var fake = new FakePrimitives { ApplyThresholdsResult = false, DeviceRange = (60, 80) };
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 40, 60);
        fake.Presets["Daily"]  = new ThresholdPreset("Daily",  60, 80);
        WithFake(fake, (f, fired) =>
        {
            bool ok = ChargeControlService.ApplyPresetByName("Travel");
            Assert.False(ok);
            Assert.Equal((40, 60), f.ApplyThresholdsArg);   // write attempted
            Assert.Equal("Daily", f.DerivedPreset);         // device never left the old range
            Assert.Equal(1, fired());                       // still an attempt → reconcile
        });
    }

    [Fact]
    public void ApplyPresetByName_UnknownName_NoOp_NoEvent()
    {
        WithFake(new FakePrimitives(), (fake, fired) =>
        {
            bool ok = ChargeControlService.ApplyPresetByName("does-not-exist");
            Assert.False(ok);
            Assert.Null(fake.ApplyThresholdsArg);
            Assert.Equal(0, fired());
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ApplyPresetByName_BlankName_NoOp_NoEvent(string? name)
    {
        WithFake(new FakePrimitives(), (fake, fired) =>
        {
            bool ok = ChargeControlService.ApplyPresetByName(name!);
            Assert.False(ok);
            Assert.Null(fake.ApplyThresholdsArg);
            Assert.Equal(0, fired());
        });
    }
}
