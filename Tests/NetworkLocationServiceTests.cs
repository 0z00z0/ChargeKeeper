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

    // The first-evaluation seed rule

    [Fact]
    public void IsLocationChange_FirstEvaluation_SeedsWithoutBeingAChange()
    {
        // Start() evaluates immediately, with _last still default. Raising LocationChanged on that
        // baseline would re-apply a network profile on every app start and cancel a persisted travel
        // override, so the first reading only seeds.
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

    // SelectPrimary: the primary-adapter heuristic, exercised without live adapters

    [Fact]
    public void BridgedHyperV_RoutingTableNamesTheSwitchPort_ButTheKeyComesFromThePhysicalNic()
    {
        // On a Hyper-V external switch the routable IP and default route live on the virtual
        // "vEthernet (…)" adapter while the bridged physical NIC keeps no usable IP, so the routing
        // table still names the switch port and SelectPrimary still returns it. What the key is taken
        // from is the reverse of what it once was: the MAC and the suggested name follow the NIC
        // behind the switch, and only the subnet stays with the port that holds the address.
        var port = BridgedSwitchPort();

        Assert.Same(port, NetworkLocationService.SelectPrimary([port], bestIndex: 31));

        var location = NetworkLocationService.Detect([port], MeasuredPeers, bestIndex: 31, () => null);

        // The name is the discriminator that the key follows the NIC: the port is called
        // "vEthernet (Bridged)" and would have named itself.
        Assert.Equal("Ethernet", location.DisplayHint);
        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("10.0.1.0/23", location.IpCidr);
    }

    [Fact]
    public void Detect_HyperVSwitchPortWithNoNicBehindIt_ReportsNoLocation()
    {
        // A switch recreated with a dynamic MAC leaves the port on a Microsoft-OUI address that pairs
        // with nothing. The old contract stored that address; storing it is what let one key stand for
        // several places, so the reading is given up instead.
        var orphan = Adapter(31, NetworkInterfaceType.Ethernet, BridgeAlias, BridgeDesc,
                             "00:15:5D:01:02:03", "10.0.1.0/23", metric: 15);

        Assert.True(NetworkLocationService.Detect(
            [orphan], [Peer(orphan)], bestIndex: 31, () => null).IsEmpty);
    }

    [Fact]
    public void Detect_UnresolvableSwitchPortWithOtherAdaptersUp_KeysOnNoneOfThem()
    {
        // The measured defect, and what the one-candidate case above cannot show: the routed switch
        // port failed to pair, the walk fell through to a metric scan over every other live adapter,
        // and a docked machine was keyed on its idle mobile modem — a different place, silently
        // applying that place's charge thresholds.
        var orphan = Adapter(31, NetworkInterfaceType.Ethernet, BridgeAlias, BridgeDesc,
                             "00:15:5D:01:02:03", "10.0.1.0/23", metric: 15);
        var modem  = Tether("100.110.83.0/24", metric: 5);   // lowest metric, so the scan would land here
        AdapterCandidate[] all = [orphan, modem];

        var location = NetworkLocationService.Detect(all, PeersOf(all), bestIndex: 31, () => null);

        Assert.NotEqual(MobileMac, location.AdapterMac);
        Assert.True(location.IsEmpty);
    }

    [Fact]
    public void Detect_SwitchPortWhoseUplinkCannotBeNamed_KeepsTheKeyAndDropsTheAlias()
    {
        // Two host vNICs on one external switch: both wear the bound NIC's cloned address, so nothing
        // non-virtual pairs and the uplink cannot be named. The address is still the NIC's, byte for
        // byte, so the key is unchanged from the case that does resolve — only the name is unknown,
        // and a "vEthernet (…)" alias must never become one: it names the switch, not the place.
        var port = BridgedSwitchPort();
        BridgePeer[] twoPorts =
        [
            Interface(BridgeAlias,            BridgeDesc, PhysicalMac),
            Interface("vEthernet (Bridged 2)", BridgeDesc, PhysicalMac),
        ];

        var (location, adapter) = NetworkLocationService.DetectDetailed(
            [port], twoPorts, bestIndex: 31, () => null);

        Assert.True(location.SameLocationAs(
            NetworkLocationService.Detect([port], MeasuredPeers, bestIndex: 31, () => null)));
        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("10.0.1.0/23", location.IpCidr);

        // Every surface a name reaches: the naming default, the "Current network" line, and the two
        // Home Assistant adapter attributes.
        Assert.Equal(NetworkLocationService.WiredLabel, location.DisplayHint);
        Assert.Equal(NetworkLocationService.WiredLabel, adapter.Alias);
        Assert.Equal(NetworkLocationService.WiredLabel, adapter.AdapterName);
        Assert.Equal($"{NetworkLocationService.WiredLabel} · 10.0.1.0/23",
                     NetworkLocationService.DescribeCurrentNetwork(location, null));
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


    // The Hyper-V bridge walk-back. The fixtures are a measured adapter set: the physical "Ethernet"
    // is Up with no IPv4 (the external switch took it) and shares its MAC verbatim with
    // "vEthernet (Bridged)", which holds the routable address; "vEthernet (Default Switch)" is an
    // internal switch with a synthesised Microsoft-OUI address and no physical partner.

    private const string PhysicalMac       = "48:65:EE:18:86:EF";
    private const string DefaultSwitchMac  = "00:15:5D:EA:DC:CF";
    private const string BridgeAlias       = "vEthernet (Bridged)";
    private const string BridgeDesc        = "Hyper-V Virtual Ethernet Adapter #2";
    private const string PhysicalDesc      = "Realtek USB GbE Family Controller";
    private const string WifiDesc          = "Intel(R) Wi-Fi 6E AX211 160MHz";
    private const string DefaultSwitchDesc = "Hyper-V Virtual Ethernet Adapter";

    // The four NDIS layers Windows binds, named exactly as the stack reports them. "Virtual WiFi" is
    // the odd one out: only its name says "Virtual", which is why LooksVirtual catches it and the
    // other three had to be named in FilterMarkers instead.
    private const string WfpNative   = "WFP Native MAC Layer LightWeight Filter-0000";
    private const string Wfp8023     = "WFP 802.3 MAC Layer LightWeight Filter-0000";
    private const string QosLayer    = "QoS Packet Scheduler-0000";
    private const string NativeWifi  = "Native WiFi Filter Driver-0000";
    private const string VirtualWifi = "Virtual WiFi Filter Driver-0000";

    // IsVirtual comes from the real predicate, as it does for AdapterCandidate below.
    private static BridgePeer Interface(string name, string description, string? mac,
                                        OperationalStatus status = OperationalStatus.Up) =>
        new(name, mac, NetworkLocationService.LooksVirtual(description), status, description);

    private static BridgePeer Physical(string name = "Ethernet", string mac = PhysicalMac,
                                       OperationalStatus status = OperationalStatus.Up) =>
        Interface(name, PhysicalDesc, mac, status);

    // A filter layer bound to a host adapter: measured, the stack suffixes BOTH the host's alias and
    // its description, and the layer clones the host's hardware address verbatim.
    private static BridgePeer Twin(string hostAlias, string hostDesc, string layer, string? mac,
                                   OperationalStatus status = OperationalStatus.Up) =>
        Interface($"{hostAlias}-{layer}", $"{hostDesc}-{layer}", mac, status);

    // Recorded from a live GetAllNetworkInterfaces() on the affected machine, not composed: 77
    // interfaces there against the 17 rows Get-NetAdapter shows. Every interface carrying one of the
    // three hardware addresses the walk-back reasons about is kept verbatim with its real
    // description; the repetitive remainder — 22 NotPresent "Mobile N" clones, the WAN miniport
    // family, the Wi-Fi Direct pair and the tunnels — is left out, since nothing in the walk-back can
    // reach it. Docked, so "Ethernet" is Up holding no IPv4 (the external switch took it) and its own
    // filter twin is present; undocked, that twin is the one row of the six that disappears.
    private static readonly BridgePeer[] MeasuredInterfaces =
    [
        // The dock's address, on six interfaces, of which Get-NetAdapter shows two.
        Physical(),                                            // Realtek USB GbE, no IPv4
        Twin("Ethernet", PhysicalDesc, WfpNative, PhysicalMac),
        Interface(BridgeAlias, BridgeDesc, PhysicalMac),
        Twin(BridgeAlias, BridgeDesc, WfpNative, PhysicalMac),
        Twin(BridgeAlias, BridgeDesc, Wfp8023,   PhysicalMac),
        Twin(BridgeAlias, BridgeDesc, QosLayer,  PhysicalMac),

        // The radio's address, on six more. Three of these layers are classified non-virtual, so this
        // is the set that decides whether the filter list is complete.
        Interface("WiFi", WifiDesc, WifiMac),
        Twin("WiFi", WifiDesc, WfpNative,   WifiMac),
        Twin("WiFi", WifiDesc, Wfp8023,     WifiMac),
        Twin("WiFi", WifiDesc, QosLayer,    WifiMac),
        Twin("WiFi", WifiDesc, NativeWifi,  WifiMac),
        Twin("WiFi", WifiDesc, VirtualWifi, WifiMac),

        // The internal switch: a Microsoft-OUI address belonging to no NIC, with its own layers.
        Interface("vEthernet (Default Switch)", DefaultSwitchDesc, DefaultSwitchMac),
        Twin("vEthernet (Default Switch)", DefaultSwitchDesc, WfpNative, DefaultSwitchMac),
        Twin("vEthernet (Default Switch)", DefaultSwitchDesc, Wfp8023,   DefaultSwitchMac),
        Twin("vEthernet (Default Switch)", DefaultSwitchDesc, QosLayer,  DefaultSwitchMac),

        // The modem, and the adapters the pairing must never reach for.
        Interface("Mobile", "5G Solution 5000", MobileMac),
        Twin("Mobile", "5G Solution 5000", WfpNative, MobileMac),
        Interface("Ethernet 2", "PANGP Virtual Ethernet Adapter Secure", "02:50:41:00:00:01", OperationalStatus.Down),
        Interface("Petterhagen - Dell docking", $"{PhysicalDesc} #2", DockMac, OperationalStatus.NotPresent),
        Interface("Ethernet 3", "Lenovo USB Ethernet", "60:7D:09:45:F4:E8", OperationalStatus.NotPresent),
        Interface("Local Area Connection* 10", "WAN Miniport (Network Monitor)", null),
    ];

    // The same set as the shipped enumeration hands to the walk-back: filter layers gone, so the one
    // hardware address is left on the two adapters that really own it.
    private static readonly BridgePeer[] MeasuredPeers =
        [.. MeasuredInterfaces.Where(i => !NetworkLocationService.IsFilterInterface(i.Name, i.Description))];

    // FilterMarkers as it stood before the scheduler and Native WiFi layers were named, so a test can
    // measure what closing that gap actually moved rather than assert it in the abstract.
    private static readonly string[] MarkersBeforeTheSchedulerGapWasClosed =
        ["-WFP ", "LightWeight Filter", "-NDIS ", "Multiplexor"];

    private static bool WasFilteredBefore(BridgePeer i) =>
        MarkersBeforeTheSchedulerGapWasClosed.Any(m =>
            i.Name.Contains(m, StringComparison.OrdinalIgnoreCase) ||
            (i.Description ?? "").Contains(m, StringComparison.OrdinalIgnoreCase));

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
        // Measured: the external switch's vNIC inherits the bound NIC's hardware address verbatim,
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
    public void ResolveBridgedPeer_TwoPresentAdaptersShareTheMac_NamesTheFirstInAliasOrder()
    {
        // Tied partners carry the same address by definition — that address is the whole key, so the
        // choice cannot move it and only the name shown is at stake. Ordinal alias order settles it,
        // so an unordered adapter list always names the same NIC rather than giving up and letting
        // the switch port's own "vEthernet (…)" alias stand.
        BridgePeer[] peers    = [Physical("Ethernet"), Physical("Ethernet 4")];
        BridgePeer[] reversed = [Physical("Ethernet 4"), Physical("Ethernet")];

        Assert.Equal("Ethernet",
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, peers)?.Name);
        Assert.Equal("Ethernet",
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, reversed)?.Name);
    }

    // The uplink Windows names, which is preferred over the MAC pairing above. The rows are measured
    // from MSFT_NetAdapterBindingSettingData (root\standardcimv2, ComponentID 'vms_pp'): Name is the
    // connection alias and InterfaceDescription the hardware name. Docked, exactly one row is enabled
    // and it names the dock; undocked, five rows come back and every one is disabled.

    private static readonly UplinkBinding[] BoundToTheDock = [new("Ethernet", PhysicalDesc)];

    [Fact]
    public void Detect_SwitchWithAnOverriddenMac_KeysOnTheBoundUplinkAndNotTheClone()
    {
        // What the MAC pairing cannot see. Cloning the uplink's address onto the vNIC is Hyper-V's
        // default, not its contract; with the address hand-set, nothing pairs and the reading is given
        // up. The binding record still names the NIC, so the key follows it.
        var port = BridgedSwitchPort() with { Mac = "00:15:5D:01:02:03" };
        BridgePeer[] peers = [Physical(), Interface(BridgeAlias, BridgeDesc, "00:15:5D:01:02:03")];

        var bound  = NetworkLocationService.Detect([port], peers, 31, () => null, () => BoundToTheDock);
        var byMacs = NetworkLocationService.Detect([port], peers, 31, () => null);

        Assert.Equal(PhysicalMac, bound.AdapterMac);
        Assert.Equal("Ethernet", bound.DisplayHint);
        Assert.Equal("10.0.1.0/23", bound.IpCidr);
        Assert.True(byMacs.IsEmpty);   // the same table without the binding resolves nothing
    }

    [Fact]
    public void Detect_NoEnabledBinding_ReadsExactlyAsTheMacPairingAlone()
    {
        // Undocked, or no Hyper-V at all: measured, the rows that come back are all disabled, so the
        // filtered read is empty and nothing about the reading may move.
        var port = BridgedSwitchPort();

        var withReader = NetworkLocationService.DetectDetailed([port], MeasuredPeers, 31, () => null, () => []);
        var without    = NetworkLocationService.DetectDetailed([port], MeasuredPeers, 31, () => null);

        Assert.Equal(PhysicalMac, withReader.Location.AdapterMac);
        Assert.Equal(without, withReader);
    }

    [Fact]
    public void Detect_TheBindingReadThrows_StillResolvesThroughTheMacPairing()
    {
        // The read is a WMI round trip on every network change. It must never be the reason a location
        // fails to resolve.
        var location = NetworkLocationService.Detect(
            [BridgedSwitchPort()], MeasuredPeers, 31, () => null,
            () => throw new InvalidOperationException("WMI unavailable"));

        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("Ethernet", location.DisplayHint);
    }

    [Fact]
    public void Detect_NoSwitchPortAmongTheCandidates_NeverReadsTheBindings()
    {
        // Measured at 300-650 ms per read, so a machine with no Hyper-V must not pay it on every
        // network change.
        var wifi = WifiCard("10.0.0.0/23");
        bool read = false;

        NetworkLocationService.Detect([wifi], PeersOf(wifi), 9, () => "HomeWiFi",
                                      () => { read = true; return []; });

        Assert.False(read);
    }

    [Fact]
    public void ResolveBoundUplink_TwoEnabledBindings_DefersToTheMacPairing()
    {
        // vms_pp records that an adapter carries AN external switch, not which one. With two switches
        // the record cannot say which this port belongs to, and the MAC — which is per-switch — can.
        UplinkBinding[] two =
        [
            new("Ethernet",   PhysicalDesc),
            new("Ethernet 4", "ASIX USB to Gigabit Ethernet Family Adapter"),
        ];

        Assert.Null(NetworkLocationService.ResolveBoundUplink(BridgeAlias, BridgeDesc, MeasuredPeers, two));
        Assert.Equal("Ethernet", NetworkLocationService.ResolveSwitchUplink(
            BridgeAlias, BridgeDesc, PhysicalMac, MeasuredPeers, two)?.Name);
    }

    [Fact]
    public void ResolveBoundUplink_RowNamingAvNic_DefersToTheMacPairing()
    {
        // Measured: the host vNICs appear among the vms_pp rows themselves, so a row is not by itself
        // proof of a NIC. Only a non-virtual peer holding a hardware address can be the uplink.
        UplinkBinding[] namesTheVnic = [new(BridgeAlias, BridgeDesc)];

        Assert.Null(NetworkLocationService.ResolveBoundUplink(
            BridgeAlias, BridgeDesc, MeasuredPeers, namesTheVnic));
    }

    [Fact]
    public void ResolveBoundUplink_AliasRenamed_StillMatchesOnTheHardwareName()
    {
        // The alias is user-editable, and the binding row and the adapter list are different tables.
        UplinkBinding[] byDescription = [new("Renamed by the user", PhysicalDesc)];

        Assert.Equal("Ethernet", NetworkLocationService.ResolveBoundUplink(
            BridgeAlias, BridgeDesc, MeasuredPeers, byDescription)?.Name);
    }

    [Fact]
    public void ResolveBoundUplink_PlainPhysicalAdapter_IsNeverRepointed()
    {
        // The same gate the MAC pairing has: an adapter that is not a switch port must never be
        // re-keyed onto whatever the binding happens to name.
        Assert.Null(NetworkLocationService.ResolveBoundUplink("WiFi", WifiDesc, MeasuredPeers, BoundToTheDock));
    }

    [Fact]
    public void Detect_BoundUplinkOverAVpn_LeavesTheTunnelPathAlone()
    {
        // The metric scan is what finds the NIC under a tunnel, and the binding must not divert it: the
        // tunnel holds the route, the switch port holds the address, the binding names the NIC.
        AdapterCandidate[] all = [WireGuard(), BridgedSwitchPort(), Tether("100.110.83.0/24")];

        var location = NetworkLocationService.Detect(all, MeasuredPeers, VpnIndex, () => null,
                                                     () => BoundToTheDock);

        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("10.0.1.0/23", location.IpCidr);
    }

    // NDIS filter layers, which GetAllNetworkInterfaces returns as interfaces of their own

    [Theory]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000")]
    [InlineData("vEthernet (Bridged)-WFP 802.3 MAC Layer LightWeight Filter-0000")]
    [InlineData("Ethernet-NDIS Capture LightWeight Filter-0000")]
    [InlineData("Microsoft Network Adapter Multiplexor Driver")]
    public void IsFilterInterface_FilterLayers_AreNotAdapters(string label)
    {
        // Either side of the pair is enough: the suffix lands on the alias for some layers and on the
        // description for others.
        Assert.True(NetworkLocationService.IsFilterInterface(label, "Realtek USB GbE Family Controller"));
        Assert.True(NetworkLocationService.IsFilterInterface("Ethernet", label));
    }

    [Theory]
    [InlineData("Ethernet",            "Realtek USB GbE Family Controller")]
    [InlineData(BridgeAlias,           BridgeDesc)]
    [InlineData("WiFi",                "Intel(R) Wi-Fi 6E AX211 160MHz")]
    [InlineData("Mobile",              "5G Solution 5000")]
    public void IsFilterInterface_RealAdaptersAreLeftAlone(string name, string description)
    {
        Assert.False(NetworkLocationService.IsFilterInterface(name, description));
    }

    [Theory]
    [InlineData("WiFi",         WifiDesc,   QosLayer)]
    [InlineData("WiFi",         WifiDesc,   NativeWifi)]
    [InlineData(BridgeAlias,    BridgeDesc, QosLayer)]
    [InlineData("Ethernet",     PhysicalDesc, QosLayer)]
    public void IsFilterInterface_TheSchedulerAndNativeWifiLayers_AreNotAdapters(
        string hostAlias, string hostDesc, string layer)
    {
        // Neither layer carries "-WFP " or "LightWeight Filter" in either half of its name, so both
        // passed the marker list as adapters. Measured, they clone the host's hardware address, and on
        // the radio they are classified non-virtual as well — nothing else keeps them out of the
        // pairing.
        var twin = Twin(hostAlias, hostDesc, layer, PhysicalMac);
        Assert.True(NetworkLocationService.IsFilterInterface(twin.Name, twin.Description));
    }

    [Fact]
    public void FilterMarkers_ClosingTheSchedulerGap_ThinsTheRadioAndLeavesTheDockUnmoved()
    {
        BridgePeer[] before = [.. MeasuredInterfaces.Where(i => !WasFilteredBefore(i))];

        // The leak, measured: on the radio's address the old list left three non-virtual partners
        // where Get-NetAdapter shows one card.
        Assert.Equal(3, before.Count(i => i.Mac == WifiMac && !i.IsVirtual));
        Assert.Equal("WiFi", MeasuredPeers.Single(i => i.Mac == WifiMac && !i.IsVirtual).Name);
        Assert.DoesNotContain(MeasuredPeers, i => i.Name.Contains(QosLayer, StringComparison.Ordinal));
        Assert.DoesNotContain(MeasuredPeers, i => i.Name.Contains(NativeWifi, StringComparison.Ordinal));

        // The third layer on that address still reaches the peer list: only its name says "Virtual",
        // so the pairing rejects it as a peer rather than the enumeration as a layer.
        Assert.Contains(MeasuredPeers, i => i.Name == $"WiFi-{VirtualWifi}" && i.IsVirtual);

        // And why the leak was benign: on the dock address the scheduler clone is virtual, so the same
        // one partner is left and the pairing names the same NIC either way.
        Assert.Equal(
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, before)?.Name,
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, MeasuredPeers)?.Name);
        Assert.Equal("Ethernet",
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, MeasuredPeers)?.Name);
    }

    [Fact]
    public void MeasuredInterfaces_FilterTwinsAreDropped_LeavingOneNicOnTheSharedMac()
    {
        // The defect this fixes: six interfaces carry the one address, and the physical NIC's own
        // filter twin is non-virtual, so it survived as a second partner and the pairing tied. Dropping
        // the layers leaves exactly one NIC on that address, which is what makes the walk-back settle.
        Assert.Equal(6, MeasuredInterfaces.Count(i => i.Mac == PhysicalMac));
        Assert.Equal(2, MeasuredInterfaces.Count(i => i.Mac == PhysicalMac && !i.IsVirtual));

        Assert.DoesNotContain(MeasuredPeers, i => i.Name.Contains("LightWeight Filter"));
        Assert.Equal("Ethernet", MeasuredPeers.Single(i => i.Mac == PhysicalMac && !i.IsVirtual).Name);
    }

    [Fact]
    public void ResolveBridgedPeer_MeasuredDockedInterfaces_PairWithTheNicAndNotItsFilterTwin()
    {
        // End to end over the measured set: unfiltered, the NIC and its filter twin tie; filtered, the
        // NIC is named.
        Assert.Equal("Ethernet",
            NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, MeasuredPeers)?.Name);
    }

    // What gets stored: the physical NIC's MAC, the selected adapter's subnet

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
        // The vNIC carries a Microsoft-assigned address (a switch recreated with a dynamic MAC) so
        // the two differ, and both the stored key and the suggested name must follow the physical NIC.
        BridgePeer[] peers = [Physical(), new(BridgeAlias, "00:15:5D:01:02:03", IsVirtual: true, OperationalStatus.Up)];
        var bridged = NetworkLocationService.ResolveBridgedPeer(BridgeAlias, BridgeDesc, PhysicalMac, peers);
        var hint    = NetworkLocationService.SuggestDisplayHint(wired: true, BridgeAlias, bridged, ssid: null);
        var location = NetworkLocationService.Compose(PhysicalMac, "10.0.0.0/23", wired: true, bridged, hint);

        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("Ethernet", location.DisplayHint);
    }

    // How the resolved adapter describes itself

    [Fact]
    public void ComposeAdapter_NoBridge_TakesTheCarriersOwnAliasAddressAndHardwareName()
    {
        var carrier = Adapter(9, NetworkInterfaceType.Wireless80211, "WiFi",
                              "Intel(R) Wi-Fi 6E AX211 160MHz", WifiMac, "10.0.0.0/23", metric: 30)
                      with { IpAddress = "10.0.0.42" };

        var info = NetworkLocationService.ComposeAdapter(new PhysicalRoute(carrier, null));

        Assert.Equal("WiFi", info.Alias);
        Assert.Equal("10.0.0.42", info.IpAddress);
        Assert.Equal("Intel(R) Wi-Fi 6E AX211 160MHz", info.AdapterName);
    }

    [Fact]
    public void ComposeAdapter_BehindASwitch_NamesThePhysicalNic_ButKeepsTheCarriersAddress()
    {
        // The NIC bound behind an external switch holds no address of its own, so the alias and the
        // hardware name follow it while the address stays with the port that actually has one.
        var port = BridgedSwitchPort() with { IpAddress = "10.0.1.7" };
        var nic  = new BridgePeer("Ethernet", PhysicalMac, IsVirtual: false, OperationalStatus.Up,
                                  "Intel(R) Ethernet Connection I219-LM");

        var info = NetworkLocationService.ComposeAdapter(new PhysicalRoute(port, nic));

        Assert.Equal("Ethernet", info.Alias);
        Assert.Equal("10.0.1.7", info.IpAddress);
        Assert.Equal("Intel(R) Ethernet Connection I219-LM", info.AdapterName);
    }

    [Fact]
    public void ComposeAdapter_BlankReadingsBecomeNull_SoTheEntityReadsUnknown()
    {
        var bare = new AdapterCandidate(3, IsVirtual: false, Type: NetworkInterfaceType.Ethernet);
        var info = NetworkLocationService.ComposeAdapter(new PhysicalRoute(bare, null));

        Assert.Null(info.Alias);
        Assert.Null(info.IpAddress);
        Assert.Null(info.AdapterName);
        Assert.True(info.IsEmpty);
    }

    // The suggested name

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

    // The stale-key hint on a saved rule

    [Fact]
    public void IsStaleKey_SameSubnetDifferentMac_IsStale()
    {
        // A profile saved on an older dock, while the adapter in use reports a different MAC on the
        // same subnet: the rule can never match again.
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

    // The match key shown in the naming dialog and the Settings rows

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

    // Resolving the routed adapter down to the physical NIC. The fixtures are measured from this
    // machine: a WireGuard tunnel reports IANA ifType 53 with no hardware address at all, mobile
    // broadband reports Wwanpp rather than Ethernet, and the interface metrics are the ones
    // Get-NetIPInterface shows.

    private const string WifiMac   = "30:89:4A:68:1C:3A";
    private const string MobileMac = "84:8D:B0:53:5F:55";
    private const string DockMac   = "3C:2C:30:CA:98:D7";
    private const string VpnCidr   = "10.0.50.0/24";
    private const uint   VpnIndex  = 72;

    // IsVirtual comes from the real predicate, so a fixture can never claim a nature the shipped
    // classification would not give it.
    private static AdapterCandidate Adapter(
        int index, NetworkInterfaceType type, string name, string description,
        string? mac, string? cidr, uint metric) =>
        new(index, NetworkLocationService.LooksVirtual(description), type, name, description, mac, cidr, metric);

    private static AdapterCandidate WireGuard(string cidr = VpnCidr) =>
        Adapter((int)VpnIndex, (NetworkInterfaceType)53, "B1", "WireGuard Tunnel", null, cidr, metric: 5);

    private static AdapterCandidate WifiCard(string cidr, uint metric = 30) =>
        Adapter(9, NetworkInterfaceType.Wireless80211, "WiFi", "Intel(R) Wi-Fi 6E AX211 160MHz", WifiMac, cidr, metric);

    private static AdapterCandidate Dock(string cidr, uint metric = 25) =>
        Adapter(5, NetworkInterfaceType.Ethernet, "Ethernet", "Realtek USB GbE Family Controller", DockMac, cidr, metric);

    private static AdapterCandidate Tether(string cidr, uint metric = 35) =>
        Adapter(15, NetworkInterfaceType.Wwanpp, "Mobile", "5G Solution 5000", MobileMac, cidr, metric);

    // The external switch's port, holding the address while the NIC behind it holds none and lending
    // its hardware address verbatim — which is what makes the pairing possible at all.
    private static AdapterCandidate BridgedSwitchPort() =>
        Adapter(31, NetworkInterfaceType.Ethernet, BridgeAlias, BridgeDesc, PhysicalMac, "10.0.1.0/23", metric: 15);

    private static BridgePeer Peer(AdapterCandidate c) => new(c.Name, c.Mac, c.IsVirtual, OperationalStatus.Up);

    private static BridgePeer[] PeersOf(params AdapterCandidate[] candidates) => [.. candidates.Select(Peer)];

    // The routing table points at the tunnel, which is what a live VPN makes it do.
    private static NetworkLocation DetectBehindVpn(params AdapterCandidate[] carriers)
    {
        AdapterCandidate[] all = [WireGuard(), .. carriers];
        return NetworkLocationService.Detect(all, PeersOf(all), VpnIndex, () => "SomeSsid");
    }

    [Theory]
    [InlineData("WireGuard Tunnel")]
    [InlineData("PANGP Virtual Ethernet Adapter Secure")]
    [InlineData("Hyper-V Virtual Ethernet Adapter #2")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Software Loopback Interface 1")]
    public void LooksVirtual_RecognisesWhatIsNotANic(string description)
    {
        Assert.True(NetworkLocationService.LooksVirtual(description));
    }

    [Theory]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz")]
    [InlineData("Realtek USB GbE Family Controller")]
    [InlineData("5G Solution 5000")]
    [InlineData("Bluetooth Device (Personal Area Network)")]
    public void LooksVirtual_LeavesRealNicsAlone(string description)
    {
        Assert.False(NetworkLocationService.LooksVirtual(description));
    }

    [Fact]
    public void IsPhysical_MobileBroadband_CountsAsANic()
    {
        // Measured: Windows reports the 5G modem as Wwanpp, so restricting the type list to Ethernet
        // and Wireless80211 would leave tethering resolving to nothing.
        Assert.True(NetworkLocationService.IsPhysical(Tether("100.110.83.0/24")));
    }

    [Fact]
    public void IsPhysical_TunnelWithNoHardwareAddress_IsNotANic()
    {
        Assert.False(NetworkLocationService.IsPhysical(WireGuard()));
    }

    [Fact]
    public void IsPhysical_VpnAdapterWithAnEthernetTypeAndAMac_IsNotANic()
    {
        // A VPN client's adapter is indistinguishable from a NIC apart from its description.
        var pangp = Adapter(28, NetworkInterfaceType.Ethernet, "Ethernet 2",
                            "PANGP Virtual Ethernet Adapter Secure", "02:50:41:00:00:01", "10.9.0.0/24", metric: 20);

        Assert.False(NetworkLocationService.IsPhysical(pangp));
    }

    [Fact]
    public void Detect_OneVpnOverWifiEthernetAndMobile_KeysStayApart()
    {
        // The defect this whole path exists for: with the tunnel supplying the key, all three read as
        // one place and one set of charge thresholds followed the machine everywhere.
        var overWifi     = DetectBehindVpn(WifiCard("10.0.0.0/23"));
        var overEthernet = DetectBehindVpn(Dock("192.168.1.0/24"));
        var overMobile   = DetectBehindVpn(Tether("100.110.83.0/24"));

        Assert.Equal(WifiMac,   overWifi.AdapterMac);
        Assert.Equal(DockMac,   overEthernet.AdapterMac);
        Assert.Equal(MobileMac, overMobile.AdapterMac);

        Assert.False(overWifi.SameLocationAs(overEthernet));
        Assert.False(overWifi.SameLocationAs(overMobile));
        Assert.False(overEthernet.SameLocationAs(overMobile));

        // And the tunnel's own subnet reaches none of them.
        Assert.All([overWifi, overEthernet, overMobile], l => Assert.NotEqual(VpnCidr, l.IpCidr));
    }

    [Fact]
    public void Detect_OneWifiCardOnTwoSubnets_KeysStayApart()
    {
        // A home and a cabin reached over the same radio differ only by subnet, so the subnet cannot
        // leave the key.
        var home  = DetectBehindVpn(WifiCard("10.0.0.0/23"));
        var cabin = DetectBehindVpn(WifiCard("10.0.20.0/24"));

        Assert.Equal(home.AdapterMac, cabin.AdapterMac);
        Assert.False(home.SameLocationAs(cabin));
    }

    [Fact]
    public void Detect_DockedWithTheWifiRadioStillUp_LowestInterfaceMetricDecides()
    {
        // Docked, Windows gives the wired link the lower metric and routes over it, so that is the
        // adapter the location is keyed on.
        var location = DetectBehindVpn(WifiCard("10.0.0.0/23", metric: 30), Dock("192.168.1.0/24", metric: 25));

        Assert.Equal(DockMac, location.AdapterMac);
        Assert.Equal("192.168.1.0/24", location.IpCidr);
    }

    [Fact]
    public void Detect_DockTieBreak_FollowsTheMetricAndNotTheAdapterType()
    {
        // Metrics swapped: nothing about "wired beats wireless" decides this.
        var location = DetectBehindVpn(WifiCard("10.0.0.0/23", metric: 20), Dock("192.168.1.0/24", metric: 45));

        Assert.Equal(WifiMac, location.AdapterMac);
        Assert.Equal("10.0.0.0/23", location.IpCidr);
    }

    [Fact]
    public void Detect_VpnOverAHyperVBridgedDock_ResolvesThroughBothLayersToTheNic()
    {
        // Two layers at once, which is this machine docked with the tunnel up: the tunnel holds the
        // route, the switch port holds the address, and the NIC behind the switch holds neither.
        AdapterCandidate[] all = [WireGuard(), BridgedSwitchPort(), Tether("100.110.83.0/24")];

        var location = NetworkLocationService.Detect(all, MeasuredPeers, VpnIndex, () => null);

        Assert.Equal(PhysicalMac, location.AdapterMac);
        Assert.Equal("10.0.1.0/23", location.IpCidr);
    }

    [Fact]
    public void Detect_PlainWifi_KeysOnTheCardAndSuggestsTheSsid()
    {
        var wifi = WifiCard("10.0.0.0/23");

        var location = NetworkLocationService.Detect([wifi], PeersOf(wifi), bestIndex: 9, () => "HomeWiFi");

        Assert.Equal(WifiMac, location.AdapterMac);
        Assert.Equal("10.0.0.0/23", location.IpCidr);
        Assert.False(location.IsWired);
        Assert.Equal("HomeWiFi", location.DisplayHint);
    }

    [Fact]
    public void Detect_NothingButATunnel_ReportsNoLocation()
    {
        // Fail closed: a wrong location applies the wrong charge thresholds without a word.
        var vpn = WireGuard();

        Assert.True(NetworkLocationService.Detect([vpn], PeersOf(vpn), VpnIndex, () => null).IsEmpty);
    }

    [Fact]
    public void Detect_InternalHyperVSwitchOnly_ReportsNoLocation()
    {
        // "vEthernet (Default Switch)" holds a routable address on a Microsoft-OUI MAC belonging to no
        // NIC. Keying on it would name the host's own private switch as a place.
        var internalSwitch = Adapter(51, NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)",
                                     "Hyper-V Virtual Ethernet Adapter", DefaultSwitchMac, "172.24.64.0/20", metric: 5000);

        Assert.True(NetworkLocationService.Detect(
            [internalSwitch], PeersOf(internalSwitch), bestIndex: 51, () => null).IsEmpty);
    }

    [Fact]
    public void Detect_NoAdaptersAtAll_ReportsNoLocation()
    {
        Assert.True(NetworkLocationService.Detect([], [], bestIndex: 0, () => null).IsEmpty);
    }

    // The stale-key hint, now that a stored MAC can also be one no NIC ever had

    [Fact]
    public void DescribeStaleKey_MacBelongsOnlyToVirtualAdapters_SaysTheKeyIsAVirtualOne()
    {
        var rule = new NetworkLocationRule { AdapterMac = DefaultSwitchMac, IpCidr = "172.24.64.0/20" };

        Assert.True(NetworkLocationService.IsVirtualAdapterMac(DefaultSwitchMac, MeasuredPeers));
        Assert.Equal(NetworkLocationService.VirtualMacHint,
                     NetworkLocationService.DescribeStaleKey(rule, default, MeasuredPeers));
    }

    [Fact]
    public void DescribeStaleKey_MacSharedWithTheNicBehindASwitch_IsNotCalledVirtual()
    {
        // An external switch port inherits the NIC's address verbatim, so both adapters own that MAC
        // and a rule keyed on it still names a real one.
        var rule = new NetworkLocationRule { AdapterMac = PhysicalMac, IpCidr = "10.0.1.0/23" };

        Assert.False(NetworkLocationService.IsVirtualAdapterMac(PhysicalMac, MeasuredPeers));
        Assert.Null(NetworkLocationService.DescribeStaleKey(rule, default, MeasuredPeers));
    }

    [Fact]
    public void DescribeStaleKey_SameSubnetDifferentMac_StillSaysSo()
    {
        var rule    = new NetworkLocationRule { AdapterMac = DockMac, IpCidr = "10.0.0.0/23" };
        var current = new NetworkLocation(PhysicalMac, "10.0.0.0/23", true, "Ethernet");

        Assert.Equal(NetworkLocationService.StaleKeyHint,
                     NetworkLocationService.DescribeStaleKey(rule, current, MeasuredPeers));
    }

    [Fact]
    public void DescribeStaleKey_SoundKey_SaysNothing()
    {
        var rule    = new NetworkLocationRule { AdapterMac = PhysicalMac, IpCidr = "10.0.0.0/23" };
        var current = new NetworkLocation(PhysicalMac, "10.0.0.0/23", true, "Ethernet");

        Assert.Null(NetworkLocationService.DescribeStaleKey(rule, current, MeasuredPeers));
    }

    // The "current network" line is three-way, and the middle case is the one that matters: an
    // unmatched network must still name the adapter and subnet a profile would be keyed on.

    [Fact]
    public void DescribeCurrentNetwork_ProfileMatches_ShowsTheProfileName()
    {
        var location = new NetworkLocation("AA:BB:CC:DD:EE:FF", "10.0.0.0/23", true, "Ethernet");
        var rule     = new NetworkLocationRule { Name = "Office dock", AdapterMac = "AA:BB:CC:DD:EE:FF" };

        Assert.Equal("Office dock", NetworkLocationService.DescribeCurrentNetwork(location, rule));
    }

    [Fact]
    public void DescribeCurrentNetwork_NoProfile_ShowsTheDetectedAdapterAndSubnet()
    {
        // Fed from Detect rather than a hand-built location, so the line is pinned to the adapter
        // label the detection actually settles on.
        var dock     = Dock("192.168.1.0/24");
        var location = NetworkLocationService.Detect([dock], PeersOf(dock), (uint)dock.IPv4Index, () => null);

        Assert.Equal("Ethernet · 192.168.1.0/24",
                     NetworkLocationService.DescribeCurrentNetwork(location, null));
    }

    [Fact]
    public void DescribeCurrentNetwork_NoProfileOnMobile_ShowsTheAdapterAndNoSubnet()
    {
        // The subnet is not part of a mobile key, so naming it would describe a lease rather than
        // the place a new profile would be keyed on.
        var location = DetectOnMobile("100.110.83.0/24");

        Assert.Equal("Mobile", NetworkLocationService.DescribeCurrentNetwork(location, null));
    }

    [Fact]
    public void DescribeCurrentNetwork_NoProfileAndNoAdapterName_FallsBackToTheSubnet()
    {
        // SSID reads are best-effort, so a matched-nothing Wi-Fi location can arrive with no hint.
        var location = new NetworkLocation("AA:BB:CC:DD:EE:FF", "192.168.1.0/24", false, null);

        Assert.Equal("192.168.1.0/24", NetworkLocationService.DescribeCurrentNetwork(location, null));
    }

    [Fact]
    public void DescribeCurrentNetwork_NothingResolved_SaysNoNetworkDetected()
    {
        // Fail-closed, and a different statement from "no profile matches": nothing was detected at
        // all, so no profile could apply even if one existed.
        Assert.Equal(NetworkLocationService.NoNetworkDetected,
                     NetworkLocationService.DescribeCurrentNetwork(default, null));
    }

    // Mobile broadband, where the carrier lease rotates and the modem is the whole key

    // The modem straight off the routing table, with no tunnel in the way.
    private static NetworkLocation DetectOnMobile(string cidr)
    {
        var tether = Tether(cidr);
        return NetworkLocationService.Detect([tether], PeersOf(tether), (uint)tether.IPv4Index, () => null);
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Wwanpp)]
    [InlineData(NetworkInterfaceType.Wwanpp2)]
    public void IsMobile_MobileBroadbandTypes_AreMobile(NetworkInterfaceType type)
    {
        Assert.True(NetworkLocationService.IsMobile(type));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet)]
    [InlineData(NetworkInterfaceType.GigabitEthernet)]
    [InlineData(NetworkInterfaceType.Wireless80211)]
    [InlineData(NetworkInterfaceType.Wman)]
    public void IsMobile_EverythingThatCarriesALocationOtherwise_IsNot(NetworkInterfaceType type)
    {
        // Wman is mobile broadband too, but it is not among the types that resolve as a NIC at all,
        // so it can never carry a location and calling it mobile would say nothing.
        Assert.False(NetworkLocationService.IsMobile(type));
    }

    [Fact]
    public void Detect_OneModemOnTwoCarrierSubnets_IsOneLocation()
    {
        // The defect: measured on this machine the 5G modem landed on 100.110.83.0/24, inside CGNAT,
        // and the next attach lands elsewhere. With the subnet in the key a mobile profile saved once
        // never matched again.
        var first  = DetectOnMobile("100.110.83.0/24");
        var second = DetectOnMobile("100.72.14.0/22");

        Assert.Equal(MobileMac, first.AdapterMac);
        Assert.Equal(MobileMac, second.AdapterMac);
        Assert.True(first.SameLocationAs(second));
    }

    [Fact]
    public void Detect_MobileAdapter_KeepsNoSubnetToStore()
    {
        // A new rule copies the detected key verbatim, so dropping the subnet here is what stops one
        // being written.
        var location = DetectOnMobile("100.110.83.0/24");

        Assert.True(location.IsMobile);
        Assert.Null(location.IpCidr);
        Assert.False(location.IsEmpty);   // the MAC alone is a complete key
    }

    [Fact]
    public void Detect_WifiAdapter_IsNotMobileAndKeepsItsSubnet()
    {
        // The mirror of the bug: a home and a cabin behind one radio must not collapse.
        var wifi     = WifiCard("10.0.0.0/23");
        var location = NetworkLocationService.Detect([wifi], PeersOf(wifi), 9, () => "HomeWiFi");

        Assert.False(location.IsMobile);
        Assert.Equal("10.0.0.0/23", location.IpCidr);
    }

    [Fact]
    public void Detect_MobileBehindTheVpn_StillDropsTheSubnet()
    {
        // The tunnel holds the default route on this machine, so the mobile path has to survive the
        // walk-back down to the modem.
        var location = DetectBehindVpn(Tether("100.110.83.0/24"));

        Assert.Equal(MobileMac, location.AdapterMac);
        Assert.True(location.IsMobile);
        Assert.Null(location.IpCidr);
    }

    [Fact]
    public void DescribeMatchKey_Mobile_NamesTheModemInsteadOfASubnet()
    {
        // A rule saved before the subnet left the key still has one in the file; showing it would
        // name something the match never looks at.
        Assert.Equal($"MAC {MobileMac} · {NetworkLocationService.MobileSubnetNote}",
            NetworkLocationService.DescribeMatchKey(MobileMac, "100.110.83.0/24", subnetIgnored: true));
        Assert.Equal($"MAC {MobileMac} · {NetworkLocationService.MobileSubnetNote}",
            NetworkLocationService.DescribeMatchKey(MobileMac, null, subnetIgnored: true));
    }

    [Fact]
    public void DescribeMatchKey_SubnetOnlyRule_KeepsItsSubnetEvenOnMobile()
    {
        // Such a rule matches on its subnet or not at all, so the line must keep saying so.
        Assert.Equal("Subnet 10.0.0.0/23",
            NetworkLocationService.DescribeMatchKey(null, "10.0.0.0/23", subnetIgnored: true));
    }

    [Fact]
    public void IsStaleKey_OnMobile_SaysNothingAboutAMobileRule()
    {
        // The current location carries no subnet on mobile, so a rotating carrier lease can never
        // read as "same subnet, different MAC".
        var rule = new NetworkLocationRule { AdapterMac = MobileMac, IpCidr = "100.110.83.0/24" };

        Assert.False(NetworkLocationService.IsStaleKey(rule, DetectOnMobile("100.72.14.0/22")));
        Assert.Null(NetworkLocationService.DescribeStaleKey(rule, DetectOnMobile("100.72.14.0/22"), MeasuredPeers));
    }
}
