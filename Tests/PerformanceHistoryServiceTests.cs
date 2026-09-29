using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The performance log's bound. It is written at a rate the user chooses, so its age rule alone
/// does not limit its size.
/// </summary>
public class PerformanceHistoryServiceTests
{
    /// <summary>Age alone cannot bound this file, because the rate is the user's to choose. The row
    /// cap is why, and it is the same shared prune the battery history uses.</summary>
    [Fact]
    public void TheRowCapIsWhatAgeAloneCannotSupply()
    {
        Assert.True(PerformanceHistoryService.MaxRows > 0,
            "the performance log must carry a row cap: at 10 Hz, seven days of age is millions of rows");
    }
}
