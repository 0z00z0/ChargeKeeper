using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ChargeKeeper.Services;

/// <summary>
/// A network location the user has configured a charge-threshold preset for (TODO #31).
/// <see cref="AdapterMac"/>/<see cref="IpCidr"/> are the actual match key (see
/// <see cref="NetworkLocationService"/>); at least one must be set for a rule to ever match.
/// </summary>
internal sealed class NetworkLocationRule
{
    public string  Name       { get; set; } = "";
    public string? AdapterMac { get; set; }
    public string? IpCidr     { get; set; }
    public string  PresetName { get; set; } = "";

    /// <summary>
    /// Hold the machine awake while this location is the current one (issue #90) — leaving is then the
    /// natural off switch, the same way the preset follows the network. Default false, so old
    /// settings.json files deserialise unchanged.
    /// </summary>
    public bool KeepAwakeHere { get; set; }

    public bool Matches(NetworkLocation location) =>
        (AdapterMac is not null || IpCidr is not null) &&
        (AdapterMac is null || AdapterMac == location.AdapterMac) &&
        (IpCidr     is null || IpCidr     == location.IpCidr);
}

/// <summary>
/// Fingerprint of the currently-connected primary network adapter (TODO #31). Equatable so
/// <see cref="NetworkLocationService"/> can cheaply tell "did anything actually change" apart from
/// "an event fired" — <see cref="NetworkChange"/> events fire far more often than the resolved
/// location does. On a Hyper-V external switch <see cref="AdapterMac"/> is the PHYSICAL NIC behind
/// the switch rather than the vNIC that holds the IP (see <see cref="NetworkLocationService.Compose"/>);
/// <see cref="IpCidr"/> and <see cref="IsWired"/> always come from the selected adapter.
/// <see cref="DisplayHint"/> (WiFi SSID, or that same wired adapter name) is NEVER part of matching
/// — only a friendlier suggested default when naming a new rule.
/// </summary>
internal readonly record struct NetworkLocation(string? AdapterMac, string? IpCidr, bool IsWired, string? DisplayHint)
{
    public bool IsEmpty => AdapterMac is null && IpCidr is null;

    /// <summary>
    /// Identity for change detection — ONLY the match keys (MAC + CIDR), not <see cref="DisplayHint"/>.
    /// The record's auto-generated <c>Equals</c> includes DisplayHint, which can flap
    /// (<c>TryGetWifiSsid</c> is best-effort and may transiently return null vs the real SSID while
    /// MAC/CIDR stay constant); comparing the whole record would then re-fire <c>LocationChanged</c>
    /// and re-apply the preset on a hint flicker, defeating the debounce's stated "don't flap the
    /// applied preset" purpose. Change detection uses this instead.
    /// </summary>
    public bool SameLocationAs(NetworkLocation other) =>
        AdapterMac == other.AdapterMac && IpCidr == other.IpCidr;
}

/// <summary>
/// The minimal projection of a live <see cref="NetworkInterface"/> that
/// <see cref="NetworkLocationService.SelectPrimary"/> needs to pick the primary adapter — pulled
/// out as a tiny record so that selection heuristic stays a pure, unit-testable function (house
/// style; see <see cref="HaStateBuilder"/>/<c>PresetEditValidator</c>). <see cref="IPv4Index"/> is
/// the adapter's IPv4 interface index (compared against <c>GetBestInterface</c>'s answer);
/// <see cref="IsVirtual"/> is only a last-resort fallback tiebreaker, NEVER a reason to drop the
/// routing-table winner (issue #21). <see cref="Adapter"/> refers back to the live interface the
/// caller ultimately returns; it is null in tests, which exercise the decision on the value fields
/// alone.
/// </summary>
internal sealed record AdapterCandidate(
    int IPv4Index,
    bool IsVirtual,
    NetworkInterfaceType Type,
    NetworkInterface? Adapter = null);

