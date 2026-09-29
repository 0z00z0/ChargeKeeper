using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The rename cascade for threshold presets, over a plain AppSettings rather than SettingsService, so
// only the cross-reference bookkeeping is exercised.
public class PresetCascadeTests
{
    private static AppSettings MakeSettings() => new()
    {
        Presets =
        [
            new ThresholdPreset("Daily", 60, 80),
            new ThresholdPreset("Travel", 80, 100),
        ],
        UnknownNetworkPresetName = "Daily",
        NetworkLocationRules =
        [
            new NetworkLocationRule { Name = "Office", AdapterMac = "AA:BB:CC:DD:EE:FF", PresetName = "Daily" },
            new NetworkLocationRule { Name = "Home",    AdapterMac = "11:22:33:44:55:66", PresetName = "Travel" },
        ],
    };

    [Fact]
    public void Rename_UpdatesOnlyMatchingNetworkLocationRules()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "Daily", "Weekday");

        Assert.Equal("Weekday", s.NetworkLocationRules[0].PresetName); // "Office" referenced Daily
        Assert.Equal("Travel",  s.NetworkLocationRules[1].PresetName); // "Home" referenced Travel — untouched
    }
}
