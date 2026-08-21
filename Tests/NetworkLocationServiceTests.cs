using System.Net;
using System.Net.NetworkInformation;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class NetworkLocationServiceTests
{
    [Theory]
    [InlineData("192.168.1.137", "255.255.255.0", "192.168.1.0/24")]
    [InlineData("10.0.5.42", "255.0.0.0", "10.0.0.0/8")]
    [InlineData("172.16.200.10", "255.255.0.0", "172.16.0.0/16")]
    [InlineData("192.168.1.137", "255.255.255.128", "192.168.1.128/25")]
    public void CalculateCidr_MasksNetworkAddressAndCountsPrefix(string ip, string mask, string expected)
    {
        var result = NetworkLocationService.CalculateCidr(IPAddress.Parse(ip), IPAddress.Parse(mask));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeMac_TwelveHexChars_InsertsColonsUppercase()
    {
        Assert.Equal("AA:BB:CC:DD:EE:FF", NetworkLocationService.NormalizeMac("aabbccddeeff"));
    }

    [Fact]
    public void NormalizeMac_NonTwelveChars_ReturnedUnchanged()
    {
        // Defensive: an unexpected format (already-formatted, empty, odd length) is passed through
        // rather than mangled.
        Assert.Equal("", NetworkLocationService.NormalizeMac(""));
        Assert.Equal("AA:BB:CC:DD:EE:FF", NetworkLocationService.NormalizeMac("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void SameLocationAs_IdenticalKeys_DifferentDisplayHint_True()
    {
        // The whole point of SameLocationAs vs record Equals: a flapping SSID/name hint must not
        // read as a location change.
        var a = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", IsWired: false, DisplayHint: "OfficeWiFi");
        var b = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", IsWired: false, DisplayHint: null);
        Assert.True(a.SameLocationAs(b));
    }

    [Fact]
    public void SameLocationAs_DifferentMac_False()
    {
        var a = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", true, null);
        var b = new NetworkLocation("11:22:33:44:55:66", "10.0.1.0/24", true, null);
        Assert.False(a.SameLocationAs(b));
    }

    [Fact]
    public void SameLocationAs_DifferentCidr_False()
    {
        var a = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", true, null);
        var b = new NetworkLocation("AA:BB:CC:DD:EE:FF", "192.168.0.0/24", true, null);
        Assert.False(a.SameLocationAs(b));
    }

    // ── The first-evaluation seed rule ─────────────────────────────────────────────

    [Fact]
    public void IsLocationChange_FirstEvaluation_SeedsWithoutBeingAChange()
    {
        // Start() evaluates immediately; _last is still default there. Raising LocationChanged on that
        // baseline re-applied a network profile on every app start, cancelling a persisted travel
        // override. The first reading only seeds.
        var current = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", true, null);
        Assert.False(NetworkLocationService.IsLocationChange(seeded: false, current, default));
    }

    [Fact]
    public void IsLocationChange_AfterSeeding_DifferentLocation_IsAChange()
    {
        var seed  = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", true, null);
        var moved = new NetworkLocation("11:22:33:44:55:66", "192.168.0.0/24", false, "CafeWiFi");
        Assert.True(NetworkLocationService.IsLocationChange(seeded: true, moved, seed));
    }

    [Fact]
    public void IsLocationChange_AfterSeeding_SameLocation_IsNotAChange()
    {
        // NetworkChange fires far more often than the resolved location moves.
        var seed = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", false, "HomeWiFi");
        var same = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.1.0/24", false, null);   // hint flapped
        Assert.False(NetworkLocationService.IsLocationChange(seeded: true, same, seed));
    }

    // ── SelectPrimary: the primary-adapter heuristic (issue #21), exercised without live adapters ──

    [Fact]
    public void SelectPrimary_BridgedHyperV_RoutingTableWinsOverPhysicalVirtualBias()
    {
        // Regression for #21: on a Hyper-V external switch the routable IP + default route live on a
        // "vEthernet (…)" Hyper-V Virtual Ethernet Adapter (IsVirtual), while the bridged physical NIC
        // keeps no usable IP. GetBestInterface points at the vEthernet's index, so it MUST be selected
        // even though a physical, non-virtual Ethernet is also present and listed first (i.e. the
        // routing table beats the "prefer physical / demote Virtual" bias).
        var physical  = new AdapterCandidate(IPv4Index: 7,  IsVirtual: false, Type: NetworkInterfaceType.Ethernet);
        var vEthernet = new AdapterCandidate(IPv4Index: 12, IsVirtual: true,  Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary([physical, vEthernet], bestIndex: 12);

        Assert.Same(vEthernet, result);
    }

    [Fact]
    public void SelectPrimary_PlainWired_SelectsThatEthernet()
    {
        var ethernet = new AdapterCandidate(IPv4Index: 5, IsVirtual: false, Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary([ethernet], bestIndex: 5);

        Assert.Same(ethernet, result);
    }

    [Fact]
    public void SelectPrimary_NoBestInterface_PrefersPhysicalEthernetOverWireless()
    {
        // GetBestInterface unavailable (returns 0) → fall back to the preference order. The wired NIC
        // wins over the simultaneously-active wireless one regardless of list order.
        var wifi     = new AdapterCandidate(IPv4Index: 9, IsVirtual: false, Type: NetworkInterfaceType.Wireless80211);
        var ethernet = new AdapterCandidate(IPv4Index: 3, IsVirtual: false, Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary([wifi, ethernet], bestIndex: 0);

        Assert.Same(ethernet, result);
    }

    [Fact]
    public void SelectPrimary_NoBestInterface_OnlyVirtualCandidate_StillReturnsIt()
    {
        // Even with GetBestInterface unavailable and the sole candidate a virtual Ethernet (e.g. the
        // Hyper-V bridge with no physical peer Up), a usable adapter must never be discarded as null.
        var vEthernet = new AdapterCandidate(IPv4Index: 4, IsVirtual: true, Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary([vEthernet], bestIndex: 0);

        Assert.Same(vEthernet, result);
    }

    [Fact]
    public void SelectPrimary_NoCandidates_ReturnsNull()
    {
        // Genuinely offline (no adapter has a usable IPv4) → null, which DetectCurrent maps to the
        // empty "No network detected" location.
        Assert.Null(NetworkLocationService.SelectPrimary([], bestIndex: 0));
    }


    // ── Suggested NAME behind a Hyper-V external switch ──────────────────────────────
    // Fixtures are the measured adapter set of the affected host: the physical "Ethernet" is Up with
    // NO IPv4 (the external switch took it) and shares its MAC verbatim with "vEthernet (Bridged)",
    // which holds 10.0.1.117/23 and the default route; "vEthernet (Default Switch)" is an INTERNAL
    // switch with a synthesised Microsoft-OUI address and no physical partner.

    private const string BridgedMac      = "48:65:EE:18:86:EF";
    private const string DefaultSwitchMac = "00:15:5D:EA:DC:CF";

    private static BridgePeer Physical(string name = "Ethernet", string mac = BridgedMac,
                                       OperationalStatus status = OperationalStatus.Up) =>
        new(name, mac, IsVirtual: false, status);

    private static readonly BridgePeer[] MeasuredPeers =
    [
        Physical(),                                                                       // Realtek USB GbE, no IPv4
        new("vEthernet (Bridged)",        BridgedMac,          IsVirtual: true,  OperationalStatus.Up),
        new("vEthernet (Default Switch)", DefaultSwitchMac,    IsVirtual: true,  OperationalStatus.Up),
        new("WiFi",                       "AA:BB:CC:11:22:33", IsVirtual: false, OperationalStatus.Down),
        new("Ethernet 2",                 "AA:BB:CC:44:55:66", IsVirtual: false, OperationalStatus.Down),
    ];

    [Theory]
    [InlineData("vEthernet (Bridged)",        "Hyper-V Virtual Ethernet Adapter #2")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("Some Alias",                 "Hyper-V Virtual Ethernet Adapter")]   // description alone is enough
    [InlineData("vEthernet (Bridged)",        "Renamed Adapter")]                    // alias alone is enough
    public void LooksLikeHyperVSwitchPort_RecognisesSwitchPorts(string name, string description)
    {
        Assert.True(NetworkLocationService.LooksLikeHyperVSwitchPort(name, description));
    }

    [Theory]
    [InlineData("Ethernet",   "Realtek USB GbE Family Controller")]
    [InlineData("WiFi",       "Intel(R) Wi-Fi 6E AX211 160MHz")]
    [InlineData("Ethernet 2", "PANGP Virtual Ethernet Adapter Secure")]   // a VPN adapter is not a switch port
    public void LooksLikeHyperVSwitchPort_RejectsEverythingElse(string name, string description)
    {
        Assert.False(NetworkLocationService.LooksLikeHyperVSwitchPort(name, description));
    }

    [Fact]
    public void ResolveBridgedAdapterName_ExternalSwitch_PairsByMacToThePhysicalNic()
    {
        // The external switch's vNIC inherits the bound NIC's hardware address verbatim (measured),
        // so the same MAC on a non-virtual adapter identifies the NIC driving the switch.
        Assert.Equal("Ethernet", NetworkLocationService.ResolveBridgedAdapterName(BridgedMac, MeasuredPeers));
    }

    [Fact]
    public void ResolveBridgedAdapterName_InternalDefaultSwitch_ResolvesNothing()
    {
        // "vEthernet (Default Switch)" is internal: its Microsoft-OUI address belongs to no physical
        // NIC, so there is nothing to pair with and the caller keeps the alias.
        Assert.Null(NetworkLocationService.ResolveBridgedAdapterName(DefaultSwitchMac, MeasuredPeers));
    }

    [Fact]
    public void ResolveBridgedAdapterName_NoMac_ResolvesNothing()
    {
        Assert.Null(NetworkLocationService.ResolveBridgedAdapterName(null, MeasuredPeers));
        Assert.Null(NetworkLocationService.ResolveBridgedAdapterName("", MeasuredPeers));
    }

    [Fact]
    public void ResolveBridgedAdapterName_OnlyVirtualAdaptersShareTheMac_ResolvesNothing()
    {
        // One vNIC must never name another — e.g. a second switch port stacked on the same address.
        BridgePeer[] peers =
        [
            new("vEthernet (Bridged)", BridgedMac, IsVirtual: true, OperationalStatus.Up),
            new("vEthernet (Other)",   BridgedMac, IsVirtual: true, OperationalStatus.Up),
        ];
        Assert.Null(NetworkLocationService.ResolveBridgedAdapterName(BridgedMac, peers));
    }

    [Fact]
    public void ResolveBridgedAdapterName_AbsentNamesake_LosesToThePresentAdapter()
    {
        BridgePeer[] peers = [Physical("Ethernet 3", status: OperationalStatus.NotPresent), Physical()];
        Assert.Equal("Ethernet", NetworkLocationService.ResolveBridgedAdapterName(BridgedMac, peers));
    }

    [Fact]
    public void ResolveBridgedAdapterName_TwoPresentAdaptersShareTheMac_ResolvesNothing()
    {
        // Genuinely ambiguous (teaming/filter oddity): a wrong name is worse than the raw alias.
        BridgePeer[] peers = [Physical("Ethernet"), Physical("Ethernet 4")];
        Assert.Null(NetworkLocationService.ResolveBridgedAdapterName(BridgedMac, peers));
    }

    [Fact]
    public void SuggestDisplayHint_SwitchPortWithPhysicalPair_SuggestsThePhysicalNic()
    {
        var hint = NetworkLocationService.SuggestDisplayHint(
            wired: true, alias: "vEthernet (Bridged)", description: "Hyper-V Virtual Ethernet Adapter #2",
            mac: BridgedMac, peers: MeasuredPeers, ssid: null);

        Assert.Equal("Ethernet", hint);
    }

    [Fact]
    public void SuggestDisplayHint_SwitchPortWithoutPhysicalPair_FallsBackToTheAlias()
    {
        var hint = NetworkLocationService.SuggestDisplayHint(
            wired: true, alias: "vEthernet (Default Switch)", description: "Hyper-V Virtual Ethernet Adapter",
            mac: DefaultSwitchMac, peers: MeasuredPeers, ssid: null);

        Assert.Equal("vEthernet (Default Switch)", hint);
    }

    [Fact]
    public void SuggestDisplayHint_PlainPhysicalAdapter_IsUnaffected()
    {
        // No pairing is attempted for a non-switch-port adapter, even with same-MAC peers present.
        var hint = NetworkLocationService.SuggestDisplayHint(
            wired: true, alias: "Ethernet", description: "Realtek USB GbE Family Controller",
            mac: BridgedMac, peers: MeasuredPeers, ssid: null);

        Assert.Equal("Ethernet", hint);
    }

    [Fact]
    public void SuggestDisplayHint_Wireless_StillSuggestsTheSsid()
    {
        var hint = NetworkLocationService.SuggestDisplayHint(
            wired: false, alias: "WiFi", description: "Intel(R) Wi-Fi 6E AX211 160MHz",
            mac: "AA:BB:CC:11:22:33", peers: MeasuredPeers, ssid: "HomeWiFi");

        Assert.Equal("HomeWiFi", hint);
    }

    [Fact]
    public void SuggestDisplayHint_WirelessWithoutSsid_StaysNull()
    {
        // TryGetWifiSsid is best-effort; a null hint makes the caller fall back to "Wireless network".
        Assert.Null(NetworkLocationService.SuggestDisplayHint(
            wired: false, alias: "WiFi", description: "Intel(R) Wi-Fi 6E AX211 160MHz",
            mac: null, peers: [], ssid: null));
    }

    // ── The contract that must not move: the suggestion never touches the match key ──

    [Fact]
    public void Compose_BridgedSuggestion_KeepsTheSelectedAdaptersMatchKey()
    {
        // Same selected (vEthernet) adapter, composed with each of the two possible suggestions. The
        // match key and IsWired are identical; ONLY the display hint differs.
        var withAlias    = NetworkLocationService.Compose(BridgedMac, "10.0.0.0/23", wired: true, "vEthernet (Bridged)");
        var withPhysical = NetworkLocationService.Compose(BridgedMac, "10.0.0.0/23", wired: true, "Ethernet");

        Assert.Equal(BridgedMac,    withPhysical.AdapterMac);
        Assert.Equal("10.0.0.0/23", withPhysical.IpCidr);
        Assert.True(withPhysical.IsWired);
        Assert.True(withAlias.SameLocationAs(withPhysical));
        Assert.Equal("Ethernet", withPhysical.DisplayHint);
    }

    [Fact]
    public void Compose_EmptyMac_BecomesNullSoTheKeyIsNeverAnEmptyString()
    {
        var location = NetworkLocationService.Compose("", "10.0.0.0/23", wired: true, "Ethernet");
        Assert.Null(location.AdapterMac);
    }

    // ── The match key shown in the naming dialog and the Settings rows ─────────────

    [Fact]
    public void DescribeMatchKey_MacAndSubnet_JoinedWithTheHouseSeparator()
    {
        Assert.Equal($"MAC {BridgedMac} · Subnet 10.0.0.0/23",
            NetworkLocationService.DescribeMatchKey(BridgedMac, "10.0.0.0/23"));
    }

    [Fact]
    public void DescribeMatchKey_SingleFacet_ShowsOnlyThatFacet()
    {
        Assert.Equal($"MAC {BridgedMac}",   NetworkLocationService.DescribeMatchKey(BridgedMac, null));
        Assert.Equal("Subnet 10.0.0.0/23", NetworkLocationService.DescribeMatchKey(null, "10.0.0.0/23"));
    }

    [Fact]
    public void DescribeMatchKey_NoFacets_SaysTheProfileCanNeverApply()
    {
        Assert.Contains("never apply", NetworkLocationService.DescribeMatchKey(null, null));
    }
}