/// <summary>
/// The minimal projection of a live <see cref="NetworkInterface"/> that
/// <see cref="NetworkLocationService.ResolveBridgedAdapterName"/> needs to walk back from a Hyper-V
/// external-switch vNIC to the physical NIC that switch is bound to — the same pure-function split
/// as <see cref="AdapterCandidate"/>, so the walk-back is unit-testable without a Hyper-V host.
/// <see cref="Mac"/> is null when the adapter has no real 6-byte hardware address (tunnel/WAN
/// miniport pseudo-adapters), which disqualifies it as the NIC behind a switch;
/// <see cref="IsVirtual"/> uses the same "Virtual" description marker as
/// <see cref="AdapterCandidate.IsVirtual"/>, and <see cref="Status"/> breaks a tie towards the
/// adapter that is actually present.
/// </summary>
internal sealed record BridgePeer(string Name, string? Mac, bool IsVirtual, OperationalStatus Status);

/// <summary>
/// Detects the current network location (TODO #31) via the OS's own routing table — the same
/// underlying approach HyperVManagerTray already uses for its VM-network-switching feature
/// (`Services/AdapterMatcher.cs` there, read for reference only, not shared code — see that
/// project's own notes on why the two apps don't share a library for this). Like that app it
/// follows Windows' own routing table (<c>GetBestInterface</c>) authoritatively — including a
/// "vEthernet (…)" Hyper-V external-switch bridge that carries the default route, which an earlier
/// <c>!Description.Contains("Virtual")</c> candidate filter here wrongly dropped (issue #21).
/// <para>
/// Fingerprints the PRIMARY adapter (the one Windows' own routing table says traffic actually goes
/// through, via <c>GetBestInterface</c>) by MAC address and IP subnet (CIDR) rather than by WiFi
/// SSID: this works identically for a docked Ethernet connection (which has no SSID at all) and a
/// WiFi network, needs no WLAN-specific capability declaration (this app is unpackaged, where that
/// matters more than for a packaged one), and correctly prefers a wired connection over a
/// simultaneously-active WiFi radio — a laptop docked with WiFi still enabled would otherwise have
/// an ambiguous "current network".
/// </para>
/// </summary>
internal static class NetworkLocationService
{
    // Coalesces a burst of NetworkChange events around one physical transition (dock/undock, WiFi
    // roam) into a single re-evaluation — same 1500ms figure and rationale as HyperVManagerTray's
    // NetworkMonitor: a single dock rebind fires the underlying OS events several times in quick
    // succession, and evaluating on each one would flap the applied preset before settling.
    private const int DebounceMs = 1500;

    // Guards the timer re-arm + _last + the started/handler state. NetworkChange events can fire
    // concurrently on multiple threads, racing ScheduleEvaluate's Change() against Stop()'s
    // dispose and Evaluate's read/write of _last; Start/Stop normally run once on the UI thread
    // but are cheap to guard too. LocationChanged is invoked OUTSIDE the lock so a slow subscriber
    // (ApplyPreset) can't stall an incoming NetworkChange.
    private static readonly System.Threading.Lock _sync = new();
    private static System.Threading.Timer? _debounceTimer;
    private static NetworkLocation _last;
    private static bool _started;

    // Whether _last holds a real reading yet. The first evaluation after Start() only SEEDS it —
    // the app has just learned where it already is, nothing changed. Treating that as a change
    // fired LocationChanged on every app start, which re-applied a network profile and thereby
    // cancelled an in-flight travel override — the one thing TravelOverrideActive is persisted to
    // survive.
    private static bool _seeded;

    // Held so Stop() can actually unsubscribe: the previous anonymous-lambda subscriptions could
    // never be removed, so a NetworkChange after Stop() re-armed the timer and a later Start()
    // double-subscribed. Only benign because the real lifecycle is one Start (App init) + one Stop
    // (Cleanup), but "Stop doesn't stop" is a latent trap.
    private static NetworkAddressChangedEventHandler? _addressChangedHandler;
    private static NetworkAvailabilityChangedEventHandler? _availabilityChangedHandler;

    /// <summary>Raised (off the UI thread, after the debounce settles) whenever the detected location changes.</summary>
    public static event Action<NetworkLocation>? LocationChanged;

