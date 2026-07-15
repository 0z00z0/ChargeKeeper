using System;
using System.Collections.Generic;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Composition tests for the shared ChargeControlService (issue #40 item 4) — the single place the
// tray menu AND the MQTT command path funnel through. The static-service primitives are faked so the
// branches (override-cancel vs SetEnabled; apply-preset found/not-found/failed) are exercised without
// a live vendor RPC or settings file. Runs sequentially within the class (xUnit does not parallelise
// tests in one class), and each test restores the global Primitives + StateChanged in a finally.
public class ChargeControlServiceTests
{
    private sealed class FakePrimitives : IChargeControlPrimitives
    {
        public bool OverrideActive;
        public int  CancelOverrideCalls;
        public bool? SetEnabledArg;
        public (int Start, int Stop)? ApplyThresholdsArg;
        public bool ApplyThresholdsResult = true;
        public string? SetActivePresetArg;
        public int  SetActivePresetCalls;   // distinguishes "cleared to null" from "never called"
        public readonly Dictionary<string, ThresholdPreset> Presets = new();

        public bool IsOverrideActive => OverrideActive;
        public void CancelOverride() => CancelOverrideCalls++;
        public void SetEnabled(bool enable) => SetEnabledArg = enable;
        public bool ApplyExplicitThresholds(int start, int stop)
        {
            ApplyThresholdsArg = (start, stop);
            return ApplyThresholdsResult;
        }
        public ThresholdPreset? FindPreset(string name) => Presets.GetValueOrDefault(name);
        public void SetActivePreset(string? name) { SetActivePresetArg = name; SetActivePresetCalls++; }
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

    // ── Smart Charge enable/disable ────────────────────────────────────────────────

    [Fact]
    public void SetSmartChargeEnabled_EnableWhileOverrideActive_CancelsOverride_NotSetEnabled()
    {
        WithFake(new FakePrimitives { OverrideActive = true }, (fake, fired) =>
        {
            ChargeControlService.SetSmartChargeEnabled(true);
            Assert.Equal(1, fake.CancelOverrideCalls);
            Assert.Null(fake.SetEnabledArg);       // did NOT fall through to a bare SetEnabled(true)
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

    // ── Explicit thresholds (MQTT number commands) ─────────────────────────────────

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
    public void SetExplicitThresholds_WithoutClearFlag_LeavesActivePresetUntouched()
    {
        // The Settings preset-edit / delete-fallback callers manage ActivePreset themselves — the
        // write must NOT touch it (default clearActivePreset:false).
        WithFake(new FakePrimitives { ApplyThresholdsResult = true }, (fake, fired) =>
        {
            ChargeControlService.SetExplicitThresholds(55, 75);
            Assert.Equal(0, fake.SetActivePresetCalls);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetExplicitThresholds_ClearActivePreset_OnSuccess_ClearsToNull()
    {
        // The dashboard slider drag makes the value "custom" — the persisted ActivePreset is cleared.
        WithFake(new FakePrimitives { ApplyThresholdsResult = true }, (fake, fired) =>
        {
            bool ok = ChargeControlService.SetExplicitThresholds(50, 80, clearActivePreset: true);
            Assert.True(ok);
            Assert.Equal((50, 80), fake.ApplyThresholdsArg);
            Assert.Equal(1, fake.SetActivePresetCalls);
            Assert.Null(fake.SetActivePresetArg);   // cleared, not set to a name
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void SetExplicitThresholds_ClearActivePreset_OnFailedWrite_DoesNotClear()
    {
        // A failed device write must not leave the UI claiming "no preset" when the device never moved.
        WithFake(new FakePrimitives { ApplyThresholdsResult = false }, (fake, fired) =>
        {
            bool ok = ChargeControlService.SetExplicitThresholds(50, 80, clearActivePreset: true);
            Assert.False(ok);
            Assert.Equal((50, 80), fake.ApplyThresholdsArg);   // write attempted
            Assert.Equal(0, fake.SetActivePresetCalls);        // but ActivePreset left intact
            Assert.Equal(1, fired());                          // still an attempt → reconcile
        });
    }

    // ── Apply preset ───────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyPresetByName_Known_WritesThresholds_ThenPersistsActivePreset()
    {
        var fake = new FakePrimitives();
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 40, 60);
        WithFake(fake, (f, fired) =>
        {
            bool ok = ChargeControlService.ApplyPresetByName("Travel");
            Assert.True(ok);
            Assert.Equal((40, 60), f.ApplyThresholdsArg);
            Assert.Equal("Travel", f.SetActivePresetArg);
            Assert.Equal(1, fired());
        });
    }

    [Fact]
    public void ApplyPresetByName_WriteFails_DoesNotPersistActivePreset()
    {
        var fake = new FakePrimitives { ApplyThresholdsResult = false };
        fake.Presets["Travel"] = new ThresholdPreset("Travel", 40, 60);
        WithFake(fake, (f, fired) =>
        {
            bool ok = ChargeControlService.ApplyPresetByName("Travel");
            Assert.False(ok);
            Assert.Equal((40, 60), f.ApplyThresholdsArg);   // write attempted
            Assert.Null(f.SetActivePresetArg);              // but NOT persisted
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
            Assert.Null(fake.SetActivePresetArg);
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
