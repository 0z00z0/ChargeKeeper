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

    public bool Matches(NetworkLocation location) =>
        (AdapterMac is not null || IpCidr is not null) &&
        (AdapterMac is null || AdapterMac == location.AdapterMac) &&
        (IpCidr     is null || IpCidr     == location.IpCidr);
}

/// <summary>
/// Fingerprint of the currently-connected primary network adapter (TODO #31). Equatable so
/// <see cref="NetworkLocationService"/> can cheaply tell "did anything actually change" apart from
/// "an event fired" — <see cref="NetworkChange"/> events fire far more often than the resolved
/// location does. <see cref="DisplayHint"/> (WiFi SSID, or the adapter's own name when wired) is
/// NEVER part of matching — only a friendlier suggested default when naming a new rule.
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
            lock (_sync)
            {
                if (current.SameLocationAs(_last)) return;   // MAC/CIDR only — ignore DisplayHint flaps
                _last = current;
            }
            LocationChanged?.Invoke(current);
        }
        catch (Exception ex)
        {
            AppLog.Error("NetworkLocationService.Evaluate", ex);
        }
    }

    /// <summary>
    /// Formats a human-readable "current network" status line — the matching rule's name, or a
    /// fallback when nothing matches / nothing is detected. Shared (TODO #19) by the Settings
    /// window's Network section; originally lived on TrayMenu when it had its own "Current: …"
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
            string? hint = wired ? primary.Name : TryGetWifiSsid();
            return new NetworkLocation(mac.Length > 0 ? mac : null, cidr, wired, hint);
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
