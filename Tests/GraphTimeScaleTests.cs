using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class GraphTimeScaleTests
{
    // GraphTimeScale is internal and a public [Theory] parameter cannot be, so InlineData passes the
    // scale name and the enum is parsed inside the method body.
    [Theory]
    [InlineData("FifteenMinutes", 15)]
    [InlineData("OneHour",        60)]
    [InlineData("SixHours",       360)]
    [InlineData("TwelveHours",    720)]
    [InlineData("OneDay",         1440)]
    [InlineData("OneWeek",        10080)]
    [InlineData("FourteenDays",   20160)]
    public void ToTimeSpan_MapsToExpectedMinutes(string scaleName, int expectedMinutes)
    {
        var scale = Enum.Parse<GraphTimeScale>(scaleName);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), scale.ToTimeSpan());
    }
}
