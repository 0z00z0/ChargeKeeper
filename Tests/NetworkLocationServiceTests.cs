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

        var result = NetworkLocationService.SelectPrimary(new[] { physical, vEthernet }, bestIndex: 12);

        Assert.Same(vEthernet, result);
    }

    [Fact]
    public void SelectPrimary_PlainWired_SelectsThatEthernet()
    {
        var ethernet = new AdapterCandidate(IPv4Index: 5, IsVirtual: false, Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary(new[] { ethernet }, bestIndex: 5);

        Assert.Same(ethernet, result);
    }

    [Fact]
    public void SelectPrimary_NoBestInterface_PrefersPhysicalEthernetOverWireless()
    {
        // GetBestInterface unavailable (returns 0) → fall back to the preference order. The wired NIC
        // wins over the simultaneously-active wireless one regardless of list order.
        var wifi     = new AdapterCandidate(IPv4Index: 9, IsVirtual: false, Type: NetworkInterfaceType.Wireless80211);
        var ethernet = new AdapterCandidate(IPv4Index: 3, IsVirtual: false, Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary(new[] { wifi, ethernet }, bestIndex: 0);

        Assert.Same(ethernet, result);
    }

    [Fact]
    public void SelectPrimary_NoBestInterface_OnlyVirtualCandidate_StillReturnsIt()
    {
        // Even with GetBestInterface unavailable and the sole candidate a virtual Ethernet (e.g. the
        // Hyper-V bridge with no physical peer Up), a usable adapter must never be discarded as null.
        var vEthernet = new AdapterCandidate(IPv4Index: 4, IsVirtual: true, Type: NetworkInterfaceType.Ethernet);

        var result = NetworkLocationService.SelectPrimary(new[] { vEthernet }, bestIndex: 0);

        Assert.Same(vEthernet, result);
    }

    [Fact]
    public void SelectPrimary_NoCandidates_ReturnsNull()
    {
        // Genuinely offline (no adapter has a usable IPv4) → null, which DetectCurrent maps to the
        // empty "No network detected" location.
        Assert.Null(NetworkLocationService.SelectPrimary(System.Array.Empty<AdapterCandidate>(), bestIndex: 0));
    }
}
