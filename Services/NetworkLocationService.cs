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

    public bool Matches(NetworkLocation location) =>
        (AdapterMac is not null || IpCidr is not null) &&
        (AdapterMac is null || AdapterMac == location.AdapterMac) &&
        (IpCidr     is null || IpCidr     == location.IpCidr);
}

/// <summary>
/// Fingerprint of the physical adapter carrying the current connection, never of a tunnel or a switch
/// port above it (see <see cref="NetworkLocationService.ResolvePhysical"/>). <see cref="IpCidr"/> is
/// kept alongside <see cref="AdapterMac"/> because one Wi-Fi card reaches many places, and the subnet
/// is what tells them apart. <see cref="DisplayHint"/> (Wi-Fi SSID or adapter name) is never part of
/// matching — only a friendlier default when naming a rule.
/// </summary>
internal readonly record struct NetworkLocation(string? AdapterMac, string? IpCidr, bool IsWired, string? DisplayHint)
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
    uint Metric = uint.MaxValue);

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
internal sealed record BridgePeer(string Name, string? Mac, bool IsVirtual, OperationalStatus Status);

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
            var current = DetectCurrent();
            bool changed;
            lock (_sync)
            {
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

    /// <summary>The "current network" status line: the matching rule's name, or a fallback. Prefers
    /// <see cref="LastKnown"/>, reading live only when that is empty. Safe off the UI thread.</summary>
    public static string DescribeCurrentLocation()
    {
        var location = LastKnown;
        if (location.IsEmpty) location = DetectCurrent();
        if (location.IsEmpty) return "No network detected";
        var rule = SettingsService.Current.FindNetworkRule(location);
        return rule is not null ? rule.Name : "Unrecognised network";
    }

    /// <summary>The match key as "MAC … · Subnet …". One formatter for the Settings "Matches" line
    /// and the naming dialog.</summary>
    internal static string DescribeMatchKey(string? adapterMac, string? ipCidr)
    {
        var parts = new List<string>();
        if (adapterMac is { } mac)  parts.Add($"MAC {mac}");
        if (ipCidr     is { } cidr) parts.Add($"Subnet {cidr}");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No match key — this profile will never apply.";
    }

    /// <summary>Reads the current location synchronously — the tray's "Add configuration for this
    /// network" needs an up-to-the-moment reading, not the last debounced one.</summary>
    public static NetworkLocation DetectCurrent()
    {
        try
        {
            // The enumerations must stay INSIDE the try: they touch adapter properties, which throw
            // during the dock/undock race this catch exists for, and the synchronous UI caller has no
            // guard of its own.
            return Detect(EnumerateCandidates(), EnumerateAdapters(), GetBestInterfaceIndex(), TryGetWifiSsid);
        }
        catch
        {
            // The adapter, or its enumeration, can vanish mid-read during a dock/undock transition.
            return default;
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
        Func<string?> readSsid)
    {
        if (ResolvePhysical(SelectPrimary(candidates, bestIndex), candidates, peers) is not { } route)
            return default;

        bool wired   = route.Carrier.Type != NetworkInterfaceType.Wireless80211;
        string? ssid = wired ? null : readSsid();
        // One resolution feeds both the stored MAC and the suggested name, so the name can never
        // describe a different adapter from the one the key identifies.
        return Compose(route.Carrier.Mac ?? "", route.Carrier.IpCidr, wired, route.Bridged,
                       SuggestDisplayHint(wired, route.Carrier.Name, route.Bridged, ssid));
    }

    // Every Up adapter owning a usable IPv4 address. Not restricted to physical types or non-virtual
    // descriptions: on a Hyper-V external switch the routable IP and default route live on the
    // "vEthernet (…)" adapter while the bridged NIC keeps no IP, so dropping the virtual ones here
    // would leave nothing to walk down from.
    private static List<AdapterCandidate> EnumerateCandidates() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(Describe)
            .Where(c => c.IpCidr is not null)
            .ToList();

    // Never throws: GetIPProperties can fail mid-enumeration, and an adapter with no readable subnet
    // is dropped by the caller.
    private static AdapterCandidate Describe(NetworkInterface n)
    {
        int index    = -1;
        string? cidr = null;
        try
        {
            var props = n.GetIPProperties();
            index = props.GetIPv4Properties()?.Index ?? -1;
            // A NIC commonly holds an APIPA address alongside a real lease, and keying on
            // 169.254.0.0/16 would match any link-local network and never the real subnet.
            if (props.UnicastAddresses.FirstOrDefault(a => IsUsableIPv4(a.Address)) is { } ipv4)
                cidr = CalculateCidr(ipv4.Address, ipv4.IPv4Mask);
        }
        catch { }

        return new AdapterCandidate(index, LooksVirtual(n.Description), n.NetworkInterfaceType,
                                    n.Name, n.Description, MacOrNull(n), cidr, ReadInterfaceMetric(index));
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
        if (routed is not null && ResolveCarrier(routed, peers) is { } direct) return direct;

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
        return bridged is { Mac.Length: > 0 } ? new PhysicalRoute(candidate, bridged) : null;
    }

    // The Hyper-V bridge walk-back. The routing table picks the "vEthernet (…)" vNIC, but that is the
    // wrong thing to REMEMBER a network by: its address is the physical NIC's only while the switch
    // keeps it, and recreating the switch gives the vNIC a Microsoft-OUI address, silently breaking a
    // stored profile with no hardware change at all. So the physical NIC behind the switch supplies
    // the stored MAC and the suggested name; subnet and IsWired stay with the selected adapter.

    /// <summary>
    /// <paramref name="cidr"/> and <paramref name="wired"/> come from the selected adapter; the stored
    /// MAC comes from <paramref name="bridged"/> when the pairing resolved a physical NIC, and from
    /// the selected adapter on every other path.
    /// </summary>
    internal static NetworkLocation Compose(
        string selectedMac, string? cidr, bool wired, BridgePeer? bridged, string? suggestedName)
    {
        string mac = bridged?.Mac ?? selectedMac;
        return new(mac.Length > 0 ? mac : null, cidr, wired, suggestedName);
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
    /// address verbatim — no WMI, no Hyper-V module, no elevation. Returns null whenever the pairing
    /// is ambiguous, and the caller then keeps the selected adapter's own MAC and alias. Virtual
    /// adapters are excluded as partners so one vNIC never stands in for another.
    /// </summary>
    internal static BridgePeer? ResolveBridgedPeer(
        string? alias, string? description, string? switchPortMac, IReadOnlyList<BridgePeer> peers)
    {
        if (!LooksLikeHyperVSwitchPort(alias, description)) return null;
        if (switchPortMac is not { Length: > 0 }) return null;

        var paired = peers.Where(p => p.Mac == switchPortMac && !p.IsVirtual).ToList();
        if (paired.Count <= 1) return paired.FirstOrDefault();

        // Prefer the partner that is present over a disabled namesake; still tied means we cannot
        // tell which NIC drives this switch, and a wrong key is worse than the vNIC's.
        var present = paired.Where(p => p.Status == OperationalStatus.Up).ToList();
        return present.Count == 1 ? present[0] : null;
    }

    /// <summary>The SSID on Wi-Fi, otherwise the physical NIC behind the switch when one resolved,
    /// falling back to the selected adapter's own alias.</summary>
    internal static string? SuggestDisplayHint(bool wired, string alias, BridgePeer? bridged, string? ssid) =>
        wired ? bridged?.Name ?? alias : ssid;

    /// <summary>
    /// A stored key that can no longer match: same subnet as now, different MAC — what a new dock or
    /// a recreated Hyper-V switch leaves behind. Advisory only, and never rewritten, because the same
    /// reading also fits a genuinely different network on the same private subnet.
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
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => new BridgePeer(
                n.Name,
                MacOrNull(n),
                n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase),
                n.OperationalStatus))
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
