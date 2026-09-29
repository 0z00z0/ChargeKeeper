using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The allow-list on the network lever, and the rules a session writes from it.
/// </summary>
/// <remarks>Everything here runs against the list and the rule composition. No firewall rule is
/// created, changed or removed, and no settings document is touched.</remarks>
public class FocusAllowedProgramsTests
{
    private const string Editor = @"C:\Program Files\Example\editor.exe";
    private const string Reader = @"C:\Program Files\Example\reader.exe";

    // ── The two published rule names ────────────────────────────────────────────────────────────

    /// <summary>The names a person types into Windows Defender Firewall to get out of a session.
    /// They are quoted on the Settings page and in USAGE.md, and they are what a removal falls back
    /// to, so they are written out here as literals rather than read from the code they guard.
    /// </summary>
    [Fact]
    public void ThePublishedRuleNamesAreTheOnesAPersonIsToldToDelete()
    {
        Assert.Equal("ChargeKeeper focus session: broker", FocusFirewallRules.BrokerRuleName);
        Assert.Equal("ChargeKeeper focus session: name resolution", FocusFirewallRules.ResolverRuleName);
        Assert.Equal("ChargeKeeper focus session: allowed program 1", FocusFirewallRules.AllowedProgramName(1));
        Assert.Equal("ChargeKeeper focus session", FocusFirewallRules.Group);
    }

    /// <summary>Every rule this application creates has to be recognised again later. One left
    /// behind is a program permanently outside every later block.</summary>
    [Fact]
    public void EveryRuleASessionWrites_IsRecognisedAsOneOfItsOwn()
    {
        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "198.51.100.1", [Editor, Reader]);

        Assert.All(rules, r => Assert.True(FocusFirewallRules.IsOwnName(r.Name), r.Name));
        Assert.False(FocusFirewallRules.IsOwnName("Some other program"));
        Assert.False(FocusFirewallRules.IsOwnName(null));
    }
}
