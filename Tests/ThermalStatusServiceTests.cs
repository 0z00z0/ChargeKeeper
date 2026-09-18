using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The plausibility bound <see cref="ThermalStatusService.IsPlausible"/> applies before a reading may
/// publish. Exercised directly against the pure predicate — nothing here touches the performance
/// counter or WMI.
/// </summary>
public class ThermalStatusServiceTests
{
    [Fact]
    public void AMissingReading_IsNotPlausible() =>
        Assert.False(ThermalStatusService.IsPlausible(null));

    [Fact]
    public void AnOrdinaryReading_IsPlausible() =>
        Assert.True(ThermalStatusService.IsPlausible(45.0));

    [Theory]
    // Past both ends of what a laptop thermal zone can honestly report — a broken sensor or a
    // broken read, never a very cold or very hot machine.
    [InlineData(-50.0)]
    [InlineData(200.0)]
    public void AnOutOfRangeReading_IsNotPlausible(double celsius) =>
        Assert.False(ThermalStatusService.IsPlausible(celsius));

    [Theory]
    // Each neighbour stays a whole degree inside the range, so the pair proves the boundary value
    // itself is accepted rather than merely being adjacent to a comfortably in-range one.
    [InlineData(ThermalStatusService.MinPlausibleCelsius)]
    [InlineData(ThermalStatusService.MaxPlausibleCelsius)]
    public void ABoundaryValue_IsPlausible(double celsius) =>
        Assert.True(ThermalStatusService.IsPlausible(celsius));
}
