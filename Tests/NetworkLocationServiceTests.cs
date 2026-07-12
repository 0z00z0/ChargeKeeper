using System.Net;
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
}
