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
    [Theory]
    // Past both ends of what a laptop thermal zone can honestly report — a broken sensor or a
    // broken read, never a very cold or very hot machine.
    [InlineData(200.0)]
    public void AnOutOfRangeReading_IsNotPlausible(double celsius) =>
        Assert.False(ThermalStatusService.IsPlausible(celsius));
}
