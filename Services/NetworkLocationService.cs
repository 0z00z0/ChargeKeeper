using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ChargeKeeper.Services;

/// <summary>A configured network location. <see cref="AdapterMac"/>/<see cref="IpCidr"/> are the
/// match key; at least one must be set for a rule to ever match.</summary>
internal sealed class NetworkLocationRule
{
    public string  Name       { get; set; } = "";
    public string? AdapterMac { get; set; }
    public string? IpCidr     { get; set; }
    public string  PresetName { get; set; } = "";

    /// <summary>Hold the machine awake here; leaving is then the off switch.</summary>
    public bool KeepAwakeHere { get; set; }

    /// <summary>
    /// Whether the stored subnet plays no part in matching <paramref name="location"/>: this rule names
    /// the mobile adapter we are on, and a carrier lease rotates, so the modem is the whole key. A
    /// mobile rule that does carry a subnet still matches — the stored value is ignored, never
    /// migrated. Also what the Settings "Matches" line reads, so the displayed key says what is
    /// actually compared.
    /// </summary>
    internal bool SubnetIgnoredOn(NetworkLocation location) =>
        location.IsMobile && AdapterMac is not null && AdapterMac == location.AdapterMac;

    /// <summary>A rule with no MAC of its own still has to match on its subnet: dropping that on
    /// mobile would make it fit every mobile network.</summary>
    public bool Matches(NetworkLocation location) =>
        (AdapterMac is not null || IpCidr is not null) &&
        (AdapterMac is null || AdapterMac == location.AdapterMac) &&
        (IpCidr     is null || IpCidr     == location.IpCidr || SubnetIgnoredOn(location));
}

/// <summary>
/// Fingerprint of the physical adapter carrying the current connection, never of a tunnel or a switch
/// port above it (see <see cref="NetworkLocationService.ResolvePhysical"/>). <see cref="IpCidr"/> is
/// kept alongside <see cref="AdapterMac"/> because one Wi-Fi card reaches many places, and the subnet
/// is what tells them apart. On mobile it is null instead: the carrier assigns a rotating address, so
/// the modem is the location and its MAC is the whole key. <see cref="DisplayHint"/> (Wi-Fi SSID or
/// adapter name) is never part of matching — only a friendlier default when naming a rule.
/// </summary>
/// <param name="IsMobile">Mobile broadband, so the subnet is neither stored nor compared.</param>
internal readonly record struct NetworkLocation(
    string? AdapterMac, string? IpCidr, bool IsWired, string? DisplayHint, bool IsMobile = false)
{
    public bool IsEmpty => AdapterMac is null && IpCidr is null;

    /// <summary>
    /// Identity for change detection: the match keys only, not <see cref="DisplayHint"/>, which flaps
    /// because <c>TryGetWifiSsid</c> is best-effort. Comparing the whole record would re-fire
    /// <c>LocationChanged</c> and re-apply the preset on a hint flicker.
    /// </summary>
    public bool SameLocationAs(NetworkLocation other) =>
        AdapterMac == other.AdapterMac && IpCidr == other.IpCidr;
}

/// <summary>
/// What the adapter heuristics need of a live adapter, so they stay pure. <see cref="Metric"/> is the
/// IPv4 interface metric Windows routes by; <see cref="uint.MaxValue"/> means it could not be read, so
/// such an adapter sorts last. <see cref="IsVirtual"/> is a last-resort tiebreaker in
/// <see cref="NetworkLocationService.SelectPrimary"/>, never a reason to drop the routing-table winner.
/// </summary>
internal sealed record AdapterCandidate(
    int IPv4Index,
    bool IsVirtual,
    NetworkInterfaceType Type,
    string Name = "",
    string Description = "",
    string? Mac = null,
    string? IpCidr = null,
    uint Metric = uint.MaxValue,
    string? IpAddress = null);

/// <summary>
/// The physical adapter a location is keyed on. <see cref="Carrier"/> holds the IP, so it supplies the
/// subnet and the wired flag; <see cref="Bridged"/> is the NIC behind a Hyper-V external switch, which
/// then supplies the MAC and the suggested name.
/// </summary>
internal sealed record PhysicalRoute(AdapterCandidate Carrier, BridgePeer? Bridged);

/// <summary>
/// What the Hyper-V bridge walk-back below needs of a live adapter, so it is testable without a
/// Hyper-V host. <see cref="Mac"/> is null for pseudo-adapters with no 6-byte hardware address, which
/// disqualifies them as the NIC behind a switch.
/// </summary>
internal sealed record BridgePeer(string Name, string? Mac, bool IsVirtual, OperationalStatus Status,
                                  string? Description = null);

