using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Fast-entry parsing for the keep-awake span: every accepted form, alongside the garbage it rejects.
public class KeepAwakeInputParserTests
{
    [Theory]
    [InlineData("3h", 180)]
    [InlineData("3H", 180)]            // case-insensitive
    [InlineData("3 h", 180)]           // spaces are noise
    [InlineData("90m", 90)]
    [InlineData("90min", 90)]
    [InlineData("1h30", 90)]           // bare tail after the hours is minutes
    [InlineData("1h30m", 90)]
    [InlineData("2h5", 125)]
    [InlineData("45", 45)]             // bare number too large to be an hour → minutes
    [InlineData("59", 59)]
    [InlineData("17m", 17)]            // explicit units beat the bare-number guess below
    public void TryParse_Durations(string input, int expectedMinutes)
    {
        Assert.True(KeepAwakeInputParser.TryParse(input, out var request));
        Assert.Equal(KeepAwakeKind.Duration, request.Kind);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), request.Duration);
        Assert.Null(request.Until);
    }

    [Theory]
    [InlineData("17:00", 17, 0)]
    [InlineData("7:30", 7, 30)]
    [InlineData("07:30", 7, 30)]
    [InlineData("1700", 17, 0)]
    [InlineData("930", 9, 30)]         // three digits are H:MM
    [InlineData("0730", 7, 30)]
    [InlineData("17", 17, 0)]          // a bare number that CAN be an hour reads as a clock time
    [InlineData("5", 5, 0)]
    [InlineData("23", 23, 0)]
    [InlineData("0:00", 0, 0)]
    public void TryParse_ClockTimes(string input, int hour, int minute)
    {
        Assert.True(KeepAwakeInputParser.TryParse(input, out var request));
        Assert.Equal(KeepAwakeKind.UntilTime, request.Kind);
        Assert.Equal(new TimeOnly(hour, minute), request.Until);
        Assert.Null(request.Duration);
    }

    [Fact]
    public void TryParse_BareNumber_SplitsAtTheLastValidHour()
    {
        // 23 is a clock time, 24 can't be — so it can only mean minutes. This boundary is the one
        // place the "bare number" rule is genuinely ambiguous, so pin both sides of it.
        Assert.True(KeepAwakeInputParser.TryParse("23", out var asTime));
        Assert.Equal(KeepAwakeKind.UntilTime, asTime.Kind);

        Assert.True(KeepAwakeInputParser.TryParse("24", out var asDuration));
        Assert.Equal(KeepAwakeKind.Duration, asDuration.Kind);
        Assert.Equal(TimeSpan.FromMinutes(24), asDuration.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("soon")]
    [InlineData("h")]
    [InlineData("m")]
    [InlineData("h30")]              // no hours value
    [InlineData("1h30x")]            // trailing junk
    [InlineData("25:00")]            // hour out of range
    [InlineData("17:99")]            // minute out of range
    [InlineData("2500")]
    [InlineData("999")]              // 9:99 is not a time, and is not silently reread as minutes
    [InlineData("12345")]            // too long to be a time, no units to be a duration
    [InlineData("0m")]               // a zero-length hold is not a hold
    [InlineData("0h0")]
    [InlineData("-5")]
    [InlineData("1.5h")]             // decimals are not part of the fast-entry alphabet
    public void TryParse_Rejects(string? input)
    {
        Assert.False(KeepAwakeInputParser.TryParse(input, out var request));
        Assert.Null(request);
    }

    [Theory]
    [InlineData("999999999h")]        // hours large enough to overflow TimeSpan.FromHours
    [InlineData("2147483647h")]       // int.MaxValue hours
    [InlineData("9999999999h")]       // past int.MaxValue, so the digits do not even parse
    [InlineData("999999999m")]
    [InlineData("999999999min")]
    [InlineData("1h999999999")]       // the absurd value in the minutes tail
    public void TryParse_OutOfRangeNumber_ReturnsFalseInsteadOfThrowing(string input)
    {
        // Settings parses the box on every keystroke, straight from a XAML event handler, so an
        // out-of-range span has to come back as false rather than as an exception.
        Assert.False(KeepAwakeInputParser.TryParse(input, out var request));
        Assert.Null(request);
    }
}
