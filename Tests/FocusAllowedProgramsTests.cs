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

    // ── What a session writes from the list ─────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyList_WritesExactlyWhatASessionAlwaysWrote()
    {
        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "198.51.100.1", []);

        Assert.Equal([FocusFirewallRules.BrokerRuleName, FocusFirewallRules.ResolverRuleName],
                     rules.Select(r => r.Name));
    }

    [Fact]
    public void EachAllowedProgram_BecomesOneMoreOutboundExceptionNamedByItsPath()
    {
        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "198.51.100.1", [Editor, Reader]);

        Assert.Equal(
            [FocusFirewallRules.BrokerRuleName, FocusFirewallRules.ResolverRuleName,
             FocusFirewallRules.AllowedProgramName(1), FocusFirewallRules.AllowedProgramName(2)],
            rules.Select(r => r.Name));

        var program = rules.Single(r => r.Name == FocusFirewallRules.AllowedProgramName(1));
        Assert.Equal(Editor, program.ApplicationPath);
        Assert.Equal(FirewallDirection.Outbound, program.Direction);
        // Every protocol, so a rule cannot carry a port: the firewall refuses one on a rule that has
        // not committed to TCP or UDP.
        Assert.Equal(FocusFirewallRules.ProtocolAny, program.Protocol);
        Assert.Equal("", program.RemotePorts);
    }

    [Fact]
    public void AMachineWhoseBrokerNeedsNoLookup_StillNumbersItsProgramsFromOne()
    {
        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "", [Editor]);

        Assert.Equal([FocusFirewallRules.BrokerRuleName, FocusFirewallRules.AllowedProgramName(1)],
                     rules.Select(r => r.Name));
    }

    [Fact]
    public void AStoredEntryThatNoLongerNamesAProgram_WritesNoRuleAtAll()
    {
        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "",
                                           ["", "not-a-path", @"C:\Program Files\Example\notes.txt", Editor]);

        Assert.Equal([FocusFirewallRules.BrokerRuleName, FocusFirewallRules.AllowedProgramName(1)],
                     rules.Select(r => r.Name));
    }

    // ── Editing the list ────────────────────────────────────────────────────────────────────────

    /// <summary>The refusal that matters: the rules were written when the session armed, so a list
    /// that moved under them would describe a firewall state the machine is not in.</summary>
    [Fact]
    public void TheListCannotChangeWhileASessionRuns()
    {
        var list = new List<string> { Editor };

        Assert.Equal(FocusAllowVerdict.SessionRunning,
                     FocusAllowedPrograms.Add(list, Reader, sessionRunning: true));
        Assert.Equal(FocusAllowVerdict.SessionRunning,
                     FocusAllowedPrograms.Remove(list, Editor, sessionRunning: true));

        Assert.Equal([Editor], list);
    }

    [Fact]
    public void AProgramGoesOnTheListByItsFullPathAndComesOffAgain()
    {
        var list = new List<string>();

        Assert.Equal(FocusAllowVerdict.Added, FocusAllowedPrograms.Add(list, Editor, false));
        Assert.Equal([Editor], list);

        Assert.Equal(FocusAllowVerdict.Removed, FocusAllowedPrograms.Remove(list, Editor, false));
        Assert.Empty(list);
    }

    [Fact]
    public void TheSameProgramSpeltTwoWays_IsOneEntry()
    {
        var list = new List<string> { Editor };

        Assert.Equal(FocusAllowVerdict.AlreadyAllowed,
                     FocusAllowedPrograms.Add(list, Editor.ToUpperInvariant(), false));
        Assert.Equal(FocusAllowVerdict.AlreadyAllowed,
                     FocusAllowedPrograms.Add(list, $"\"{Editor}\" ", false));
        Assert.Single(list);
    }

    [Fact]
    public void SomethingThatIsNotAProgram_NeverReachesTheList()
    {
        var list = new List<string>();

        Assert.Equal(FocusAllowVerdict.NotAProgram, FocusAllowedPrograms.Add(list, "", false));
        Assert.Equal(FocusAllowVerdict.NotAProgram, FocusAllowedPrograms.Add(list, "editor.exe", false));
        Assert.Equal(FocusAllowVerdict.NotAProgram,
                     FocusAllowedPrograms.Add(list, @"C:\Program Files\Example\notes.txt", false));
        Assert.Empty(list);
    }

    [Fact]
    public void TakingOffAProgramThatIsNotOnTheList_ChangesNothing()
    {
        var list = new List<string> { Editor };

        Assert.Equal(FocusAllowVerdict.NotAllowed, FocusAllowedPrograms.Remove(list, Reader, false));
        Assert.Equal([Editor], list);
    }

    [Fact]
    public void TheRowShowsTheProgramsOwnNameWhileTheStoredValueStaysThePath() =>
        Assert.Equal("editor", FocusAllowedPrograms.DisplayName(Editor));
}