/// <summary>
/// How the resolved adapter describes itself: the Windows connection alias, the address it holds, and
/// the hardware name. Not part of the location key — a rule never matches on any of it — so it can
/// change without re-firing a location change.
/// </summary>
internal readonly record struct NetworkAdapterInfo(string? Alias, string? IpAddress, string? AdapterName)
{
    public bool IsEmpty => Alias is null && IpAddress is null && AdapterName is null;
}

/// <summary>
/// Detects the current network location by fingerprinting the physical adapter carrying the traffic,
/// by MAC address and IP subnet rather than by Wi-Fi SSID. That works identically for a docked
/// Ethernet connection and needs no WLAN capability declaration (this app is unpackaged).
/// </summary>
/// <remarks>
/// Windows' routing table (<c>GetBestInterface</c>) names the adapter traffic leaves by, but that
/// adapter is often not a NIC: a VPN tunnel, or a Hyper-V external switch, holds the default route and
/// lends its own MAC and subnet to every network reached through it. Keying on those collapses
/// tethering, Wi-Fi and a dock into one location. So the routed adapter is resolved down to the
/// physical NIC first, and both halves of the key come from that NIC.
/// </remarks>
internal static class NetworkLocationService
{
    // Coalesces a burst of NetworkChange events around one physical transition (dock/undock, Wi-Fi
    // roam) into a single re-evaluation: one dock rebind fires the OS events several times in quick
    // succession, and evaluating on each would flap the applied preset before settling.
    private const int DebounceMs = 1500;

    // Guards the timer re-arm + _last + the started/handler state; NetworkChange events fire
    // concurrently on several threads. LocationChanged is invoked outside the lock so a slow
    // subscriber (ApplyPreset) can't stall an incoming NetworkChange.
    private static readonly System.Threading.Lock _sync = new();
    private static System.Threading.Timer? _debounceTimer;
    private static NetworkLocation _last;
    private static NetworkAdapterInfo _lastAdapter;
    private static bool _started;

    // Whether _last holds a real reading yet. The first evaluation after Start() only seeds it —
    // treating that as a change re-applies a network profile on every app start, cancelling an
    // in-flight travel override.
    private static bool _seeded;

    // Held so Stop() can actually unsubscribe; an anonymous lambda could never be removed.
    private static NetworkAddressChangedEventHandler? _addressChangedHandler;
    private static NetworkAvailabilityChangedEventHandler? _availabilityChangedHandler;

    /// <summary>Raised (off the UI thread, after the debounce settles) whenever the detected location changes.</summary>
    public static event Action<NetworkLocation>? LocationChanged;

    /// <summary>The cheap read for status display, with no adapter enumeration. Empty until the
    /// first post-<see cref="Start"/> evaluation lands.</summary>
    public static NetworkLocation LastKnown { get { lock (_sync) return _last; } }

    /// <summary>How the last-detected adapter describes itself. Refreshed on every evaluation, not
    /// only on a location change: an alias or a lease can change without the match key moving.</summary>
    public static NetworkAdapterInfo LastKnownAdapter { get { lock (_sync) return _lastAdapter; } }

    /// <summary>A real location change, rather than the first baseline seed. Pure, so testable.</summary>
    internal static bool IsLocationChange(bool seeded, NetworkLocation current, NetworkLocation last) =>
        seeded && !current.SameLocationAs(last);