    /// <summary>
    /// The last debounced location <see cref="Evaluate"/> resolved — the cheap read for status
    /// display (the tray menu shows a location row on every open). Unlike <see cref="DetectCurrent"/>
    /// this does no adapter enumeration; <see cref="LocationChanged"/> keeps consumers current.
    /// Default (empty) until the first post-<see cref="Start"/> evaluation lands.
    /// </summary>
    public static NetworkLocation LastKnown { get { lock (_sync) return _last; } }

    /// <summary>
    /// Whether an evaluation is a real location CHANGE (raise <see cref="LocationChanged"/>) rather
    /// than the first baseline seed. Pure so the seed rule is testable without live adapters (house
    /// style; see <see cref="SelectPrimary"/>). Comparison is MAC/CIDR only — DisplayHint flaps.
    /// </summary>
    internal static bool IsLocationChange(bool seeded, NetworkLocation current, NetworkLocation last) =>
        seeded && !current.SameLocationAs(last);

    public static void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
            // One timer for the service's lifetime, re-armed per event via Change() — dock/undock
            // transitions fire NetworkChange in bursts (the exact case the debounce exists for),
            // and allocating a fresh Timer per event on that hot path is pointless churn.
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
            _debounceTimer?.Change(DebounceMs, System.Threading.Timeout.Infinite);   // push the deadline out
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

    /// <summary>
    /// Formats a human-readable "current network" status line — the matching rule's name, or a
    /// fallback when nothing matches / nothing is detected. Shared (TODO #19) by the Settings
    /// window's Smart Charge page; originally lived on TrayMenu when it had its own "Current: …"
    /// status row, before that row moved into the Settings window. Prefers <see cref="LastKnown"/>
    /// over a fresh <see cref="DetectCurrent"/>: a full adapter enumeration + routing-table
    /// P/Invoke is wasted work when <see cref="LocationChanged"/> already keeps callers current.
    /// Falls back to a live read only when LastKnown is empty — the first post-<see cref="Start"/>
    /// evaluation hasn't resolved yet, or the machine is genuinely offline. Safe off the UI thread.
    /// </summary>
    public static string DescribeCurrentLocation()
    {
        var location = LastKnown;
        if (location.IsEmpty) location = DetectCurrent();
        if (location.IsEmpty) return "No network detected";
        var rule = SettingsService.Current.FindNetworkRule(location);
        return rule is not null ? rule.Name : "Unrecognised network";
    }

    /// <summary>
    /// Renders the MATCH KEY — the MAC and subnet a profile is actually keyed on (see
    /// <see cref="NetworkLocationRule.Matches"/>) — as "MAC … · Subnet …". One formatter for both
    /// places it is shown: the Settings page's per-rule "Matches" line, and the naming dialog, where
    /// it makes clear the profile follows these and not the name being typed into the box.
    /// </summary>
    internal static string DescribeMatchKey(string? adapterMac, string? ipCidr)
    {
        var parts = new List<string>();
        if (adapterMac is { } mac)  parts.Add($"MAC {mac}");
        if (ipCidr     is { } cidr) parts.Add($"Subnet {cidr}");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No match key — this profile will never apply.";
    }

