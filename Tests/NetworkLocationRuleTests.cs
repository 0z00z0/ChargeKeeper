using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class NetworkLocationRuleTests
{
    private static readonly NetworkLocation OfficeDock =
        new(AdapterMac: "AA:BB:CC:DD:EE:FF", IpCidr: "10.0.1.0/24", IsWired: true, DisplayHint: "Docking Station Ethernet");

    [Fact]
    public void Matches_BothKeysMatch_True()
    {
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "10.0.1.0/24" };
        Assert.True(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_MacDiffers_False()
    {
        var rule = new NetworkLocationRule { AdapterMac = "11:22:33:44:55:66", IpCidr = "10.0.1.0/24" };
        Assert.False(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_CidrDiffers_False()
    {
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "192.168.1.0/24" };
        Assert.False(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_OnlyMacSet_IgnoresCidr()
    {
        // A rule can key on just one dimension — e.g. a laptop that always gets a different DHCP
        // lease at the same physical dock should still match on MAC alone.
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = null };
        Assert.True(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_OnlyCidrSet_IgnoresMac()
    {
        var rule = new NetworkLocationRule { AdapterMac = null, IpCidr = "10.0.1.0/24" };
        Assert.True(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_NeitherKeySet_NeverMatches()
    {
        // A rule with no match key at all must not become an accidental catch-all — that's what
        // UnknownNetworkPresetName is for, as an explicit separate setting, not an empty rule.
        var rule = new NetworkLocationRule { AdapterMac = null, IpCidr = null };
        Assert.False(rule.Matches(OfficeDock));
    }

    [Fact]
    public void Matches_EmptyLocation_NeverMatchesAnyRealRule()
    {
        var rule = new NetworkLocationRule { AdapterMac = "AA:BB:CC:DD:EE:FF" };
        Assert.False(rule.Matches(default));
    }

    [Fact]
    public void NetworkLocation_IsEmpty_TrueOnlyWhenBothKeysNull()
    {
        Assert.True(default(NetworkLocation).IsEmpty);
        Assert.False(new NetworkLocation("AA:BB:CC:DD:EE:FF", null, true, null).IsEmpty);
        Assert.False(new NetworkLocation(null, "10.0.1.0/24", true, null).IsEmpty);
    }
}
