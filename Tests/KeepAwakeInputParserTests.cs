using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Fast-entry parsing for the keep-awake span: every accepted form, alongside the garbage it rejects.
public class KeepAwakeInputParserTests
{
    [Theory]
    [InlineData("999999999h")]        // hours large enough to overflow TimeSpan.FromHours
    [InlineData("1h999999999")]       // the absurd value in the minutes tail
    public void TryParse_OutOfRangeNumber_ReturnsFalseInsteadOfThrowing(string input)
    {
        // Settings parses the box on every keystroke, straight from a XAML event handler, so an
        // out-of-range span has to come back as false rather than as an exception.
        Assert.False(KeepAwakeInputParser.TryParse(input, out var request));
        Assert.Null(request);
    }
}
