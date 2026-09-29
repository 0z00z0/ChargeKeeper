using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The command seam: one inbound payload in, and either a refusal carrying a reason or exactly one
/// call on the services the Settings window drives. Every bound enforced here is the bound the UI
/// enforces, from the same constant — a remote write can reach nothing the UI cannot.
/// </summary>
public class MqttEntityCommandTests
{
    private static MqttCommandVerdict Send(
        string entityId, string payload,
        IChargeControlActions? charge = null, ISettingsActions? settings = null,
        LiveState? live = null, SurfaceState? surface = null)
    {
        var set = MqttTestBed.Build(
            live ?? MqttTestBed.Live(), surface ?? MqttTestBed.Surface(),
            charge: charge, settings: settings);
        return MqttTestBed.Command(set, entityId).Accept(payload);
    }

    // ── The two charge thresholds, which are one control split over two entities ────────────────

    [Fact]
    public void ANewStart_KeepsTheStopWhereItIs()
    {
        var charge = new FakeChargeControl { Current = (60, 80) };
        MqttTestBed.Run(Send(MqttEntityCatalog.ChargeStart, "65", charge: charge));
        Assert.Equal([(65, 80)], charge.Applied);
    }

    [Theory]
    [InlineData("101")]
    public void AThresholdOutsideTheDeclaredRange_IsRefusedBeforeAnyClampIsConsidered(string payload)
    {
        var charge = new FakeChargeControl();
        Assert.Equal(MqttCommandOutcome.OutOfRange,
                     Send(MqttEntityCatalog.ChargeStart, payload, charge: charge).Outcome);
        Assert.Empty(charge.Applied);
    }

    // ── The button ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ON")]
    public void AnythingElseOnTheButton_IsMalformedAndFiresNothing(string payload)
    {
        // Exact match only: a kick to 100 % must not fire on a stray payload.
        var charge = new FakeChargeControl();
        Assert.Equal(MqttCommandOutcome.Malformed,
                     Send(MqttEntityCatalog.ChargeToFull, payload, charge: charge).Outcome);
        Assert.Equal(0, charge.ChargeToFullCalls);
    }
}
