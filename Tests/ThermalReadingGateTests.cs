using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The plausibility gate issue #157 requires before any temperature reading may publish: existing is
/// not enough, a reading also has to sit in a plausible range and move over several samples. Exercised
/// against synthetic sequences only — nothing here touches the performance counter or WMI.
/// </summary>
public class ThermalReadingGateTests
{
    private static bool[] Feed(ThermalReadingGate gate, params double?[] readings)
    {
        var results = new bool[readings.Length];
        for (int i = 0; i < readings.Length; i++) results[i] = gate.Observe(readings[i]);
        return results;
    }

    [Fact]
    public void AHealthyVaryingSequence_PublishesOnceTheWindowFills()
    {
        var gate = new ThermalReadingGate();

        // Five readings, each different from its neighbour — a machine tracking load, as measured.
        var results = Feed(gate, 45.0, 47.0, 46.0, 50.0, 48.0);

        // Not enough evidence on the first four; the fifth completes the window and finds movement.
        Assert.Equal([false, false, false, false, true], results);

        // And it keeps publishing as further readings slide the window, so long as they keep moving.
        Assert.True(gate.Observe(49.0));
    }

    [Fact]
    public void AFlatConstantSequence_NeverPublishes()
    {
        var gate = new ThermalReadingGate();

        // The classic false positive: present, readable, and stuck — as measured on the development
        // machine across eight reads over 8 s.
        var results = Feed(gate, 69.05, 69.05, 69.05, 69.05, 69.05, 69.05, 69.05, 69.05);

        Assert.All(results, Assert.False);
    }

    [Fact]
    public void AnOutOfRangeSequence_NeverPublishes()
    {
        var gate = new ThermalReadingGate();

        // Past both ends of what a laptop thermal zone can honestly report, and varying besides —
        // movement alone must not be enough to launder an implausible value into publication.
        var results = Feed(gate, 200.0, -50.0, 180.0, -40.0, 999.0);

        Assert.All(results, Assert.False);
    }

    [Fact]
    public void ASourceThatIsAlwaysAbsent_NeverPublishesAndNeverThrows()
    {
        var gate = new ThermalReadingGate();

        var exception = Record.Exception(() =>
        {
            var results = Feed(gate, null, null, null, null, null);
            Assert.All(results, Assert.False);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void AMissingReadingMidSequence_IsSkippedRatherThanCountedAsNotVarying()
    {
        var gate = new ThermalReadingGate();

        // Four real readings either side of one transient failure. The failure must not consume a
        // window slot: a healthy machine whose counter hiccups once should not be punished for it.
        var results = Feed(gate, 45.0, 47.0, null, 46.0, 50.0, 48.0);

        Assert.Equal([false, false, false, false, false, true], results);
    }

    [Fact]
    public void AnOutOfRangeReadingMidSequence_IsSkippedAndCannotPoisonTheWindow()
    {
        var gate = new ThermalReadingGate();

        var results = Feed(gate, 45.0, 47.0, 999.0, 46.0, 50.0, 48.0);

        Assert.Equal([false, false, false, false, false, true], results);
    }

    [Theory]
    // Each neighbour stays a whole degree inside the range, so the pair proves the boundary value
    // itself is accepted rather than merely being adjacent to a comfortably in-range one.
    [InlineData(ThermalReadingGate.MinPlausibleCelsius, ThermalReadingGate.MinPlausibleCelsius + 1)]
    [InlineData(ThermalReadingGate.MaxPlausibleCelsius, ThermalReadingGate.MaxPlausibleCelsius - 1)]
    public void ABoundaryValue_CountsAsPlausible(double boundary, double neighbour)
    {
        var gate = new ThermalReadingGate();

        var results = Feed(gate, boundary, neighbour, boundary, neighbour, boundary);

        Assert.Equal([false, false, false, false, true], results);
    }
}
