using System.Net.NetworkInformation;
using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class NetworkLocationRuleTests
{
    private static readonly NetworkLocation OfficeDock =
        new(AdapterMac: "AA:BB:CC:DD:EE:FF", IpCidr: "10.0.1.0/24", IsWired: true, DisplayHint: "Docking Station Ethernet");

    [Fact]
    public void Matches_BothKeysMatch_True()
    {
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "10.0.1.0/24" };
        Assert.True(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_MacDiffers_False()
    {
        var rule = new NetworkLocationRule { AdapterMac = "11:22:33:44:55:66", IpCidr = "10.0.1.0/24" };
        Assert.False(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_CidrDiffers_False()
    {
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "192.168.1.0/24" };
        Assert.False(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_OnlyMacSet_IgnoresCidr()
    {
        // A rule can key on just one dimension — e.g. a laptop that always gets a different DHCP
        // lease at the same physical dock should still match on MAC alone.
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = null };
        Assert.True(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_OnlyCidrSet_IgnoresMac()
    {
        var rule = new NetworkLocationRule { AdapterMac = null, IpCidr = "10.0.1.0/24" };
        Assert.True(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_NeitherKeySet_NeverMatches()
    {
        // A rule with no match key at all must not become an accidental catch-all — that's what
        // UnknownNetworkPresetName is for, as an explicit separate setting, not an empty rule.
        var rule = new NetworkLocationRule { AdapterMac = null, IpCidr = null };
        Assert.False(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_EmptyLocation_NeverMatchesAnyRealRule()
    {
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF" };
        Assert.False(rule.Matches(default));
    }

    [Fact]
    public void NetworkLocation_IsEmpty_TrueOnlyWhenBothKeysNull()
    {
        Assert.True(default(NetworkLocation).IsEmpty);
        Assert.False(new NetworkLocation("AA:BB:CC:DD:EE:FF", null, true, null).IsEmpty);
        Assert.False(new NetworkLocation(null, "10.0.1.0/24", true, null).IsEmpty);
    }

    // Mobile: the carrier lease rotates, so the modem's MAC is the whole key

    private const string ModemMac = "84:8D:B0:53:5F:55";

    // A detected mobile location carries no subnet at all, whatever the carrier leased this time.
    private static readonly NetworkLocation OnMobile =
        new(AdapterMac: ModemMac, IpCidr: null, IsWired: true, DisplayHint: "Mobile", IsMobile: true);

    [Fact]
    public void Matches_StoredMobileRuleCarryingASubnet_StillMatches()
    {
        // Written before the subnet left the mobile key, and there is no migration: the stored value
        // is ignored rather than rewritten.
        var rule = new NetworkLocationRule { AdapterMac = ModemMac, IpCidr = "100.110.83.0/24" };

        Assert.True(rule.Matches(OnMobile));
    }

    [Fact]
    public void Matches_MobileRuleAgainstADifferentModem_False()
    {
        // The MAC is the whole key on mobile, so it has to carry the whole rejection too.
        var rule = new NetworkLocationRule { AdapterMac = ModemMac, IpCidr = "100.110.83.0/24" };

        Assert.False(rule.Matches(OnMobile with { AdapterMac = "00:11:22:33:44:55" }));
    }

    [Fact]
    public void Matches_SubnetOnlyRule_NeverBecomesACatchAllOnMobile()
    {
        // With no MAC there is nothing left to match on, so ignoring the subnet as well would fit
        // every mobile network.
        var rule = new NetworkLocationRule { AdapterMac = null, IpCidr = "10.0.1.0/24" };

        Assert.False(rule.Matches(OnMobile));
    }

    [Fact]
    public void Matches_NewMobileRule_StoresNoSubnet()
    {
        // Composed the way the Settings page builds one: straight from the detected key.
        var rule = new NetworkLocationRule { AdapterMac = OnMobile.AdapterMac, IpCidr = OnMobile.IpCidr };

        Assert.Null(rule.IpCidr);
        Assert.True(rule.Matches(OnMobile));
    }

    [Fact]
    public void Matches_WiredRuleOnItsOwnSubnet_IsUnaffected()
    {
        // The mirror of the mobile bug: a wired or Wi-Fi rule still needs both halves, or the home
        // and the cabin behind one radio collapse into one place.
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "10.0.1.0/24" };

        Assert.True(rule.Matches(OfficeDock));
        Assert.False(rule.Matches(OfficeDock with { IpCidr = "10.0.20.0/24" }));
    }

    [Fact]
    public void SubnetIgnoredOn_OnlyTheAdapterWeAreOn_AndOnlyOnMobile()
    {
        // What the Settings "Matches" line reads, so the shown key says what is actually compared.
        var mobileRule = new NetworkLocationRule { AdapterMac = ModemMac, IpCidr = "100.110.83.0/24" };
        var wifiRule   = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "10.0.1.0/24" };

        Assert.True(mobileRule.SubnetIgnoredOn(OnMobile));
        Assert.False(wifiRule.SubnetIgnoredOn(OnMobile));
        Assert.False(mobileRule.SubnetIgnoredOn(OfficeDock));
    }

    // Keep awake here

    [Fact]
    public void KeepAwakeHere_DefaultsToFalse_SoOldRulesAreUnaffected()
    {
        Assert.False(new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF" }.KeepAwakeHere);
    }

    [Fact]
    public void KeepAwakeHere_ComesFromTheFirstMatchingRule_NotAnyMatchingRule()
    {
        // The keep-awake reaction reads FindNetworkRule, the same first-match-wins lookup the preset
        // auto-apply uses, so a later rule that also matches must not turn keep-awake on.
        var settings = new AppSettings
        {
            NetworkLocationRules =
            [
                new() { Name = "Office dock", AdapterMac = "AA:BB:CC:DD:EE:FF", KeepAwakeHere = false },
                new() { Name = "Office LAN",  IpCidr     = "10.0.1.0/24",       KeepAwakeHere = true  },
            ],
        };

        var rule = settings.FindNetworkRule(OfficeDock);
        Assert.Equal("Office dock", rule?.Name);
        Assert.False(rule?.KeepAwakeHere);
    }

    [Fact]
    public void KeepAwakeHere_FirstMatchWins_EvenWhenAnEarlierRuleDoesNotMatch()
    {
        var settings = new AppSettings
        {
            NetworkLocationRules =
            [
                new() { Name = "Home",  IpCidr = "192.168.1.0/24", KeepAwakeHere = false },
                new() { Name = "Cabin", IpCidr = "10.0.1.0/24",    KeepAwakeHere = true  },
            ],
        };

        var rule = settings.FindNetworkRule(OfficeDock);
        Assert.Equal("Cabin", rule?.Name);
        Assert.True(rule?.KeepAwakeHere);
    }

    [Fact]
    public void KeepAwakeHere_NoMatchingRule_LeavesNothingToActOn()
    {
        var settings = new AppSettings
        {
            NetworkLocationRules = [new() { Name = "Home", IpCidr = "192.168.1.0/24", KeepAwakeHere = true }],
        };
        Assert.Null(settings.FindNetworkRule(OfficeDock));
    }

    // The one-time removal of rules written before locations were keyed on the physical adapter

    private const string SwitchMac = "00:15:5D:EA:DC:CF";   // Hyper-V external switch port
    private const string NicMac    = "30:89:4A:68:1C:3A";   // the card behind it

    private static readonly List<BridgePeer> Adapters =
    [
        new("vEthernet (External)", SwitchMac, IsVirtual: true,  OperationalStatus.Up),
        new("Wi-Fi",                NicMac,    IsVirtual: false, OperationalStatus.Up),
    ];

    /// <summary>One rule keyed on the switch port, one on the card — only the first is identifiable
    /// as written against the routed adapter.</summary>
    private static AppSettings SettingsAwaitingMigration() => new()
    {
        NetworkProfilesEnabled              = true,
        UnknownNetworkPresetName            = "Daily",
        NetworkRulesKeyedOnPhysicalAdapter  = false,
        NetworkLocationRules =
        [
            new() { Name = "Office", AdapterMac = SwitchMac, IpCidr = "172.24.64.0/20", PresetName = "Daily" },
            new() { Name = "Cabin",  AdapterMac = NicMac,    IpCidr = "10.0.20.0/24",   PresetName = "Travel" },
        ],
    };

    [Fact]
    public void ClearRoutedAdapterRules_MarkerAbsent_RemovesNothing()
    {
        // The defect this replaced: a settings.json written before the key existed read as "not yet
        // migrated" and lost every rule. Absent configuration means nothing to do.
        var s = SettingsAwaitingMigration();
        s.NetworkRulesKeyedOnPhysicalAdapter = null;

        Assert.Null(SettingsService.ClearRoutedAdapterRules(s, Adapters));
        Assert.Equal(2, s.NetworkLocationRules.Count);
        Assert.Null(s.NetworkRulesKeyedOnPhysicalAdapter);
    }

    [Fact]
    public void ClearRoutedAdapterRules_RemovesOnlyTheVirtualAdapterRules()
    {
        var s = SettingsAwaitingMigration();

        Assert.Equal(1, SettingsService.ClearRoutedAdapterRules(s, Adapters));
        Assert.Single(s.NetworkLocationRules);
        Assert.Equal("Cabin", s.NetworkLocationRules[0].Name);
        Assert.True(s.NetworkRulesKeyedOnPhysicalAdapter);
    }

    [Fact]
    public void ClearRoutedAdapterRules_NothingIdentifiable_StillMarksItDone()
    {
        var s = SettingsAwaitingMigration();
        s.NetworkLocationRules.RemoveAll(r => r.AdapterMac == SwitchMac);

        Assert.Equal(0, SettingsService.ClearRoutedAdapterRules(s, Adapters));
        Assert.Single(s.NetworkLocationRules);
        Assert.True(s.NetworkRulesKeyedOnPhysicalAdapter);
    }

    [Fact]
    public void ClearRoutedAdapterRules_MarkerTrue_RemovesNothing()
    {
        var s = SettingsAwaitingMigration();
        s.NetworkRulesKeyedOnPhysicalAdapter = true;

        Assert.Null(SettingsService.ClearRoutedAdapterRules(s, Adapters));
        Assert.Equal(2, s.NetworkLocationRules.Count);
    }

    [Fact]
    public void ClearRoutedAdapterRules_SecondCall_DoesNothing()
    {
        // The marker is the whole guard: running on every start would take the rules saved since.
        var s = SettingsAwaitingMigration();
        SettingsService.ClearRoutedAdapterRules(s, Adapters);
        s.NetworkLocationRules.Add(new() { Name = "Dock", AdapterMac = SwitchMac, IpCidr = "10.0.1.0/24" });

        Assert.Null(SettingsService.ClearRoutedAdapterRules(s, Adapters));
        Assert.Equal(2, s.NetworkLocationRules.Count);
        Assert.Equal("Dock", s.NetworkLocationRules[1].Name);
    }

    [Fact]
    public void ClearRoutedAdapterRules_TouchesNothingElse()
    {
        var s = SettingsAwaitingMigration();
        SettingsService.ClearRoutedAdapterRules(s, Adapters);

        Assert.True(s.NetworkProfilesEnabled);
        Assert.Equal("Daily", s.UnknownNetworkPresetName);
        Assert.Equal(2, s.Presets.Count);
    }

    [Fact]
    public void FindRoutedAdapterRules_UnknownMac_IsNotIdentifiable()
    {
        // A MAC no adapter on this machine owns proves nothing either way, so the rule stays.
        var s = SettingsAwaitingMigration();
        s.NetworkLocationRules[0].AdapterMac = "AA:BB:CC:DD:EE:FF";

        Assert.Empty(SettingsService.FindRoutedAdapterRules(s, Adapters));
    }

    [Fact]
    public void SettingsFileWithoutTheMarker_DeserialisesAsNull()
    {
        // The whole premise: System.Text.Json leaves an absent key at the property's default, so the
        // default has to be the harmless one.
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "NetworkProfilesEnabled": true }""");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.NetworkRulesKeyedOnPhysicalAdapter);
    }
}