    /// <summary>
    /// Reads the current location synchronously. Used both by the change-detection path above and
    /// directly by the tray's "Add configuration for this network" command, which needs an
    /// up-to-the-moment reading rather than whatever the last debounced event happened to capture.
    /// </summary>
    public static NetworkLocation DetectCurrent()
    {
        try
        {
            // FindPrimaryAdapter itself enumerates interfaces (GetAllNetworkInterfaces) and touches
            // adapter properties, which can throw NetworkInformationException during the very
            // dock/undock race this catch exists for — so it must be INSIDE the try, or the
            // synchronous UI caller (SettingsWindow.OnAddNetworkRule, "Add profile for this
            // network") gets an unguarded exception. (Evaluate wraps its own call, but it doesn't.)
            var primary = FindPrimaryAdapter();
            if (primary is null) return default;

            string mac = NormalizeMac(primary.GetPhysicalAddress().ToString());
            var props  = primary.GetIPProperties();
            var ipv4   = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            string? cidr = ipv4 is not null ? CalculateCidr(ipv4.Address, ipv4.IPv4Mask) : null;
            bool wired   = primary.NetworkInterfaceType != NetworkInterfaceType.Wireless80211;

            // ONE pairing lookup feeds both facets it can change — the stored MAC and the suggested
            // name — so the name can never describe a different adapter from the one the key
            // identifies. Null for Wi-Fi and for every non-bridged wired adapter.
            var bridged  = wired ? FindBridgedPeer(primary, mac) : null;
            string? ssid = wired ? null : TryGetWifiSsid();
            return Compose(mac, cidr, wired, bridged, SuggestDisplayHint(wired, primary.Name, bridged, ssid));
        }
        catch
        {
            // The adapter (or its enumeration) can vanish mid-read during a dock/undock transition —
            // the same race HyperVManagerTray's NetworkMonitor guards against. Treat it as "no
            // location" rather than letting a transient native-adjacent fault propagate.
            return default;
        }
    }

    // Mirrors HyperVManagerTray's AdapterMatcher.PrimaryAdapter: ask Windows' own routing table
    // (GetBestInterface) which adapter traffic actually goes through. The candidate set is EVERY Up
    // adapter that owns a usable IPv4 address — deliberately NOT restricted to Ethernet/Wireless
    // types or to non-"Virtual" descriptions: on a Hyper-V external switch the routable IP + default
    // route live on a "vEthernet (…)" Hyper-V Virtual Ethernet Adapter while the bridged physical NIC
    // keeps no IP of its own, so the old "drop anything named Virtual" filter detected nothing at all
    // (issue #21). The pure selection heuristic lives in SelectPrimary (unit-tested); this method
    // only enumerates the live adapters and projects each usable one to an AdapterCandidate.
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
    /// Pure selection heuristic behind <see cref="FindPrimaryAdapter"/>, extracted so the #21
    /// bridge-vs-physical decision is unit-testable without live adapters (house style — see
    /// <see cref="HaStateBuilder"/>/<c>PresetEditValidator</c>). The authoritative pick is the
    /// candidate whose IPv4 interface index equals <paramref name="bestIndex"/> — what Windows'
    /// routing table (<c>GetBestInterface</c>) says traffic actually uses — which correctly selects
    /// a "vEthernet (…)" Hyper-V bridge over the IP-less physical NIC. Only when GetBestInterface is
    /// unavailable (<paramref name="bestIndex"/> == 0) or names no candidate does it fall back to a
    /// preference order in which "Virtual" is merely a last-resort tiebreaker: physical Ethernet,
    /// then physical Wi-Fi, then any Ethernet-type (this covers the bridge), then anything usable —
    /// and that fallback NEVER overrides the routing-table winner above.
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

    // ── The Hyper-V bridge walk-back (issue #21 follow-up) ─────────────────────
    // On a Hyper-V external switch the routing table picks the "vEthernet (…)" vNIC (SelectPrimary
    // above, issue #21 — unchanged), but that vNIC is the wrong thing to REMEMBER a network by: its
    // address is the physical NIC's only as long as the switch keeps it. Recreate the switch, or give
    // it a dynamic MAC, and the vNIC gets a Microsoft-OUI 00:15:5D address while the physical NIC
    // keeps its own — a stored profile would then silently stop matching after a change involving no
    // hardware at all. So the physical NIC behind the switch supplies the stored MAC and the
    // suggested name, both from the SAME resolved peer. Subnet and IsWired always come from the
    // selected adapter, and NetworkLocationRule.Matches is untouched.

