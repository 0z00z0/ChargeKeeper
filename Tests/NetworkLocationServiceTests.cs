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


    // ── The Hyper-V bridge walk-back ─────────────────────────────────────
    // Fixtures are the measured adapter set of the affected host: the physical "Ethernet" is Up with
    // NO IPv4 (the external switch took it) and shares its MAC verbatim with "vEthernet (Bridged)",
    // which holds the routable address and the default route; "vEthernet (Default Switch)" is an
    // INTERNAL switch with a synthesised Microsoft-OUI address and no physical partner.

    private const string PhysicalMac      = "48:65:EE:18:86:EF";
    private const string DefaultSwitchMac = "00:15:5D:EA:DC:CF";
    private const string BridgeAlias      = "vEthernet (Bridged)";
    private const string BridgeDesc       = "Hyper-V Virtual Ethernet Adapter #2";

    private static BridgePeer Physical(string name = "Ethernet", string mac = PhysicalMac,
                                       OperationalStatus status = OperationalStatus.Up) =>
        new(name, mac, IsVirtual: false, status);

    private static readonly BridgePeer[] MeasuredPeers =
    [
        Physical(),                                                                       // Realtek USB GbE, no IPv4
        new(BridgeAlias,                  PhysicalMac,         IsVirtual: true,  OperationalStatus.Up),
        new("vEthernet (Default Switch)", DefaultSwitchMac,    IsVirtual: true,  OperationalStatus.Up),
        new("WiFi",                       "AA:BB:CC:11:22:33", IsVirtual: false, OperationalStatus.Down),
        new("Ethernet 2",                 "AA:BB:CC:44:55:66", IsVirtual: false, OperationalStatus.Down),
        new("Local Area Connection* 1",   null,                IsVirtual: false, OperationalStatus.Down),   // WAN miniport, no MAC
    ];

    [Theory]
    [InlineData(BridgeAlias,                  BridgeDesc)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("Some Alias",                 "Hyper-V Virtual Ethernet Adapter")]   // description alone is enough
    [InlineData(BridgeAlias,                  "Renamed Adapter")]                    // alias alone is enough
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
    public void ResolveBridgedPeer_ExternalSwitch_PairsByMacToThePhysicalNic()
    {
        // The external switch's vNIC inherits the bound NIC's hardware address verbatim (measured),
        // so the same MAC on a non-virtual adapter identifies the NIC driving the switch.
        var peer = NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, MeasuredPeers);

        Assert.NotNull(peer);
        Assert.Equal("Ethernet", peer.Name);
        Assert.Equal(PhysicalMac, peer.Mac);
    }

    [Fact]
    public void ResolveBridgedPeer_InternalDefaultSwitch_ResolvesNothing()
    {
        // "vEthernet (Default Switch)" is internal: its Microsoft-OUI address belongs to no physical
        // NIC, so there is nothing to pair with and the vNIC's own MAC is stored.
        Assert.Null(NetworkLocationService.ResolveBridgedPeer(
            "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", DefaultSwitchMac, MeasuredPeers));
    }

    [Fact]
    public void ResolveBridgedPeer_PlainPhysicalAdapter_IsNeverPaired()
    {
        // The gate, not the pairing: a non-switch-port adapter must not be re-keyed onto a same-MAC
        // peer (its own WFP filter adapter would otherwise be a candidate).
        Assert.Null(NetworkLocationService.ResolveBridgedPeer(
            "Ethernet", "Realtek USB GbE Family Controller", PhysicalMac, MeasuredPeers));
    }

    [Fact]
    public void ResolveBridgedPeer_NoMac_ResolvesNothing()
    {
        Assert.Null(NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, null, MeasuredPeers));
        Assert.Null(NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, "", MeasuredPeers));
    }

    [Fact]
    public void ResolveBridgedPeer_OnlyVirtualAdaptersShareTheMac_ResolvesNothing()
    {
        // One vNIC must never stand in for another — e.g. a second switch port on the same address.
        BridgePeer[] peers =
        [
            new(BridgeAlias,         PhysicalMac, IsVirtual: true, OperationalStatus.Up),
            new("vEthernet (Other)", PhysicalMac, IsVirtual: true, OperationalStatus.Up),
        ];
        Assert.Null(NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, peers));
    }

    [Fact]
    public void ResolveBridgedPeer_AbsentNamesake_LosesToThePresentAdapter()
    {
        BridgePeer[] peers = [Physical("Ethernet 3", status: OperationalStatus.NotPresent), Physical()];

        Assert.Equal("Ethernet",
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, peers)?.Name);
    }

    [Fact]
    public void ResolveBridgedPeer_TwoPresentAdaptersShareTheMac_ResolvesNothing()
    {
        // Genuinely ambiguous (teaming/filter oddity): a wrong key is worse than the vNIC's own.
        BridgePeer[] peers = [Physical("Ethernet"), Physical("Ethernet 4")];

        Assert.Null(NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, peers));
    }

    // ── What gets STORED: the physical NIC's MAC, the selected adapter's subnet ──────────

    [Fact]
    public void Compose_BridgedPeer_StoresThePhysicalMacAndKeepsTheSelectedAdaptersSubnet()
    {
        // The selected adapter here is the vEthernet: it owns the IP (10.0.0.0/23) and the wired
        // flag, but its MAC is only borrowed from the NIC behind the switch. The stable one is stored.
        var location = NetworkLocationService.Compose(
            selectedMac: "00:15:5D:EA:DC:CF", cidr: "10.0.0.0/23", wired: true,
            bridged: Physical(), suggestedName: "Ethernet");

        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("10.0.0.0/23", location.IpCidr);
        Assert.True(location.IsWired);
        Assert.Equal("Ethernet", location.DisplayHint);
    }

    [Fact]
    public void Compose_NoBridgedPeer_StoresTheSelectedAdaptersOwnMac()
    {
        // Every fallback lands here: no Hyper-V, an internal switch, an ambiguous or failed pairing.
        var location = NetworkLocationService.Compose(
            selectedMac: DefaultSwitchMac, cidr: "172.20.0.0/16", wired: true,
            bridged: null, suggestedName: "vEthernet (Default Switch)");

        Assert.Equal(DefaultSwitchMac, location.AdapterMac);
        Assert.Equal("172.20.0.0/16", location.IpCidr);
        Assert.Equal("vEthernet (Default Switch)", location.DisplayHint);
    }

    [Fact]
    public void Compose_TheSubnetAndWiredFlagNeverComeFromThePeer()
    {
        // The contract that must not move: pairing may change the MAC and the name, nothing else.
        var withPeer    = NetworkLocationService.Compose("00:15:5D:EA:DC:CF", "10.0.0.0/23", true, Physical(), "Ethernet");
        var withoutPeer = NetworkLocationService.Compose("00:15:5D:EA:DC:CF", "10.0.0.0/23", true, null, BridgeAlias);

        Assert.Equal(withoutPeer.IpCidr,  withPeer.IpCidr);
        Assert.Equal(withoutPeer.IsWired, withPeer.IsWired);
        Assert.NotEqual(withoutPeer.AdapterMac, withPeer.AdapterMac);
    }

    [Fact]
    public void Compose_EmptyMac_BecomesNullSoTheKeyIsNeverAnEmptyString()
    {
        var location = NetworkLocationService.Compose("", "10.0.0.0/23", wired: true, bridged: null, "Ethernet");
        Assert.Null(location.AdapterMac);
    }

    [Fact]
    public void BridgedHost_MeasuredAdapters_StorePhysicalMacAndNameThePhysicalNic()
    {
        // End to end over the pure parts, with the vNIC given a Microsoft-assigned address (a switch
        // recreated with a dynamic MAC) so the two differ: the pairing still finds the physical NIC,
        // and both the stored key and the suggested name follow it rather than the vNIC.
        BridgePeer[] peers = [Physical(), new(BridgeAlias, "00:15:5D:01:02:03", IsVirtual: true, OperationalStatus.Up)];
        var bridged = NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, peers);
        var hint    = NetworkLocationService.SuggestDisplayHint(wired: true, BridgeAlias, bridged, ssid: null);
        var location = NetworkLocationService.Compose(PhysicalMac, "10.0.0.0/23", wired: true, bridged, hint);

        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("Ethernet", location.DisplayHint);
    }

    // ── The suggested name ───────────────────────────────────────────────────

    [Fact]
    public void SuggestDisplayHint_BridgedPeer_NamesThePhysicalNic()
    {
        Assert.Equal("Ethernet",
            NetworkLocationService.SuggestDisplayHint(wired: true, BridgeAlias, Physical(), ssid: null));
    }

    [Fact]
    public void SuggestDisplayHint_NoBridgedPeer_KeepsTheAdaptersOwnAlias()
    {
        Assert.Equal(BridgeAlias,
            NetworkLocationService.SuggestDisplayHint(wired: true, BridgeAlias, bridged: null, ssid: null));
    }

    [Fact]
    public void SuggestDisplayHint_Wireless_StillSuggestsTheSsid()
    {
        Assert.Equal("HomeWiFi",
            NetworkLocationService.SuggestDisplayHint(wired: false, "WiFi", bridged: null, ssid: "HomeWiFi"));
    }

    [Fact]
    public void SuggestDisplayHint_WirelessWithoutSsid_StaysNull()
    {
        // TryGetWifiSsid is best-effort; a null hint makes the caller fall back to "Wireless network".
        Assert.Null(NetworkLocationService.SuggestDisplayHint(wired: false, "WiFi", bridged: null, ssid: null));
    }

    // ── The stale-key hint on a saved rule ─────────────────────────────────

    [Fact]
    public void IsStaleKey_SameSubnetDifferentMac_IsStale()
    {
        // The user's own case: a profile saved on an older dock (3C:2C:30:CA:98:D7) while the adapter
        // in use now reports 48:65:EE:18:86:EF on the same subnet. It can never match again.
        var rule    = new NetworkLocationRule { AdapterMac = "3C:2C:30:CA:98:D7", IpCidr = "10.0.0.0/23" };
        var current = new NetworkLocation(PhysicalMac, "10.0.0.0/23", IsWired: true, DisplayHint: "Ethernet");

        Assert.True(NetworkLocationService.IsStaleKey(rule, current));
    }

    [Fact]
    public void IsStaleKey_MatchingRule_IsNotStale()
    {
        var rule    = new NetworkLocationRule { AdapterMac = PhysicalMac, IpCidr = "10.0.0.0/23" };
        var current = new NetworkLocation(PhysicalMac, "10.0.0.0/23", true, "Ethernet");

        Assert.False(NetworkLocationService.IsStaleKey(rule, current));
    }

    [Fact]
    public void IsStaleKey_DifferentSubnet_IsNotStale()
    {
        // A profile for another network is simply not this one — saying nothing is the whole point.
        var rule    = new NetworkLocationRule { AdapterMac = "3C:2C:30:CA:98:D7", IpCidr = "192.168.1.0/24" };
        var current = new NetworkLocation(PhysicalMac, "10.0.0.0/23", true, "Ethernet");

        Assert.False(NetworkLocationService.IsStaleKey(rule, current));
    }

    [Fact]
    public void IsStaleKey_SubnetOnlyRule_IsNotStale()
    {
        // No MAC in the key means the subnet alone matches; there is nothing stale about it.
        var rule    = new NetworkLocationRule { IpCidr = "10.0.0.0/23" };
        var current = new NetworkLocation(PhysicalMac, "10.0.0.0/23", true, "Ethernet");

        Assert.False(NetworkLocationService.IsStaleKey(rule, current));
    }

    [Fact]
    public void IsStaleKey_NoCurrentLocation_IsNotStale()
    {
        // Offline, or the first evaluation has not landed: nothing to compare against, so say nothing.
        var rule = new NetworkLocationRule { AdapterMac = "3C:2C:30:CA:98:D7", IpCidr = "10.0.0.0/23" };

        Assert.False(NetworkLocationService.IsStaleKey(rule, default));
    }

    // ── The match key shown in the naming dialog and the Settings rows ─────────────

    [Fact]
    public void DescribeMatchKey_MacAndSubnet_JoinedWithTheHouseSeparator()
    {
        Assert.Equal($"MAC {PhysicalMac} · Subnet 10.0.0.0/23",
            NetworkLocationService.DescribeMatchKey(PhysicalMac, "10.0.0.0/23"));
    }

    [Fact]
    public void DescribeMatchKey_SingleFacet_ShowsOnlyThatFacet()
    {
        Assert.Equal($"MAC {PhysicalMac}",  NetworkLocationService.DescribeMatchKey(PhysicalMac, null));
        Assert.Equal("Subnet 10.0.0.0/23", NetworkLocationService.DescribeMatchKey(null, "10.0.0.0/23"));
    }

    [Fact]
    public void DescribeMatchKey_NoFacets_SaysTheProfileCanNeverApply()
    {
        Assert.Contains("never apply", NetworkLocationService.DescribeMatchKey(null, null));
    }
}
