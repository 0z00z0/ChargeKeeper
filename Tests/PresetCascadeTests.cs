using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure rename/delete cascade tests for threshold presets (TODO #19) — the highest-regression-risk
// piece of the Settings window's preset editor per the issue's own review. Constructs a plain
// AppSettings directly (no SettingsService file I/O involved) so these exercise exactly the
// cross-reference bookkeeping (ActivePreset / NetworkLocationRule.PresetName /
// UnknownNetworkPresetName) in isolation.
public class PresetCascadeTests
{
    private static AppSettings MakeSettings() => new()
    {
        Presets =
        [
            new ThresholdPreset("Daily", 60, 80),
            new ThresholdPreset("Travel", 80, 100),
        ],
        ActivePreset             = "Daily",
        UnknownNetworkPresetName = "Daily",
        NetworkLocationRules =
        [
            new NetworkLocationRule { Name = "Office", AdapterMac = "AA:BB:CC:DD:EE:FF", PresetName = "Daily" },
            new NetworkLocationRule { Name = "Home",    AdapterMac = "11:22:33:44:55:66", PresetName = "Travel" },
        ],
    };

    [Fact]
    public void Rename_UpdatesActivePreset()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "Daily", "Weekday");
        Assert.Equal("Weekday", s.ActivePreset);
    }

    [Fact]
    public void Rename_UpdatesUnknownNetworkPresetName()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "Daily", "Weekday");
        Assert.Equal("Weekday", s.UnknownNetworkPresetName);
    }

    [Fact]
    public void Rename_UpdatesOnlyMatchingNetworkLocationRules()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "Daily", "Weekday");

        Assert.Equal("Weekday", s.NetworkLocationRules[0].PresetName); // "Office" referenced Daily
        Assert.Equal("Travel",  s.NetworkLocationRules[1].PresetName); // "Home" referenced Travel — untouched
    }

    [Fact]
    public void Rename_SameName_IsNoOp()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "Daily", "Daily");

        Assert.Equal("Daily", s.ActivePreset);
        Assert.Equal("Daily", s.UnknownNetworkPresetName);
        Assert.Equal("Daily", s.NetworkLocationRules[0].PresetName);
    }

    [Fact]
    public void Rename_NameNotReferencedAnywhere_TouchesNothing()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "NoSuchPreset", "Renamed");

        Assert.Equal("Daily", s.ActivePreset);
        Assert.Equal("Daily", s.UnknownNetworkPresetName);
        Assert.Equal("Daily",  s.NetworkLocationRules[0].PresetName);
        Assert.Equal("Travel", s.NetworkLocationRules[1].PresetName);
    }

    [Fact]
    public void Delete_RemovesPresetFromList()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: "Travel");
        Assert.DoesNotContain(s.Presets, p => p.Name == "Daily");
        Assert.Single(s.Presets);
    }

    [Fact]
    public void Delete_ReassignsActivePresetToFallback()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: "Travel");
        Assert.Equal("Travel", s.ActivePreset);
    }

    [Fact]
    public void Delete_WithNoFallback_ClearsActivePresetToNull()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: null);
        Assert.Null(s.ActivePreset);
    }

    [Fact]
    public void Delete_WithNoFallback_ClearsUnknownNetworkPresetNameToNull()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: null);
        Assert.Null(s.UnknownNetworkPresetName);
    }

    [Fact]
    public void Delete_ReassignsReferencingNetworkRuleToFallback()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: "Travel");
        Assert.Equal("Travel", s.NetworkLocationRules[0].PresetName); // "Office" referenced Daily
    }

    [Fact]
    public void Delete_WithNoFallback_ClearsReferencingNetworkRuleToEmptyString()
    {
        // NetworkLocationRule.PresetName is non-nullable; an empty string is the "matches nothing"
        // state (mirrors a rule with no MAC/CIDR match key at all — see NetworkLocationRuleTests).
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: null);
        Assert.Equal("", s.NetworkLocationRules[0].PresetName);
    }

    [Fact]
    public void Delete_DoesNotTouchRulesReferencingADifferentPreset()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: null);
        Assert.Equal("Travel", s.NetworkLocationRules[1].PresetName); // "Home" referenced Travel
    }

    [Fact]
    public void Delete_DuplicateName_RemovesOnlyOneInstance()
    {
        // PresetEditValidator blocks creating a duplicate name going forward, but settings.json
        // can still arrive with one (a hand edit, or a sync/merge conflict) — a single "Delete
        // preset" click must not destroy both.
        var s = MakeSettings();
        s.Presets.Add(new ThresholdPreset("Daily", 65, 85));
        Assert.Equal(3, s.Presets.Count);

        PresetCascade.Delete(s, "Daily", fallbackName: "Travel");

        Assert.Single(s.Presets, p => p.Name == "Daily");
        Assert.Equal(2, s.Presets.Count);
    }
}
