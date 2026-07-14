using System.Collections.Generic;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure parsing + routing tests for the MQTT charge-control commands (issue #30). No broker, no RPC —
// the dispatch side is exercised through a spy IChargeControlActions.
public class HaCommandTests
{
    // ── Parsing ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ON", true)]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("OFF", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void TryParse_SmartCharge_AcceptsCommonBooleans(string payload, bool expected)
    {
        Assert.True(HaCommand.TryParse(HaDiscovery.CmdSmartCharge, payload, out var cmd));
        Assert.Equal(HaCommandKind.SmartCharge, cmd.Kind);
        Assert.Equal(expected, cmd.BoolValue);
    }

    [Fact]
    public void TryParse_SmartCharge_RejectsGarbage()
    {
        Assert.False(HaCommand.TryParse(HaDiscovery.CmdSmartCharge, "maybe", out _));
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("80.0", 80)]      // HA number entities may publish a float
    [InlineData(" 55 ", 55)]      // whitespace tolerated
    public void TryParse_Threshold_AcceptsInRangeNumbers(string payload, int expected)
    {
        Assert.True(HaCommand.TryParse(HaDiscovery.CmdChargeStart, payload, out var cmd));
        Assert.Equal(HaCommandKind.ChargeStart, cmd.Kind);
        Assert.Equal(expected, cmd.IntValue);
    }

    [Theory]
    [InlineData("0")]     // below MinThreshold
    [InlineData("4")]     // below MinThreshold
    [InlineData("101")]   // above MaxThreshold
    [InlineData("abc")]
    [InlineData("")]
    public void TryParse_Threshold_RejectsOutOfRangeOrNonNumeric(string payload)
    {
        Assert.False(HaCommand.TryParse(HaDiscovery.CmdChargeStop, payload, out _));
    }

    [Fact]
    public void TryParse_ChargeToFull_RequiresExactPressPayload()
    {
        // Only the exact discovery payload_press ("PRESS") may fire the kick-to-100% command — a
        // stray or retained payload must not (issue #30 review).
        Assert.True(HaCommand.TryParse(HaDiscovery.CmdChargeToFull, HaCommand.ButtonPress, out var cmd));
        Assert.Equal(HaCommandKind.ChargeToFull, cmd.Kind);
        Assert.True(HaCommand.TryParse(HaDiscovery.CmdChargeToFull, "  PRESS  ", out _));  // trimmed
    }

    [Theory]
    [InlineData("")]
    [InlineData("press")]      // wrong case
    [InlineData("Press")]
    [InlineData("ON")]
    [InlineData("1")]
    [InlineData("garbage")]
    public void TryParse_ChargeToFull_RejectsAnythingButPress(string payload)
    {
        Assert.False(HaCommand.TryParse(HaDiscovery.CmdChargeToFull, payload, out _));
    }

    [Fact]
    public void TryParse_Preset_KeepsName_RejectsEmpty()
    {
        Assert.True(HaCommand.TryParse(HaDiscovery.CmdPreset, " Travel ", out var cmd));
        Assert.Equal(HaCommandKind.SetPreset, cmd.Kind);
        Assert.Equal("Travel", cmd.StringValue);
        Assert.False(HaCommand.TryParse(HaDiscovery.CmdPreset, "   ", out _));
    }

    [Fact]
    public void TryParse_UnknownObjectId_ReturnsFalse()
    {
        Assert.False(HaCommand.TryParse("not_a_command", "ON", out _));
    }

    // ── Dispatch routing ─────────────────────────────────────────────────────────

    private sealed class SpyActions : IChargeControlActions
    {
        public (int Start, int Stop) Current = (60, 80);
        public bool? SmartCharge;
        public (int Start, int Stop)? Applied;
        public bool ChargeToFullCalled;
        public string? Preset;

        public (int Start, int Stop) CurrentThresholds() => Current;
        public void ApplyThresholds(int start, int stop) => Applied = (start, stop);
        public void SetSmartChargeEnabled(bool enable) => SmartCharge = enable;
        public void ChargeToFullOnce() => ChargeToFullCalled = true;
        public void ApplyPreset(string name) => Preset = name;
    }

    private static void Dispatch(HaCommandKind kind, SpyActions spy, bool b = false, int i = 0, string s = "")
        => HaCommandDispatcher.Dispatch(new HaCommand(kind, b, i, s), spy);

    [Fact]
    public void Dispatch_SmartCharge_TogglesEnable()
    {
        var spy = new SpyActions();
        Dispatch(HaCommandKind.SmartCharge, spy, b: true);
        Assert.True(spy.SmartCharge);
    }

    [Fact]
    public void Dispatch_ChargeStart_KeepsStopFixed_ClampsForGap()
    {
        var spy = new SpyActions { Current = (60, 80) };
        Dispatch(HaCommandKind.ChargeStart, spy, i: 70);
        Assert.Equal((70, 80), spy.Applied);
    }

    [Fact]
    public void Dispatch_ChargeStart_TooCloseToStop_ClampedDownToKeepMinGap()
    {
        var spy = new SpyActions { Current = (60, 80) };
        Dispatch(HaCommandKind.ChargeStart, spy, i: 79);   // would leave a 1-pt gap
        Assert.Equal((80 - PresetEditValidator.MinGap, 80), spy.Applied);
    }

    [Fact]
    public void Dispatch_ChargeStop_KeepsStartFixed_ClampsForGap()
    {
        var spy = new SpyActions { Current = (60, 80) };
        Dispatch(HaCommandKind.ChargeStop, spy, i: 90);
        Assert.Equal((60, 90), spy.Applied);
    }

    [Fact]
    public void Dispatch_ChargeStop_TooCloseToStart_ClampedUpToKeepMinGap()
    {
        var spy = new SpyActions { Current = (60, 80) };
        Dispatch(HaCommandKind.ChargeStop, spy, i: 61);   // would leave a 1-pt gap
        Assert.Equal((60, 60 + PresetEditValidator.MinGap), spy.Applied);
    }

    [Fact]
    public void Dispatch_ChargeToFull_TriggersOverride()
    {
        var spy = new SpyActions();
        Dispatch(HaCommandKind.ChargeToFull, spy);
        Assert.True(spy.ChargeToFullCalled);
    }

    [Fact]
    public void Dispatch_SetPreset_PassesNameThrough()
    {
        var spy = new SpyActions();
        Dispatch(HaCommandKind.SetPreset, spy, s: "Travel");
        Assert.Equal("Travel", spy.Preset);
    }
}
