using System;
using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>A lever that records what it was asked to do and answers however a test wants.</summary>
internal sealed class FakeFocusLever : IFocusLever
{
    public string? RefusalText { get; set; }

    public bool EngageSucceeds { get; set; } = true;

    public int Engagements { get; private set; }

    public int Lifts { get; private set; }

    public int Resumptions { get; private set; }

    public string? Refusal() => RefusalText;

    public bool Engage(ActionCause cause)
    {
        Engagements++;
        return EngageSucceeds;
    }

    public void Resume(ActionCause cause) => Resumptions++;

    public bool Lift(ActionCause cause)
    {
        Lifts++;
        return true;
    }
}

internal sealed class FakeFocusSessionRecord : IFocusSessionRecord
{
    public FocusSessionRecord? Held { get; set; }

    public bool SaveSucceeds { get; set; } = true;

    public FocusSessionRecord? Read() => Held;

    public bool Save(FocusSessionRecord session)
    {
        if (!SaveSucceeds) return false;
        Held = session;
        return true;
    }

    public void Clear() => Held = null;
}

/// <summary>A firewall that remembers what it was set to, so a test can compare what came back
/// against what was there before.</summary>
internal sealed class FakeFirewallPolicy : IFirewallPolicy
{
    private readonly Dictionary<FirewallProfile, FirewallProfileSetting> _state;

    public FakeFirewallPolicy(params FirewallProfileSetting[] state) =>
        _state = state.ToDictionary(s => s.Profile);

    public bool ReadFails { get; set; }

    public List<string> Rules { get; } = [];

    public int Removals { get; private set; }

    public IReadOnlyList<FirewallProfileSetting>? ReadAll() =>
        ReadFails ? null : [.. Enum.GetValues<FirewallProfile>().Select(p => _state[p])];

    public bool Write(FirewallProfileSetting setting)
    {
        _state[setting.Profile] = setting;
        return true;
    }

    /// <summary>Every rule added, so a test can read back the program a rule was scoped to.</summary>
    public List<FirewallAllowRule> Added { get; } = [];

    public bool AddAllowRule(FirewallAllowRule rule)
    {
        Rules.Add(rule.Name);
        Added.Add(rule);
        return true;
    }

    /// <summary>Removes by the same reading the live policy uses — whether the name belongs to this
    /// feature — rather than by a fixed list, which cannot reach an allowed program's rule.</summary>
    public bool RemoveOwnRules()
    {
        Removals++;
        Rules.RemoveAll(FocusFirewallRules.IsOwnName);
        return true;
    }

    /// <summary>The three profiles as they stand, in declaration order.</summary>
    public IReadOnlyList<FirewallProfileSetting> Now() =>
        [.. Enum.GetValues<FirewallProfile>().Select(p => _state[p])];
}

internal sealed class FakeFirewallRecord : IFirewallBlockRecord
{
    public IReadOnlyList<FirewallProfileSetting>? Held { get; set; }

    public IReadOnlyList<FirewallProfileSetting>? Read() => Held;

    public bool Save(IReadOnlyList<FirewallProfileSetting> settings)
    {
        Held = [.. settings];
        return true;
    }

    public void Clear() => Held = null;
}

