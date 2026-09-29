using System.Net.NetworkInformation;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class NetworkLocationServiceTests
{
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

    // NDIS filter layers, which GetAllNetworkInterfaces returns as interfaces of their own

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

    // Resolving the routed adapter down to the physical NIC. The fixtures are measured from this
    // machine: mobile broadband reports Wwanpp rather than Ethernet, and the interface metrics are the
    // ones Get-NetIPInterface shows.

    private const string WifiMac   = "30:89:4A:68:1C:3A";
    private const string MobileMac = "84:8D:B0:53:5F:55";
    private const string DockMac   = "3C:2C:30:CA:98:D7";

    // IsVirtual comes from the real predicate, so a fixture can never claim a nature the shipped
    // classification would not give it.
    private static AdapterCandidate Adapter(
        int index, NetworkInterfaceType type, string name, string description,
        string? mac, string? cidr, uint metric) =>
        new(index, NetworkLocationService.LooksVirtual(description), type, name, description, mac, cidr, metric);

    private static AdapterCandidate Tether(string cidr, uint metric = 35) =>
        Adapter(15, NetworkInterfaceType.Wwanpp, "Mobile", "5G Solution 5000", MobileMac, cidr, metric);

    // The external switch's port, holding the address while the NIC behind it holds none and lending
    // its hardware address verbatim — which is what makes the pairing possible at all.
    private static AdapterCandidate BridgedSwitchPort() =>
        Adapter(31, NetworkInterfaceType.Ethernet, BridgeAlias, BridgeDesc, PhysicalMac, "10.0.1.0/23", metric: 15);

    private static BridgePeer Peer(AdapterCandidate c) => new(c.Name, c.Mac, c.IsVirtual, OperationalStatus.Up);

    private static BridgePeer[] PeersOf(params AdapterCandidate[] candidates) => [.. candidates.Select(Peer)];

    // The modem straight off the routing table, with no tunnel in the way.
    private static NetworkLocation DetectOnMobile(string cidr)
    {
        var tether = Tether(cidr);
        return NetworkLocationService.Detect([tether], PeersOf(tether), (uint)tether.IPv4Index, () => null);
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
}
