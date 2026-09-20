using System.Collections.Generic;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The brightness is the display's own setting, and the application changes it. A level not put back
/// leaves somebody with a screen they cannot read and nothing on it saying why. Every case runs
/// against a fake display: no test writes the real one.
/// </summary>
public class ScreenBrightnessParkTests
{
    private sealed class FakeDisplay(int level) : IScreenBrightnessSetting
    {
        public int Level { get; set; } = level;
        public bool Supported { get; set; } = true;
        public bool FailWrites { get; set; }
        public List<int> Writes { get; } = [];
        public List<string>? Order { get; set; }

        public bool CanSet() => Supported;

        public int? Read() => Supported ? Level : null;

        public bool Write(int percent)
        {
            Order?.Add("write");
            if (FailWrites) return false;
            Writes.Add(percent);
            Level = percent;
            return true;
        }
    }

    private sealed class FakeRecord : IScreenBrightnessRecord
    {
        public int? Held { get; set; }
        public bool FailSaves { get; set; }
        public List<string>? Order { get; set; }

        public int? Read() => Held;

        public bool Save(int percent)
        {
            Order?.Add("save");
            if (FailSaves) return false;
            Held = percent;
            return true;
        }

        public void Clear() => Held = null;
    }

    private static ScreenBrightnessPark Park(FakeDisplay display, FakeRecord record,
                                             List<string>? lines = null) =>
        new(display, record, (what, cause) => lines?.Add($"{what} — cause: {cause}"));

    /// <summary>The guard the whole feature rides on: whatever the screen was on before the first
    /// change is exactly what comes back, however many changes happened in between.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(62)]
    [InlineData(1)]
    public void TheLevelTakenIsTheLevelPutBack(int original)
    {
        var display = new FakeDisplay(original);
        var record  = new FakeRecord();
        var park    = Park(display, record);

        Assert.True(park.Set(0, "a test"));
        Assert.True(park.Set(20, "a test"));
        Assert.True(park.Set(5, "a test"));

        Assert.True(park.Restore("a test"));

        Assert.Equal(original, display.Level);
        Assert.Null(record.Held);
        Assert.False(park.Holding);
    }

    [Fact]
    public void TheRecordReachesDiskBeforeTheDisplayChanges()
    {
        // A crash between the two is the case this ordering exists for: a record written afterwards
        // is a record that does not exist when it is needed.
        var order   = new List<string>();
        var display = new FakeDisplay(80) { Order = order };
        var record  = new FakeRecord { Order = order };

        Park(display, record).Set(10, "a test");

        Assert.Equal(["save", "write"], order);
    }

    [Fact]
    public void ASecondChangeDoesNotOverwriteTheLevelTheFirstOneDisplaced()
    {
        var display = new FakeDisplay(90);
        var record  = new FakeRecord();
        var park    = Park(display, record);

        park.Set(30, "a test");
        park.Set(10, "a test");

        Assert.Equal(90, record.Held);
    }

    [Fact]
    public void ARecordLeftByARunThatEnded_IsPutBackWithoutOneOfItsOwn()
    {
        // What the next start finds: the display carries the dimmed level and the record says what
        // it was. Nothing in this process ever set it.
        var display = new FakeDisplay(0);
        var record  = new FakeRecord { Held = 75 };

        Assert.True(Park(display, record).Restore("starting up"));

        Assert.Equal(75, display.Level);
        Assert.Null(record.Held);
    }

    [Fact]
    public void ALevelThatCouldNotBeSaved_LeavesTheDisplayAlone()
    {
        var display = new FakeDisplay(70);
        var record  = new FakeRecord { FailSaves = true };

        Assert.False(Park(display, record).Set(10, "a test"));

        Assert.Empty(display.Writes);
        Assert.Equal(70, display.Level);
    }

    [Fact]
    public void AFailedRestore_KeepsTheRecordForTheNextStart()
    {
        var display = new FakeDisplay(40);
        var record  = new FakeRecord { Held = 85 };

        display.FailWrites = true;
        Assert.False(Park(display, record).Restore("a test"));

        Assert.Equal(85, record.Held);
    }

    [Fact]
    public void ADisplayThatAcceptsNothing_IsSaidRatherThanSilentlyIgnored()
    {
        var lines   = new List<string>();
        var display = new FakeDisplay(50) { Supported = false };
        var record  = new FakeRecord();

        Assert.False(Park(display, record, lines).Set(10, "a test"));

        Assert.Null(record.Held);
        Assert.Contains(lines, line => line.Contains("no display", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingTheLevelAlreadyInForce_RecordsNothing()
    {
        // Nothing was displaced, so nothing is owed back — and a record written here would put the
        // same level back later and read as a restore that did something.
        var display = new FakeDisplay(65);
        var record  = new FakeRecord();

        Assert.True(Park(display, record).Set(65, "a test"));

        Assert.Null(record.Held);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(140, 100)]
    public void ALevelOutsideTheRange_IsHeldAtTheBound(int asked, int written)
    {
        var display = new FakeDisplay(50);

        Park(display, new FakeRecord()).Set(asked, "a test");

        Assert.Equal([written], display.Writes);
    }
}