    /// <summary>
    /// Composes the location record. <paramref name="cidr"/> and <paramref name="wired"/> always
    /// come from the SELECTED adapter — the vEthernet on a bridged host. The stored MAC comes from
    /// <paramref name="bridged"/> when the pairing resolved the physical NIC behind a Hyper-V
    /// external switch (the stable identity, see the note above), and from the selected adapter on
    /// every other path — no Hyper-V, an internal switch, an ambiguous or failed pairing.
    /// </summary>
    internal static NetworkLocation Compose(
        string selectedMac, string? cidr, bool wired, BridgePeer? bridged, string? suggestedName)
    {
        string mac = bridged?.Mac ?? selectedMac;
        return new(mac.Length > 0 ? mac : null, cidr, wired, suggestedName);
    }

    /// <summary>
    /// Whether an adapter is a Hyper-V virtual switch port — the "vEthernet (…)" adapter that holds
    /// the routable IP while the physical NIC bound to the switch keeps none (issue #21). Recognised
    /// by the two strings Windows fixes for these: the description "Hyper-V Virtual Ethernet
    /// Adapter" (the marker HyperVManagerTray keys on too) and the "vEthernet (&lt;switch&gt;)"
    /// connection alias. Gates the pairing below, so a machine without Hyper-V pays two string
    /// comparisons and no second enumeration.
    /// </summary>
    internal static bool LooksLikeHyperVSwitchPort(string? name, string? description) =>
        description?.StartsWith("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase) == true ||
        name?.StartsWith("vEthernet (", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Pure walk-back from a Hyper-V external switch's vNIC to the physical NIC the switch is bound
    /// to. Pairing is by MAC: an EXTERNAL switch's host vNIC inherits the bound adapter's hardware
    /// address verbatim (measured — vEthernet (Bridged) and Ethernet both report 48:65:EE:18:86:EF on
    /// the affected host), which needs no WMI, no Hyper-V module and no elevation.
    /// <para>
    /// Returns null — caller keeps the selected adapter's own MAC and alias — whenever the pairing is
    /// not unambiguous: a non-switch-port adapter is never paired at all, an INTERNAL switch such as
    /// "vEthernet (Default Switch)" carries a synthesised Microsoft-OUI address no physical NIC
    /// shares, and two same-MAC non-virtual adapters (a teaming/filter oddity) leave nothing to
    /// prefer once presence has been weighed. Virtual adapters are excluded as partners so one vNIC
    /// never stands in for another.
    /// </para>
    /// </summary>
    internal static BridgePeer? ResolveBridgedPeer(
        string? alias, string? description, string? switchPortMac, IReadOnlyList<BridgePeer> peers)
    {
        if (!LooksLikeHyperVSwitchPort(alias, description)) return null;
        if (switchPortMac is not { Length: > 0 }) return null;

        var paired = peers.Where(p => p.Mac == switchPortMac && !p.IsVirtual).ToList();
        if (paired.Count <= 1) return paired.FirstOrDefault();

        // Prefer the partner that is actually present over a disabled/absent namesake; still tied
        // means we cannot tell which NIC drives this switch, and a wrong key is worse than the vNIC's.
        var present = paired.Where(p => p.Status == OperationalStatus.Up).ToList();
        return present.Count == 1 ? present[0] : null;
    }

    /// <summary>
    /// Pure name-suggestion rule behind <see cref="NetworkLocation.DisplayHint"/>: the SSID on
    /// Wi-Fi, otherwise the physical NIC behind the switch when one resolved, falling back to the
    /// selected adapter's own alias.
    /// </summary>
    internal static string? SuggestDisplayHint(bool wired, string alias, BridgePeer? bridged, string? ssid) =>
        wired ? bridged?.Name ?? alias : ssid;

    /// <summary>
    /// Whether a stored rule's key can no longer match the network it was made for: the SUBNET is
    /// the one we are on right now, but the MAC is not. That is what an adapter change looks like
    /// afterwards — a new dock, a replaced NIC, a recreated Hyper-V switch — and the rule then never
    /// applies again, silently. Advisory ONLY: the app states the fact on the rule's row and never
    /// rewrites a stored key, because the same reading also fits a genuinely different network that
    /// happens to use the same private subnet.
    /// </summary>
    internal static bool IsStaleKey(NetworkLocationRule rule, NetworkLocation current) =>
        rule.IpCidr     is not null && rule.IpCidr    == current.IpCidr &&
        rule.AdapterMac is not null && current.AdapterMac is not null &&
        rule.AdapterMac != current.AdapterMac;

    /// <summary>Wording for the <see cref="IsStaleKey"/> hint, shown under the rule's match key.</summary>
    internal const string StaleKeyHint =
        "Same subnet as the network you are on now, but a different MAC — this profile will not apply here.";

    // Live-adapter side of ResolveBridgedPeer, mirroring the FindPrimaryAdapter/SelectPrimary split:
    // this one only queries the OS, the decision above is pure. It must never throw — DetectCurrent's
    // own catch would turn a pairing hiccup into "No network detected" — and it must not slow the
    // common case, hence the gate: the peer enumeration happens ONLY for a vEthernet adapter.
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
            // Hyper-V absent, an adapter vanishing mid-enumeration, a property read denied — fall back
            // to exactly what DetectCurrent returned before the pairing existed.
            return null;
        }
    }

