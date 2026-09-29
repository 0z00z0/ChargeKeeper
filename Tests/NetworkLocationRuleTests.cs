using System.Net.NetworkInformation;
using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class NetworkLocationRuleTests
{
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
    public void SettingsFileWithoutTheMarker_DeserialisesAsNull()
    {
        // The whole premise: System.Text.Json leaves an absent key at the property's default, so the
        // default has to be the harmless one.
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "NetworkProfilesEnabled": true }""");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.NetworkRulesKeyedOnPhysicalAdapter);
    }
}
