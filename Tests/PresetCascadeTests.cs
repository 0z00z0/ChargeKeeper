using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// Rename and delete cascades for threshold presets, over a plain AppSettings rather than
// SettingsService, so only the cross-reference bookkeeping is exercised. Which preset is active is
// not one of those references — it derives from the device thresholds — so the cascades are also
// checked against ActivePresetPolicy over a device sitting on Daily's range.
public class PresetCascadeTests
{
    private static readonly ChargeThresholdState OnDailysRange =
        new(Capable: true, Enabled: true, Start: 60, Stop: 80);

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
    public void Rename_LeavesThresholdsAlone_SoTheDerivedActivePresetFollowsTheNewName()
    {
        // Nothing re-points an active-preset field any more: the renamed preset still carries the
        // range the device is running, so it is still the one the policy resolves to.
        var s = MakeSettings();
        PresetCascade.Rename(s, "Daily", "Weekday");
        s.Presets[0].Name = "Weekday";   // the caller renames the preset itself

        Assert.Equal("Weekday", ActivePresetPolicy.Match(s.Presets, OnDailysRange)?.Name);
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

        Assert.Equal("Daily", s.UnknownNetworkPresetName);
        Assert.Equal("Daily", s.NetworkLocationRules[0].PresetName);
    }

    [Fact]
    public void Rename_NameNotReferencedAnywhere_TouchesNothing()
    {
        var s = MakeSettings();
        PresetCascade.Rename(s, "NoSuchPreset", "Renamed");

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
    public void Delete_OfThePresetInUse_LeavesNoMatchUntilTheFallbackIsPushed()
    {
        // Deleting is bookkeeping only — the device keeps running the deleted range, which now
        // belongs to no preset. The Settings caller pushes the fallback's thresholds separately.
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: "Travel");

        Assert.Null(ActivePresetPolicy.Match(s.Presets, OnDailysRange));
        Assert.Contains(s.Presets, p => p.Name == "Travel");
    }

    [Fact]
    public void Delete_WithNoFallback_StillLeavesNoMatch()
    {
        var s = MakeSettings();
        PresetCascade.Delete(s, "Daily", fallbackName: null);
        Assert.Null(ActivePresetPolicy.Match(s.Presets, OnDailysRange));
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
        // PresetEditValidator blocks creating a duplicate name, but settings.json can still arrive
        // with one from a hand edit or a sync conflict, and one Delete click must not destroy both.
        var s = MakeSettings();
        s.Presets.Add(new ThresholdPreset("Daily", 65, 85));
        Assert.Equal(3, s.Presets.Count);

        PresetCascade.Delete(s, "Daily", fallbackName: "Travel");

        Assert.Single(s.Presets, p => p.Name == "Daily");
        Assert.Equal(2, s.Presets.Count);
    }
}
