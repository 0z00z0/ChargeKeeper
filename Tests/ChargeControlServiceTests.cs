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
    /// <summary>What a test says asked. The branches are what is under test here, not the
    /// wording, and every one of these calls is made by the test rather than by a trigger.</summary>
    private static readonly ActionCause ACause = "a test";

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
        public void CancelOverride(ActionCause cause) => CancelOverrideCalls++;
        public void SetEnabled(bool enable) => SetEnabledArg = enable;
        public bool ApplyExplicitThresholds(int start, int stop, ActionCause cause)
        {
            ApplyThresholdsArg = (start, stop);
            if (ApplyThresholdsResult) DeviceRange = (start, stop);
            return ApplyThresholdsResult;
        }
        public bool ApplyMode(string id) => true;
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
    public void SetSmartChargeEnabled_EnableWhileOverrideActive_WithoutSavedThresholds_AlsoSetsEnabled()
    {
        // Activate() saves nothing when Smart Charge was already off, so the cancel's revert writes
        // nothing to the device — the enable must still reach it instead of being silently dropped.
        WithFake(new FakePrimitives { OverrideActive = true, SavedRevertThresholds = false }, (fake, fired) =>
        {
            ChargeControlService.SetSmartChargeEnabled(true, ACause);
            Assert.Equal(1, fake.CancelOverrideCalls);
            Assert.True(fake.SetEnabledArg);
            Assert.Equal(1, fired());
        });
    }
}