/// <summary>
/// The rules a focus session cannot get wrong: it ends even if the machine was switched off through
/// its expiry, it ends early on the second request inside its window, nothing stays engaged when
/// arming fails, and the firewall comes back exactly as it was found.
/// </summary>
/// <remarks>Everything here runs against fakes. No firewall rule is created, changed or removed, no
/// brightness is written, and no settings document is touched.</remarks>
public class FocusSessionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class Bed
    {
        public DateTimeOffset Now = Noon;
        public FakeFocusLever Network { get; } = new();
        public FakeFocusLever Screen { get; } = new();
        public FakeFocusLever Cover { get; } = new();
        public FakeFocusLever Input { get; } = new();
        public FakeFocusSessionRecord Record { get; } = new();
        public List<string> Log { get; } = [];
        public FocusSessionEngine Engine { get; }

        public Bed() => Engine = new FocusSessionEngine(
            Network, Screen, Cover, Input, Record, () => Now,
            (what, cause) => Log.Add($"{what} ({cause})"));

        public void Advance(TimeSpan by) => Now += by;
    }

    // ── An expired session is always cleared at the next start ──────────────────────────────────

    [Fact]
    public void ASessionThatExpiredWhileTheMachineWasOff_IsLiftedAtTheNextStart()
    {
        // The whole of what makes this survive a power cut: nothing was running to notice the timer
        // pass, so the start is the only moment left that can end it.
        var bed = new Bed { Now = Noon };
        bed.Record.Held = new FocusSessionRecord(
            Noon.AddMinutes(-150), Noon.AddMinutes(-90), true, true, true, true);

        bed.Engine.Start();

        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(1, bed.Screen.Lifts);
        Assert.Equal(1, bed.Cover.Lifts);
        Assert.Null(bed.Record.Held);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    [Fact]
    public void ALeverRecordWithNoSessionBehindIt_IsPutBackAtTheNextStart()
    {
        var bed = new Bed();

        bed.Engine.Start();

        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(1, bed.Screen.Lifts);
        // The cover is a window and died with the run that raised it. Taking it down again costs
        // nothing and is what stops a black screen outliving a session nobody owns.
        Assert.Equal(1, bed.Cover.Lifts);
    }

    // ── Arming ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALeverThatFailsToEngage_LeavesNothingEngagedBehindIt()
    {
        var bed = new Bed();
        bed.Screen.EngageSucceeds = false;

        var outcome = bed.Engine.Arm(60, blocksNetwork: true, dimsScreen: true, coversScreen: true, blocksInput: false, "a test");

        Assert.Equal(FocusArmOutcome.LeverFailed, outcome);
        Assert.Null(bed.Record.Held);
        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    // ── The cover ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCoverComesDown_WhenTheSessionRunsItsLength()
    {
        // A cover left up is a black screen with nothing on the machine able to clear it, so this is
        // the one thing about the cover that must never fail.
        var bed = new Bed();
        bed.Engine.Arm(30, blocksNetwork: false, dimsScreen: false, coversScreen: true, blocksInput: false, "a test");
        Assert.Equal(1, bed.Cover.Engagements);

        bed.Advance(TimeSpan.FromMinutes(30));
        bed.Engine.Tick();

        Assert.Equal(1, bed.Cover.Lifts);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    // ── The staged cancel ───────────────────────────────────────────────────────────────────────

    /// <summary>The staged cancel's own timing, written out rather than read from the code it
    /// guards. A test that advances its clock by the constant follows a change to that constant
    /// instead of catching it — which is how this guard passed against a ten-minute confirm
    /// window.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    [Fact]
    public void TheStagedCancelRunsOnFiveMinutesAndTenSeconds() =>
        Assert.Equal((Wait, Window), (FocusSessionStages.CancelWait, FocusSessionStages.ConfirmWindow));

    private static Bed Running()
    {
        var bed = new Bed();
        Assert.Equal(FocusArmOutcome.Armed,
                     bed.Engine.Arm(120, blocksNetwork: true, dimsScreen: true, coversScreen: true, blocksInput: false, "a test"));
        return bed;
    }

    [Fact]
    public void ASecondRequestInsideTheConfirmWindow_EndsTheSession()
    {
        var bed = Running();
        bed.Engine.RequestCancel("a test");

        bed.Advance(Wait);
        bed.Engine.Tick();
        Assert.Equal(FocusSessionStage.Confirm, bed.Engine.Snapshot().Stage);

        bed.Engine.RequestCancel("a test");

        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(1, bed.Screen.Lifts);
        Assert.Equal(1, bed.Cover.Lifts);
        Assert.Null(bed.Record.Held);
    }

    // ── The firewall park ───────────────────────────────────────────────────────────────────────

    /// <summary>Three profiles that disagree with each other, so a restore that writes one blanket
    /// value passes nothing.</summary>
    private static FakeFirewallPolicy Firewall() => new(
        new FirewallProfileSetting(FirewallProfile.Domain, BlockOutbound: false, BlockAllInbound: true),
        new FirewallProfileSetting(FirewallProfile.Private, BlockOutbound: false, BlockAllInbound: false),
        new FirewallProfileSetting(FirewallProfile.Public, BlockOutbound: true, BlockAllInbound: false));

    private static IReadOnlyList<FirewallAllowRule> Exceptions() =>
        FocusFirewallRules.For("198.51.100.7", 8883, "198.51.100.1");

    [Fact]
    public void TheFirewallStateRecordedBeforeTheBlock_IsExactlyWhatIsPutBack()
    {
        var policy = Firewall();
        var before = policy.Now();
        var record = new FakeFirewallRecord();
        var park = new FirewallBlockPark(policy, record, (_, _) => { });

        Assert.True(park.Engage(Exceptions(), "a test"));
        Assert.All(policy.Now(), p => Assert.True(p.BlockOutbound && p.BlockAllInbound));
        Assert.Equal(before, record.Held);

        Assert.True(park.Lift("a test"));

        Assert.Equal(before, policy.Now());
        Assert.Null(record.Held);
        Assert.Empty(policy.Rules);
    }

    [Fact]
    public void TheRecordIsNeverRecapturedWhileTheBlockStands()
    {
        // A second engage that read the blocked state back into the record would make the block its
        // own "before", and the firewall would never come off.
        var policy = Firewall();
        var before = policy.Now();
        var record = new FakeFirewallRecord();
        var park = new FirewallBlockPark(policy, record, (_, _) => { });

        park.Engage(Exceptions(), "a test");
        park.Engage(Exceptions(), "a test");

        Assert.Equal(before, record.Held);
        park.Lift("a test");
        Assert.Equal(before, policy.Now());
    }

    /// <summary>A rule an earlier session wrote for a program since taken off the list still has to
    /// go. The removal reads the rules that are there, never the list as it stands now.</summary>
    [Fact]
    public void ARuleFromASessionWithALongerList_IsStillRemoved()
    {
        var policy = Firewall();
        policy.Rules.Add(FocusFirewallRules.AllowedProgramName(4));
        var park = new FirewallBlockPark(policy, new FakeFirewallRecord(), (_, _) => { });

        park.Engage(FocusFirewallRules.For("198.51.100.7", 8883, "", []), "a test");
        park.Lift("a test");

        Assert.Empty(policy.Rules);
    }

    [Fact]
    public void AFirewallThatCannotBeRead_LeavesTheMachineOpenRatherThanBlockingWithNothingToRestore()
    {
        var policy = Firewall();
        var before = policy.Now();
        policy.ReadFails = true;
        var record = new FakeFirewallRecord();
        var park = new FirewallBlockPark(policy, record, (_, _) => { });

        Assert.False(park.Engage(Exceptions(), "a test"));

        Assert.Null(record.Held);
        Assert.Equal(before, policy.Now());
        Assert.Empty(policy.Rules);
    }
}
