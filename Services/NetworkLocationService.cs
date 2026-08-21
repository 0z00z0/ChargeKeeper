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
/// Fingerprint of the currently-connected primary network adapter. On a Hyper-V external switch
/// <see cref="AdapterMac"/> is the physical NIC behind the switch rather than the vNIC that holds the
/// IP (see <see cref="NetworkLocationService.Compose"/>); <see cref="IpCidr"/> and
/// <see cref="IsWired"/> always come from the selected adapter. <see cref="DisplayHint"/> (Wi-Fi SSID
/// or wired adapter name) is never part of matching — only a friendlier default when naming a rule.
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
/// What <see cref="NetworkLocationService.SelectPrimary"/> needs of a live adapter, so that heuristic
/// stays pure. <see cref="IsVirtual"/> is a last-resort tiebreaker, never a reason to drop the
/// routing-table winner. <see cref="Adapter"/> is null in tests.
/// </summary>
internal sealed record AdapterCandidate(
    int IPv4Index,
    bool IsVirtual,
    NetworkInterfaceType Type,
    NetworkInterface? Adapter = null);

/// <summary>
/// What the Hyper-V bridge walk-back below needs of a live adapter, so it is testable without a
/// Hyper-V host. <see cref="Mac"/> is null for pseudo-adapters with no 6-byte hardware address, which
/// disqualifies them as the NIC behind a switch.
/// </summary>
internal sealed record BridgePeer(string Name, string? Mac, bool IsVirtual, OperationalStatus Status);

/// <summary>
/// Detects the current network location by fingerprinting the primary adapter — the one Windows'
/// routing table (<c>GetBestInterface</c>) says traffic goes through — by MAC address and IP subnet
/// rather than by Wi-Fi SSID. That works identically for a docked Ethernet connection, needs no WLAN
/// capability declaration (this app is unpackaged), and prefers a dock over a simultaneously-active
/// Wi-Fi radio. The routing table is followed authoritatively, including a "vEthernet (…)" Hyper-V
/// external-switch bridge that carries the default route.
/// </summary>
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
            // FindPrimaryAdapter must stay INSIDE the try: it enumerates interfaces and touches
            // adapter properties, which throw during the dock/undock race this catch exists for, and
            // the synchronous UI caller has no guard of its own.
            var primary = FindPrimaryAdapter();
            if (primary is null) return default;

            string mac = NormalizeMac(primary.GetPhysicalAddress().ToString());
            var props  = primary.GetIPProperties();
            // Same predicate the adapter was SELECTED by: a NIC commonly holds an APIPA address
            // alongside a real lease, and keying the rule on 169.254.0.0/16 would match any
            // link-local network and never the real subnet.
            var ipv4   = props.UnicastAddresses.FirstOrDefault(a => IsUsableIPv4(a.Address));
            string? cidr = ipv4 is not null ? CalculateCidr(ipv4.Address, ipv4.IPv4Mask) : null;
            bool wired   = primary.NetworkInterfaceType != NetworkInterfaceType.Wireless80211;

            // One pairing lookup feeds both the stored MAC and the suggested name, so the name can
            // never describe a different adapter from the one the key identifies.
            var bridged  = wired ? FindBridgedPeer(primary, mac) : null;
            string? ssid = wired ? null : TryGetWifiSsid();
            return Compose(mac, cidr, wired, bridged, SuggestDisplayHint(wired, primary.Name, bridged, ssid));
        }
        catch
        {
            // The adapter, or its enumeration, can vanish mid-read during a dock/undock transition.
            return default;
        }
    }

    // Asks Windows' routing table (GetBestInterface) which adapter traffic goes through. The
    // candidate set is every Up adapter owning a usable IPv4 address, and is not restricted to
    // Ethernet/Wireless types or to non-"Virtual" descriptions: on a Hyper-V external switch the
    // routable IP and default route live on the "vEthernet (…)" adapter while the bridged physical
    // NIC keeps no IP, so dropping anything named Virtual detects nothing at all.
    private static NetworkInterface? FindPrimaryAdapter()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(HasUsableIPv4)
            .Select(n => new AdapterCandidate(
                IPv4InterfaceIndex(n),
                n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase),
                n.NetworkInterfaceType,
                n))
            .ToList();

        return SelectPrimary(candidates, GetBestInterfaceIndex())?.Adapter;
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

    /// <summary>Wording for the <see cref="IsStaleKey"/> hint, shown under the rule's match key.</summary>
    internal const string StaleKeyHint =
        "Same subnet as the network you are on now, but a different MAC — this profile will not apply here.";

    // Live-adapter side of ResolveBridgedPeer. Must never throw: DetectCurrent's own catch would turn
    // a pairing hiccup into "No network detected".
    private static BridgePeer? FindBridgedPeer(NetworkInterface primary, string mac)
    {
        try
        {
            if (!LooksLikeHyperVSwitchPort(primary.Name, primary.Description)) return null;
            return ResolveBridgedPeer(primary.Name, primary.Description,
                                      mac.Length > 0 ? mac : null, EnumerateBridgePeers());
        }
        catch
        {
            // Hyper-V absent, an adapter vanishing mid-enumeration, a property read denied.
            return null;
        }
    }

    // A separate enumeration from FindPrimaryAdapter's, with a looser filter. The physical NIC behind
    // an external switch is Up with no IPv4 at all, so it must not become a selection candidate; and
    // OperationalStatus is not filtered either, so a disabled same-MAC namesake stays visible to
    // ResolveBridgedPeer and can be ranked below the present one rather than silently winning.
    private static List<BridgePeer> EnumerateBridgePeers() =>
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

    // True when the adapter owns an IPv4 unicast address usable as a real connection. GetIPProperties
    // can throw during a dock/undock race, in which case the adapter simply isn't a candidate.
    private static bool HasUsableIPv4(NetworkInterface n)
    {
        try { return n.GetIPProperties().UnicastAddresses.Any(a => IsUsableIPv4(a.Address)); }
        catch { return false; }
    }

    private static bool IsUsableIPv4(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(addr)) return false;
        var b = addr.GetAddressBytes();
        return !(b[0] == 169 && b[1] == 254);   // 169.254.0.0/16 APIPA / link-local
    }

    // The adapter's IPv4 interface index, or -1 when it has none or the read races an adapter
    // removal — a value no real bestIndex can equal, so such a candidate only ever wins the fallback.
    private static int IPv4InterfaceIndex(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; }
        catch { return -1; }
    }

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