    // A SEPARATE enumeration from FindPrimaryAdapter's: the physical NIC behind an external switch is
    // Up with no IPv4 at all (the switch took it), so it is deliberately not a selection candidate —
    // loosening that filter would put an IP-less NIC back in front of SelectPrimary and re-open #21.
    // Not filtered on OperationalStatus either: a disabled same-MAC namesake must be VISIBLE to
    // ResolveBridgedPeer so it can be ranked below the present one rather than silently win.
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
    // WAN miniports and other pseudo-adapters report an empty one, and treating those as equal would
    // pair a switch port with a non-NIC.
    private static string? MacOrNull(NetworkInterface n)
    {
        try
        {
            var address = n.GetPhysicalAddress();
            return address.GetAddressBytes().Length == 6 ? NormalizeMac(address.ToString()) : null;
        }
        catch { return null; }
    }

    // True when the adapter owns an IPv4 unicast address usable as a real connection — excludes
    // loopback and 169.254.x.x APIPA/link-local (a NIC holding only an APIPA address is "up" but has
    // no working network). try/catch → false: GetIPProperties can throw during the very dock/undock
    // race DetectCurrent's catch exists for, in which case the adapter simply isn't a candidate.
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

    // The adapter's IPv4 interface index (what GetBestInterface returns), or -1 when it has no IPv4
    // properties or the read races an adapter removal — a value that can never equal a real
    // bestIndex, so such a candidate is only ever chosen by the fallback, never the routing match.
    private static int IPv4InterfaceIndex(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; }
        catch { return -1; }
    }

    private static uint GetBestInterfaceIndex()
    {
        try
        {
            // 8.8.8.8 is only a routing-table probe target for GetBestInterface — no packet is
            // actually sent anywhere.
            uint dest = BitConverter.ToUInt32([8, 8, 8, 8], 0);
            return GetBestInterface(dest, out uint index) == 0 ? index : 0;
        }
        catch { return 0; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    // internal (not private) so unit tests can verify the formatting directly.
    internal static string NormalizeMac(string raw) => raw.Length == 12
        ? string.Join(":", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))).ToUpperInvariant()
        : raw;

    // internal (not private) so unit tests can verify the network-address masking + prefix length.
    internal static string CalculateCidr(IPAddress address, IPAddress mask)
    {
        int prefixLen = mask.GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b));
        var addrBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var network   = new byte[4];
        for (int i = 0; i < 4; i++) network[i] = (byte)(addrBytes[i] & maskBytes[i]);
        return $"{new IPAddress(network)}/{prefixLen}";
    }

    // Best-effort only: reading the connected WiFi SSID normally wants the WLAN API (or a packaged
    // app's capability-gated WinRT surface); this app is unpackaged and has never otherwise needed
    // networking capabilities. Used purely to suggest a friendlier default NAME when the user names
    // a location — actual matching is MAC/CIDR only (see NetworkLocationRule.Matches), so a failure
    // here just means a slightly less helpful default suggestion, never a broken match.
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