    public static void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
            // One timer for the service's lifetime, re-armed per event via Change().
            _debounceTimer = new System.Threading.Timer(_ => Evaluate(), null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _addressChangedHandler      = (_, _) => ScheduleEvaluate();
            _availabilityChangedHandler = (_, _) => ScheduleEvaluate();
            NetworkChange.NetworkAddressChanged      += _addressChangedHandler;
            NetworkChange.NetworkAvailabilityChanged += _availabilityChangedHandler;
        }
        ScheduleEvaluate();   // outside the lock; it takes _sync itself
    }

    public static void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            _seeded  = false;   // a later Start() re-seeds instead of firing off a stale baseline
            if (_addressChangedHandler is not null)      NetworkChange.NetworkAddressChanged      -= _addressChangedHandler;
            if (_availabilityChangedHandler is not null) NetworkChange.NetworkAvailabilityChanged -= _availabilityChangedHandler;
            _addressChangedHandler = null;
            _availabilityChangedHandler = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private static void ScheduleEvaluate()
    {
        lock (_sync)
        {
            if (!_started) return;   // a NetworkChange racing Stop() must not re-arm the timer
            _debounceTimer?.Change(DebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    private static void Evaluate()
    {
        try
        {
            var (current, adapter) = DetectCurrentDetailed();
            bool changed;
            lock (_sync)
            {
                // Stored before the early return: the alias and the lease can move without the match
                // key moving, and the status entities read this.
                _lastAdapter = adapter;
                changed = IsLocationChange(_seeded, current, _last);
                if (_seeded && !changed) return;
                _seeded = true;
                _last   = current;
            }
            if (changed) LocationChanged?.Invoke(current);
        }
        catch (Exception ex)
        {
            AppLog.Error("NetworkLocationService.Evaluate", ex);
        }
    }

    /// <summary>The "current network" status line. Prefers <see cref="LastKnown"/>, reading live only
    /// when that is empty. Safe off the UI thread.</summary>
    public static string DescribeCurrentLocation()
    {
        var location = LastKnown;
        if (location.IsEmpty) location = DetectCurrent();
        return DescribeCurrentNetwork(location, SettingsService.Current.FindNetworkRule(location));
    }

    /// <summary>Nothing resolved: the fail-closed reading, and a different thing from a network that
    /// resolved but matches no profile.</summary>
    internal const string NoNetworkDetected = "No network detected";

    /// <summary>
    /// The three-way "current network" line, pure so the choice is testable: the matching profile's
    /// name; failing that, what was actually detected, since the adapter a new profile would be keyed
    /// on has to be visible before anyone creates one; failing that, <see cref="NoNetworkDetected"/>.
    /// </summary>
    internal static string DescribeCurrentNetwork(NetworkLocation location, NetworkLocationRule? matched)
    {
        if (location.IsEmpty)    return NoNetworkDetected;
        if (matched is not null) return matched.Name;

        // DisplayHint is the adapter alias or SSID the detection already settled on; pairing it with
        // the subnet is what tells two places on one adapter apart. With neither, only the match key
        // is left, and DescribeMatchKey is already its formatter.
        string? hint = location.DisplayHint is { Length: > 0 } h ? h : null;
        if (hint is null) return location.IpCidr ?? DescribeMatchKey(location.AdapterMac, null, location.IsMobile);
        return location.IpCidr is { } cidr ? $"{hint} · {cidr}" : hint;
    }

    /// <summary>Stands in for the subnet on mobile, where the key is the modem alone. Stated rather
    /// than left blank: a stored subnet is still in the file, and showing it would name something the
    /// match never looks at.</summary>
    internal const string MobileSubnetNote = "Mobile — any subnet";

    /// <summary>The match key as "MAC … · Subnet …". One formatter for the Settings "Matches" line
    /// and the naming dialog. <paramref name="subnetIgnored"/> is the mobile case, and only applies
    /// with a MAC to fall back on.</summary>
    internal static string DescribeMatchKey(string? adapterMac, string? ipCidr, bool subnetIgnored = false)
    {
        var parts = new List<string>();
        if (adapterMac is { } mac)  parts.Add($"MAC {mac}");
        if (subnetIgnored && adapterMac is not null) parts.Add(MobileSubnetNote);
        else if (ipCidr is { } cidr) parts.Add($"Subnet {cidr}");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No match key — this profile will never apply.";
    }

    /// <summary>Reads the current location synchronously — the tray's "Add configuration for this
    /// network" needs an up-to-the-moment reading, not the last debounced one.</summary>
    public static NetworkLocation DetectCurrent() => DetectCurrentDetailed().Location;

    /// <summary>The same reading with the resolved adapter's own description alongside it.</summary>
    public static (NetworkLocation Location, NetworkAdapterInfo Adapter) DetectCurrentDetailed()
    {
        try
        {
            // The enumerations must stay INSIDE the try: they touch adapter properties, which throw
            // during the dock/undock race this catch exists for, and the synchronous UI caller has no
            // guard of its own.
            return DetectDetailed(EnumerateCandidates(), EnumerateAdapters(), GetBestInterfaceIndex(), TryGetWifiSsid);
        }
        catch
        {
            // The adapter, or its enumeration, can vanish mid-read during a dock/undock transition.
            return (default, default);
        }
    }

    /// <summary>
    /// The whole detection over supplied adapter state, so it is testable without live adapters: the
    /// routing table names the adapter traffic leaves by, that adapter is resolved down to the physical
    /// NIC behind it, and the key comes from that NIC. Resolving nothing yields the empty location
    /// rather than a guess — a wrong location applies the wrong charge thresholds silently.
    /// </summary>
    internal static NetworkLocation Detect(
        IReadOnlyList<AdapterCandidate> candidates,
        IReadOnlyList<BridgePeer> peers,
        uint bestIndex,
        Func<string?> readSsid) => DetectDetailed(candidates, peers, bestIndex, readSsid).Location;

    /// <summary>The detection above, also returning how the resolved adapter describes itself. One
    /// resolution feeds both, so the description can never belong to a different adapter from the one
    /// the key identifies.</summary>
    internal static (NetworkLocation Location, NetworkAdapterInfo Adapter) DetectDetailed(
        IReadOnlyList<AdapterCandidate> candidates,
        IReadOnlyList<BridgePeer> peers,
        uint bestIndex,
        Func<string?> readSsid)
    {
        if (ResolvePhysical(SelectPrimary(candidates, bestIndex), candidates, peers) is not { } route)
            return (default, default);

        bool mobile  = IsMobile(route.Carrier.Type);
        bool wired   = route.Carrier.Type != NetworkInterfaceType.Wireless80211;
        string? ssid = wired ? null : readSsid();
        var location = Compose(route.Carrier.Mac ?? "", route.Carrier.IpCidr, wired, route.Bridged,
                               SuggestDisplayHint(wired, route.Carrier.Name, route.Bridged, ssid), mobile);
        return (location, ComposeAdapter(route));
    }

    /// <summary>
    /// How the resolved adapter describes itself. Alias and hardware name come from the physical NIC
    /// the walk-back settled on, so a Hyper-V switch port reports the card behind it rather than
    /// "vEthernet (…)". The address stays with the carrier, which is the adapter that actually holds
    /// one — a NIC bound behind an external switch keeps none.
    /// </summary>
    internal static NetworkAdapterInfo ComposeAdapter(PhysicalRoute route) => new(
        Alias:       Blank(route.Bridged?.Name ?? route.Carrier.Name),
        IpAddress:   Blank(route.Carrier.IpAddress),
        AdapterName: Blank(route.Bridged?.Description ?? route.Carrier.Description));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Every Up adapter owning a usable IPv4 address. Not restricted to physical types or non-virtual
    // descriptions: on a Hyper-V external switch the routable IP and default route live on the
    // "vEthernet (…)" adapter while the bridged NIC keeps no IP, so dropping the virtual ones here
    // would leave nothing to walk down from.
    private static List<AdapterCandidate> EnumerateCandidates() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => !IsFilterInterface(n.Name, n.Description))
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(Describe)
            .Where(c => c.IpCidr is not null)
            .ToList();

    // Never throws: GetIPProperties can fail mid-enumeration, and an adapter with no readable subnet
    // is dropped by the caller.
    private static AdapterCandidate Describe(NetworkInterface n)
    {
        int index       = -1;
        string? cidr    = null;
        string? address = null;
        try
        {
            var props = n.GetIPProperties();
            index = props.GetIPv4Properties()?.Index ?? -1;
            // A NIC commonly holds an APIPA address alongside a real lease, and keying on
            // 169.254.0.0/16 would match any link-local network and never the real subnet.
            if (props.UnicastAddresses.FirstOrDefault(a => IsUsableIPv4(a.Address)) is { } ipv4)
            {
                cidr = CalculateCidr(ipv4.Address, ipv4.IPv4Mask);
                address = ipv4.Address.ToString();
            }
        }
        catch { }

        return new AdapterCandidate(index, LooksVirtual(n.Description), n.NetworkInterfaceType,
                                    n.Name, n.Description, MacOrNull(n), cidr, ReadInterfaceMetric(index),
                                    address);
    }

    /// <summary>
    /// Pure selection heuristic behind <see cref="FindPrimaryAdapter"/>, extracted so the
    /// bridge-vs-physical decision is testable without live adapters. The authoritative pick is the
    /// candidate whose IPv4 index equals <paramref name="bestIndex"/>. Only when GetBestInterface is
    /// unavailable (0) or names no candidate does it fall back to the preference order below, where
    /// "Virtual" is a last-resort tiebreaker that never overrides the routing-table winner.
    /// </summary>
    internal static AdapterCandidate? SelectPrimary(IReadOnlyList<AdapterCandidate> candidates, uint bestIndex)
    {
        if (candidates.Count == 0) return null;

        if (bestIndex != 0)
        {
            var byIndex = candidates.FirstOrDefault(c => c.IPv4Index == bestIndex);
            if (byIndex is not null) return byIndex;
        }

        return candidates.FirstOrDefault(c => !c.IsVirtual && c.Type == NetworkInterfaceType.Ethernet)
            ?? candidates.FirstOrDefault(c => !c.IsVirtual && c.Type == NetworkInterfaceType.Wireless80211)
            ?? candidates.FirstOrDefault(c => c.Type == NetworkInterfaceType.Ethernet)
            ?? candidates[0];
    }

    // Adapter types a NIC actually reports. Measured on this hardware: mobile broadband is Wwanpp,
    // not Ethernet, and a WireGuard tunnel is IANA ifType 53 (propVirtual), which the enum has no
    // member for at all — so the type test alone already rejects it.
    private static readonly NetworkInterfaceType[] PhysicalTypes =
    [
        NetworkInterfaceType.Ethernet, NetworkInterfaceType.GigabitEthernet,
        NetworkInterfaceType.FastEthernetT, NetworkInterfaceType.FastEthernetFx,
        NetworkInterfaceType.Wireless80211, NetworkInterfaceType.Wwanpp, NetworkInterfaceType.Wwanpp2,
    ];

    // Mobile broadband, where the carrier hands out a rotating address from the CGNAT range: the
    // subnet is a lease, not a place. The enum's third mobile member, Wman (WiMax), is deliberately
    // absent — it is not in PhysicalTypes either, so it can never carry a location, and adding it
    // here alone would say nothing.
    private static readonly NetworkInterfaceType[] MobileTypes =
    [
        NetworkInterfaceType.Wwanpp, NetworkInterfaceType.Wwanpp2,
    ];

    /// <summary>Whether an adapter type is mobile broadband, where the card is the location because
    /// there is one mobile network reached through one modem.</summary>
    internal static bool IsMobile(NetworkInterfaceType type) => MobileTypes.Contains(type);

    // Description markers for adapters that are not a NIC. Only the description is matched, never the
    // alias: the alias is user-editable, and every measured case ("WireGuard Tunnel", "PANGP Virtual
    // Ethernet Adapter Secure", "Hyper-V Virtual Ethernet Adapter", "Microsoft Wi-Fi Direct Virtual
    // Adapter") is already named by the driver.
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vpn", "tunnel", "tap-windows", "tap adapter", "wintun", "wireguard", "tailscale",
        "zerotier", "anyconnect", "openvpn", "pangp", "vmware", "virtualbox", "docker", "loopback",
    ];

    /// <summary>Whether an adapter's driver description marks it as a tunnel, a VPN client, a
    /// hypervisor switch port or another pseudo-adapter rather than a NIC.</summary>
    internal static bool LooksVirtual(string? description) =>
        description is { Length: > 0 } &&
        VirtualMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));

    // Markers of an NDIS filter pseudo-interface, which is a driver layer bound to an adapter rather
    // than an adapter of its own. Multiplexor is the Windows "Bridge Connections" adapter, a bridge
    // over NICs rather than one of them.
    private static readonly string[] FilterMarkers =
    [
        "-WFP ", "LightWeight Filter", "-NDIS ", "Multiplexor",
    ];

    /// <summary>
    /// Whether an interface is an NDIS filter layer rather than an adapter. Both the alias and the
    /// description are tested, because the filter's suffix is appended to whichever of the two the
    /// stack names it by. Every one of these clones its host adapter's hardware address, so leaving
    /// them in doubles each same-MAC set and the bridge walk-back below ties instead of resolving —
    /// measured, six interfaces on one MAC where <c>Get-NetAdapter</c> shows two. <see
    /// cref="NetworkInterface.GetAllNetworkInterfaces"/> has returned them since .NET 5.
    /// </summary>
    internal static bool IsFilterInterface(string? name, string? description) =>
        HasFilterMarker(name) || HasFilterMarker(description);

    private static bool HasFilterMarker(string? value) =>
        value is { Length: > 0 } &&
        FilterMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>A real NIC: a physical adapter type, its own 6-byte hardware address, and no virtual
    /// marker in its description.</summary>
    internal static bool IsPhysical(AdapterCandidate candidate) =>
        PhysicalTypes.Contains(candidate.Type) && candidate.Mac is { Length: > 0 } && !candidate.IsVirtual;

    /// <summary>
    /// Resolves the adapter the routing table picked down to the physical NIC carrying its traffic.
    /// A NIC resolves to itself; a Hyper-V external switch port resolves to the NIC bound behind it.
    /// A tunnel or VPN adapter resolves to nothing of its own, so the walk falls back to the live
    /// adapter with the lowest interface metric — the one Windows itself would route over, which is
    /// what separates a dock from a simultaneously-connected Wi-Fi radio. Null when nothing resolves.
    /// </summary>
    internal static PhysicalRoute? ResolvePhysical(
        AdapterCandidate? routed, IReadOnlyList<AdapterCandidate> candidates, IReadOnlyList<BridgePeer> peers)
    {
        if (routed is not null)
        {
            if (ResolveCarrier(routed, peers) is { } direct) return direct;

            // A switch port carries one specific NIC's traffic, so failing to name that NIC means the
            // name is unknown — not that some other adapter carries it. Scanning on from here keyed a
            // docked machine on its idle mobile modem, a different place with different thresholds, so
            // the reading is given up instead. A tunnel is the opposite case and still scans: it has no
            // uplink of its own, and the metric order is what finds the NIC underneath it.
            if (LooksLikeHyperVSwitchPort(routed.Name, routed.Description)) return null;
        }

        return candidates
            .OrderBy(c => c.Metric)
            // Equal metrics are a genuine tie; wired first, then interface index, so one order-free
            // adapter list always yields the same answer.
            .ThenBy(c => c.Type == NetworkInterfaceType.Wireless80211)
            .ThenBy(c => c.IPv4Index)
            .Select(c => ResolveCarrier(c, peers))
            .FirstOrDefault(r => r is not null);
    }

    private static PhysicalRoute? ResolveCarrier(AdapterCandidate candidate, IReadOnlyList<BridgePeer> peers)
    {
        if (IsPhysical(candidate)) return new PhysicalRoute(candidate, null);
        var bridged = ResolveBridgedPeer(candidate.Name, candidate.Description, candidate.Mac, peers);
        return bridged is { Mac.Length: > 0 }
            ? new PhysicalRoute(candidate, bridged)
            : DegradeSwitchPort(candidate, peers);
    }

    /// <summary>Neutral adapter labels, for a resolved adapter with no name worth showing. A switch
    /// port's "vEthernet (…)" alias names the switch rather than the place, so it is never one of
    /// them.</summary>
    internal const string WiredLabel    = "Wired network";
    internal const string WirelessLabel = "Wireless network";
    internal const string MobileLabel   = "Mobile network";

    /// <summary>The neutral label for an adapter type.</summary>
    internal static string DescribeAdapterType(NetworkInterfaceType type) =>
        IsMobile(type)                               ? MobileLabel
        : type == NetworkInterfaceType.Wireless80211 ? WirelessLabel
                                                     : WiredLabel;

    /// <summary>
    /// A switch port whose uplink could not be named, but whose hardware address another interface
    /// also carries — which only happens because an external switch clones the bound NIC's address
    /// verbatim. The key is the same bytes whichever of the two is named, so it is kept; the name is
    /// the only unknown, and a neutral one stands in rather than an alias that names the switch.
    /// A port whose address pairs with nothing has no such backing: that is a switch recreated on a
    /// Microsoft-OUI address, which changes again on the next recreation, so it resolves to nothing.
    /// </summary>
    private static PhysicalRoute? DegradeSwitchPort(AdapterCandidate port, IReadOnlyList<BridgePeer> peers)
    {
        if (!LooksLikeHyperVSwitchPort(port.Name, port.Description)) return null;
        if (port.Mac is not { Length: > 0 } mac) return null;
        if (!peers.Any(p => p.Mac == mac && !string.Equals(p.Name, port.Name, StringComparison.Ordinal)))
            return null;

        string label = DescribeAdapterType(port.Type);
        return new PhysicalRoute(port, new BridgePeer(label, mac, IsVirtual: false, OperationalStatus.Up, label));
    }

    // The Hyper-V bridge walk-back. The routing table picks the "vEthernet (…)" vNIC, but that is the
    // wrong thing to REMEMBER a network by: its address is the physical NIC's only while the switch
    // keeps it, and recreating the switch gives the vNIC a Microsoft-OUI address, silently breaking a
    // stored profile with no hardware change at all. So the physical NIC behind the switch supplies
    // the stored MAC and the suggested name; subnet and IsWired stay with the selected adapter.

    /// <summary>
    /// <paramref name="cidr"/> and <paramref name="wired"/> come from the selected adapter; the stored
    /// MAC comes from <paramref name="bridged"/> when the pairing resolved a physical NIC, and from
    /// the selected adapter on every other path. On <paramref name="mobile"/> the subnet is dropped
    /// rather than stored: a carrier lease rotates, so keeping it would key the location on something
    /// that never comes back.
    /// </summary>
    internal static NetworkLocation Compose(
        string selectedMac, string? cidr, bool wired, BridgePeer? bridged, string? suggestedName,
        bool mobile = false)
    {
        string mac = bridged?.Mac ?? selectedMac;
        return new(mac.Length > 0 ? mac : null, mobile ? null : cidr, wired, suggestedName, mobile);
    }

    /// <summary>
    /// Recognised by the two strings Windows fixes for these: the "Hyper-V Virtual Ethernet Adapter"
    /// description and the "vEthernet (&lt;switch&gt;)" alias. Gates the pairing below, so a machine
    /// without Hyper-V pays two string comparisons and no second enumeration.
    /// </summary>
    internal static bool LooksLikeHyperVSwitchPort(string? name, string? description) =>
        description?.StartsWith("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase) == true ||
        name?.StartsWith("vEthernet (", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Pairing is by MAC, since an external switch's host vNIC inherits the bound adapter's hardware
    /// address verbatim — no WMI, no Hyper-V module, no elevation. Returns null when nothing pairs,
    /// and the caller then keeps the selected adapter's own MAC. Virtual adapters are excluded as
    /// partners so one vNIC never stands in for another.
    /// </summary>
    internal static BridgePeer? ResolveBridgedPeer(
        string? alias, string? description, string? switchPortMac, IReadOnlyList<BridgePeer> peers)
    {
        if (!LooksLikeHyperVSwitchPort(alias, description)) return null;
        if (switchPortMac is not { Length: > 0 }) return null;

        var paired = peers.Where(p => p.Mac == switchPortMac && !p.IsVirtual).ToList();
        if (paired.Count <= 1) return paired.FirstOrDefault();

        // Several partners carry that address, so which NIC drives the switch is genuinely unknown —
        // but every one of them carries the same address, which is the whole key, so the choice moves
        // only the name shown. Present beats a disabled namesake, then ordinal alias order, so one
        // unordered adapter list always names the same NIC instead of giving up and showing the
        // switch port's own "vEthernet (…)" alias.
        return paired
            .OrderBy(p => p.Status == OperationalStatus.Up ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .First();
    }

    /// <summary>The SSID on Wi-Fi, otherwise the physical NIC behind the switch when one resolved,
    /// falling back to the selected adapter's own alias.</summary>
    internal static string? SuggestDisplayHint(bool wired, string alias, BridgePeer? bridged, string? ssid) =>
        wired ? bridged?.Name ?? alias : ssid;

    /// <summary>
    /// A stored key that can no longer match: same subnet as now, different MAC — what a new dock or
    /// a recreated Hyper-V switch leaves behind. Advisory only, and never rewritten, because the same
    /// reading also fits a genuinely different network on the same private subnet. Never fires on
    /// mobile: the current location carries no subnet there, so there is nothing to compare and a
    /// rotating carrier lease would otherwise read as a moved dock.
    /// </summary>
    internal static bool IsStaleKey(NetworkLocationRule rule, NetworkLocation current) =>
        rule.IpCidr     is not null && rule.IpCidr    == current.IpCidr &&
        rule.AdapterMac is not null && current.AdapterMac is not null &&
        rule.AdapterMac != current.AdapterMac;

    /// <summary>
    /// A stored MAC that belongs to a virtual adapter on this machine — a VPN tunnel or a switch port,
    /// which several networks share. Such a key was written before locations were resolved down to the
    /// physical NIC, and matching it is worse than not matching: it fits every place reached over that
    /// tunnel.
    /// </summary>
    internal static bool IsVirtualAdapterMac(string? mac, IReadOnlyList<BridgePeer> adapters)
    {
        if (mac is not { Length: > 0 }) return false;
        var owners = adapters.Where(a => a.Mac == mac).ToList();
        return owners.Count > 0 && owners.All(a => a.IsVirtual);
    }

    /// <summary>Wording for the <see cref="IsStaleKey"/> hint, shown under the rule's match key.</summary>
    internal const string StaleKeyHint =
        "Same subnet as the current network, but a different MAC — this profile will not apply here.";

    /// <inheritdoc cref="IsVirtualAdapterMac"/>
    internal const string VirtualMacHint =
        "Keyed on a virtual adapter, which several networks share — save this location again to key it "
        + "on the physical adapter.";

    /// <summary>The one hint that fits, or null when the stored key is sound. The virtual-adapter case
    /// is stated first: it explains a key that matches too much, which the subnet comparison cannot
    /// see.</summary>
    internal static string? DescribeStaleKey(
        NetworkLocationRule rule, NetworkLocation current, IReadOnlyList<BridgePeer> adapters) =>
        IsVirtualAdapterMac(rule.AdapterMac, adapters) ? VirtualMacHint
        : IsStaleKey(rule, current)                    ? StaleKeyHint
        : null;

    /// <summary>
    /// Every adapter, on a looser filter than <c>EnumerateCandidates</c>: the NIC behind an external
    /// switch is Up with no IPv4 at all, and OperationalStatus is not filtered either, so a disabled
    /// same-MAC namesake stays visible to <see cref="ResolveBridgedPeer"/> and can be ranked below the
    /// present one rather than silently winning. Also what the Settings page checks a stored MAC
    /// against.
    /// </summary>
    internal static List<BridgePeer> EnumerateAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => !IsFilterInterface(n.Name, n.Description))
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => new BridgePeer(
                n.Name,
                MacOrNull(n),
                LooksVirtual(n.Description),
                n.OperationalStatus,
                n.Description))
            .ToList();

    // The adapter's MAC in the stored format, or null when it has no real 6-byte hardware address —
    // WAN miniports report an empty one, and treating those as equal would pair a switch port with a
    // non-NIC.
    private static string? MacOrNull(NetworkInterface n)
    {
        try
        {
            var address = n.GetPhysicalAddress();
            return address.GetAddressBytes().Length == 6 ? NormalizeMac(address.ToString()) : null;
        }
        catch { return null; }
    }

    private static bool IsUsableIPv4(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(addr)) return false;
        var b = addr.GetAddressBytes();
        return !(b[0] == 169 && b[1] == 254);   // 169.254.0.0/16 APIPA / link-local
    }

    // The interface metric Windows routes by. Neither IPInterfaceProperties nor IPv4InterfaceProperties
    // exposes it, so it comes from iphlpapi; uint.MaxValue means "not read", which sorts last.
    private static uint ReadInterfaceMetric(int ipv4Index)
    {
        if (ipv4Index <= 0) return uint.MaxValue;
        try
        {
            var row = new MibIpInterfaceRow
            {
                Family         = (ushort)AddressFamily.InterNetwork,
                InterfaceIndex = (uint)ipv4Index,
                Metric         = 0,
            };
            return GetIpInterfaceEntry(ref row) == 0 ? row.Metric : uint.MaxValue;
        }
        catch { return uint.MaxValue; }
    }

    // MIB_IPINTERFACE_ROW (iphlpapi.h), 168 bytes on x64. Only three fields are touched, but the whole
    // row must be allocated because GetIpInterfaceEntry fills all of it. Offsets verified against
    // Get-NetIPInterface on this machine.
    [StructLayout(LayoutKind.Explicit, Size = 168)]
    private struct MibIpInterfaceRow
    {
        [FieldOffset(0)]   public ushort Family;
        [FieldOffset(16)]  public uint   InterfaceIndex;
        [FieldOffset(148)] public uint   Metric;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    private static uint GetBestInterfaceIndex()
    {
        try
        {
            // 8.8.8.8 is only a routing-table probe target; no packet is sent.
            uint dest = BitConverter.ToUInt32([8, 8, 8, 8], 0);
            return GetBestInterface(dest, out uint index) == 0 ? index : 0;
        }
        catch { return 0; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    internal static string NormalizeMac(string raw) => raw.Length == 12
        ? string.Join(":", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))).ToUpperInvariant()
        : raw;

    internal static string CalculateCidr(IPAddress address, IPAddress mask)
    {
        int prefixLen = mask.GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b));
        var addrBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var network   = new byte[4];
        for (int i = 0; i < 4; i++) network[i] = (byte)(addrBytes[i] & maskBytes[i]);
        return $"{new IPAddress(network)}/{prefixLen}";
    }

    // Best-effort: reading the SSID normally wants the WLAN API or a packaged app's capability-gated
    // WinRT surface, and this app is unpackaged. Only feeds the suggested NAME, so a failure here
    // costs a less helpful default, never a broken match.
    private static string? TryGetWifiSsid()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            return profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
        }
        catch
        {
            return null;
        }
    }
}
