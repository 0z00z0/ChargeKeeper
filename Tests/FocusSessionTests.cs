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

    public bool Engage(string cause)
    {
        Engagements++;
        return EngageSucceeds;
    }

    public void Resume(string cause) => Resumptions++;

    public bool Lift(string cause)
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

    public bool AddAllowRule(FirewallAllowRule rule)
    {
        Rules.Add(rule.Name);
        return true;
    }

    public bool RemoveOwnRules()
    {
        Removals++;
        Rules.RemoveAll(FocusFirewallRules.Names.Contains);
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

internal sealed class FakeNetworkTargets : IFocusNetworkTargets
{
    public string? Host { get; set; } = "broker.example.invalid";

    public int? Port { get; set; } = 8883;

    public string Addresses { get; set; } = "198.51.100.7";

    public string ResolverAddresses { get; set; } = "198.51.100.1";

    public string? BrokerHost() => Host;

    public int? BrokerPort() => Port;

    public string Resolve(string host) => Addresses;

    public string Resolvers() => ResolverAddresses;
}

/// <summary>
/// The rules a focus session cannot get wrong: it ends even if the machine was switched off through
/// its expiry, it cannot be talked out of early without the second request landing in its window, it
/// refuses to arm with nothing to do, and the firewall comes back exactly as it was found.
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
        public FakeFocusSessionRecord Record { get; } = new();
        public List<string> Log { get; } = [];
        public FocusSessionEngine Engine { get; }

        public Bed() => Engine = new FocusSessionEngine(
            Network, Screen, Cover, Record, () => Now,
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
            Noon.AddMinutes(-150), Noon.AddMinutes(-90), true, true, true);

        bed.Engine.Start();

        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(1, bed.Screen.Lifts);
        Assert.Equal(1, bed.Cover.Lifts);
        Assert.Null(bed.Record.Held);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    [Fact]
    public void ASessionStillWithinItsTime_ResumesAndTakesUpOnlyTheLeversItOwns()
    {
        var bed = new Bed();
        var ends = Noon.AddMinutes(30);
        bed.Record.Held = new FocusSessionRecord(Noon.AddMinutes(-30), ends, true, true, true);

        bed.Engine.Start();

        var session = bed.Engine.Snapshot();
        Assert.Equal(FocusSessionStage.Active, session.Stage);
        Assert.Equal(ends, session.EndsAt);
        // Resumed, never re-engaged: each lever decides what that means for itself, and neither is
        // lifted on the way.
        Assert.Equal(1, bed.Network.Resumptions);
        Assert.Equal(1, bed.Screen.Resumptions);
        Assert.Equal(1, bed.Cover.Resumptions);
        Assert.Equal(0, bed.Network.Engagements);
        Assert.Equal(0, bed.Network.Lifts);
    }

    [Fact]
    public void ASessionThatOwnsOnlyOneLever_LeavesTheOthersAlone()
    {
        var bed = new Bed();
        bed.Record.Held = new FocusSessionRecord(Noon, Noon.AddMinutes(30), BlocksNetwork: true,
                                                  DimsScreen: false, CoversScreen: false);

        bed.Engine.Start();

        Assert.Equal(1, bed.Network.Resumptions);
        Assert.Equal(0, bed.Screen.Resumptions);
        Assert.Equal(0, bed.Cover.Resumptions);
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

    [Fact]
    public void ARestartMidCancel_DiscardsTheAttemptAndResumesActive()
    {
        var bed = new Bed();
        bed.Record.Held = new FocusSessionRecord(Noon, Noon.AddMinutes(30), true, true, true);

        bed.Engine.Start();

        // The confirm window is a live interaction: a restart must neither end a session nor grant
        // an open-ended window.
        Assert.Equal(FocusSessionStage.Active, bed.Engine.Snapshot().Stage);
    }

    // ── Arming ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASessionWithNeitherLeverChosen_IsRefusedAndArmsNothing()
    {
        var bed = new Bed();

        var outcome = bed.Engine.Arm(60, blocksNetwork: false, dimsScreen: false, coversScreen: false, "a test");

        Assert.Equal(FocusArmOutcome.NoLeverChosen, outcome);
        Assert.Null(bed.Record.Held);
        Assert.Equal(0, bed.Network.Engagements);
        Assert.Equal(0, bed.Screen.Engagements);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    [Fact]
    public void ALeverThatWouldRefuse_StopsTheWholeSessionRatherThanHalfArmingIt()
    {
        var bed = new Bed();
        bed.Network.RefusalText = "the MQTT broker port is set to Automatic";

        var outcome = bed.Engine.Arm(60, blocksNetwork: true, dimsScreen: true, coversScreen: true, "a test");

        Assert.Equal(FocusArmOutcome.LeverRefused, outcome);
        Assert.Null(bed.Record.Held);
        Assert.Equal(0, bed.Screen.Engagements);
    }

    [Fact]
    public void ALeverThatFailsToEngage_LeavesNothingEngagedBehindIt()
    {
        var bed = new Bed();
        bed.Screen.EngageSucceeds = false;

        var outcome = bed.Engine.Arm(60, blocksNetwork: true, dimsScreen: true, coversScreen: true, "a test");

        Assert.Equal(FocusArmOutcome.LeverFailed, outcome);
        Assert.Null(bed.Record.Held);
        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    [Fact]
    public void AnArmedSession_RunsToTheEndTimeAndIsWrittenDownBeforeALeverMoves()
    {
        var bed = new Bed();

        Assert.Equal(FocusArmOutcome.Armed,
                     bed.Engine.Arm(45, blocksNetwork: true, dimsScreen: true, coversScreen: true, "a test"));

        Assert.Equal(Noon.AddMinutes(45), bed.Record.Held!.Value.EndsAt);
        Assert.Equal(1, bed.Network.Engagements);
        Assert.Equal(1, bed.Screen.Engagements);
    }

    [Fact]
    public void ASessionEndsItself_WhenItsOwnTimeRunsOutWhileTheApplicationIsRunning()
    {
        var bed = new Bed();
        bed.Engine.Arm(10, blocksNetwork: true, dimsScreen: false, coversScreen: false, "a test");

        bed.Advance(TimeSpan.FromMinutes(10));
        bed.Engine.Tick();

        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
        Assert.Equal(1, bed.Network.Lifts);
        Assert.Null(bed.Record.Held);
    }

    // ── The cover ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCoverComesDown_WhenTheSessionRunsItsLength()
    {
        // A cover left up is a black screen with nothing on the machine able to clear it, so this is
        // the one thing about the cover that must never fail.
        var bed = new Bed();
        bed.Engine.Arm(30, blocksNetwork: false, dimsScreen: false, coversScreen: true, "a test");
        Assert.Equal(1, bed.Cover.Engagements);

        bed.Advance(TimeSpan.FromMinutes(30));
        bed.Engine.Tick();

        Assert.Equal(1, bed.Cover.Lifts);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    [Fact]
    public void ACoverIsTheOnlyLeverASessionNeeds()
    {
        // The cover alone is a session: the lever that made the screen dim too weak to matter is
        // reason enough to run one.
        var bed = new Bed();

        Assert.Equal(FocusArmOutcome.Armed,
                     bed.Engine.Arm(30, blocksNetwork: false, dimsScreen: false, coversScreen: true,
                                    "a test"));
        Assert.Equal(0, bed.Network.Engagements);
        Assert.Equal(0, bed.Screen.Engagements);
    }

    [Fact]
    public void ADeadRunThatLeftARecordBehind_NeverLeavesTheCoverUp()
    {
        // The application died with a cover up and the session record already cleared. The window
        // went with the process; lifting again at the next start is what makes that certain.
        var bed = new Bed();

        bed.Engine.Start();

        Assert.Equal(1, bed.Cover.Lifts);
        Assert.Equal(FocusSessionStage.Off, bed.Engine.Snapshot().Stage);
    }

    [Fact]
    public void ACoverThatCannotBeRaised_LeavesNothingEngagedBehindIt()
    {
        var bed = new Bed();
        bed.Cover.EngageSucceeds = false;

        var outcome = bed.Engine.Arm(60, blocksNetwork: true, dimsScreen: true, coversScreen: true,
                                     "a test");

        Assert.Equal(FocusArmOutcome.LeverFailed, outcome);
        Assert.Null(bed.Record.Held);
        Assert.Equal(1, bed.Network.Lifts);
        Assert.Equal(1, bed.Screen.Lifts);
    }

    // ── The length a start request runs for ─────────────────────────────────────────────────────

    [Fact]
    public void TheLengthChosenInTheStartBox_IsTheLengthTheSessionRunsFor()
    {
        // The dashboard's box hands its own value in; the stored default is what a request without
        // one falls back to. A box whose value were dropped would start an hour when it said 25.
        Assert.Equal(25, FocusStartRequest.Minutes(chosen: 25, storedDefault: 60));
        Assert.Equal(60, FocusStartRequest.Minutes(chosen: null, storedDefault: 60));
    }

    [Fact]
    public void ALengthOutsideTheRangeASessionAccepts_IsBroughtBackIntoIt() =>
        Assert.Equal((FocusSessionEngine.MinMinutes, FocusSessionEngine.MaxMinutes),
                     (FocusStartRequest.Minutes(0, 60), FocusStartRequest.Minutes(9_000, 60)));

    // ── The countdown ring ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRingIsWholeWhenASessionStartsAndGoneWhenItEnds()
    {
        var session = new FocusSnapshot(FocusSessionStage.Active, Noon, Noon.AddMinutes(60),
                                        false, false, true);

        Assert.Equal(1, FocusCoverCountdown.For(session, Noon)!.Value.FractionLeft, 3);
        Assert.Equal(0.5, FocusCoverCountdown.For(session, Noon.AddMinutes(30))!.Value.FractionLeft, 3);
        Assert.Equal(0, FocusCoverCountdown.For(session, Noon.AddMinutes(60))!.Value.FractionLeft, 3);
    }

    [Fact]
    public void NoSessionMeansNoRingAtAll() =>
        Assert.Null(FocusCoverCountdown.For(FocusSnapshot.None, Noon));

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
                     bed.Engine.Arm(120, blocksNetwork: true, dimsScreen: true, coversScreen: true, "a test"));
        return bed;
    }

    [Fact]
    public void AFirstCancelRequest_OpensTheWaitRatherThanEndingTheSession()
    {
        var bed = Running();

        bed.Engine.RequestCancel("a test");

        Assert.Equal(FocusSessionStage.Ending, bed.Engine.Snapshot().Stage);
        Assert.Equal(0, bed.Network.Lifts);
        Assert.NotNull(bed.Record.Held);
    }

    [Fact]
    public void ARepeatedRequestDuringTheWait_ChangesNothingAtAll()
    {
        var bed = Running();
        bed.Engine.RequestCancel("a test");

        // The wait runs on a fixed clock from the first request: a repeat neither shortens nor
        // restarts it, so pressing again buys nothing worth relying on.
        bed.Advance(TimeSpan.FromMinutes(4));
        bed.Engine.RequestCancel("a test");
        bed.Engine.Tick();

        Assert.Equal(FocusSessionStage.Ending, bed.Engine.Snapshot().Stage);
        Assert.Equal(0, bed.Network.Lifts);

        // One more minute is all the original wait had left, so the window opens on its own clock.
        bed.Advance(TimeSpan.FromMinutes(1));
        bed.Engine.Tick();
        Assert.Equal(FocusSessionStage.Confirm, bed.Engine.Snapshot().Stage);
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

    [Fact]
    public void ASecondRequestAfterTheConfirmWindow_LeavesTheSessionRunning()
    {
        var bed = Running();
        bed.Engine.RequestCancel("a test");

        bed.Advance(Wait + Window);
        bed.Engine.Tick();

        // Back to Active, and the attempt is forgotten: ending early again starts the wait afresh.
        Assert.Equal(FocusSessionStage.Active, bed.Engine.Snapshot().Stage);

        bed.Engine.RequestCancel("a test");
        Assert.Equal(FocusSessionStage.Ending, bed.Engine.Snapshot().Stage);
        Assert.Equal(0, bed.Network.Lifts);
    }

    [Fact]
    public void ACancelAttempt_NeverPausesTheCountdownToTheOriginalEndTime()
    {
        var bed = Running();
        var ends = bed.Engine.Snapshot().EndsAt;

        bed.Engine.RequestCancel("a test");
        bed.Advance(Wait);
        bed.Engine.Tick();

        Assert.Equal(ends, bed.Engine.Snapshot().EndsAt);
    }

    // ── The network lever's own refusals ────────────────────────────────────────────────────────

    private static FocusNetworkLever Lever(FakeNetworkTargets targets, FakeFirewallPolicy policy) =>
        new(new FirewallBlockPark(policy, new FakeFirewallRecord(), (_, _) => { }), targets,
            (_, _) => { });

    [Fact]
    public void TheNetworkLever_RefusesWhileTheBrokerPortIsAutomatic()
    {
        var targets = new FakeNetworkTargets { Port = null };

        string? refusal = Lever(targets, Firewall()).Refusal();

        Assert.NotNull(refusal);
        Assert.Contains("Automatic", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNetworkLever_RefusesWithNoBrokerConfiguredAtAll() =>
        Assert.NotNull(Lever(new FakeNetworkTargets { Host = "" }, Firewall()).Refusal());

    [Fact]
    public void TheNetworkLever_HasNothingToRefuseWithAHostAndAPinnedPort() =>
        Assert.Null(Lever(new FakeNetworkTargets(), Firewall()).Refusal());

    [Fact]
    public void AMachineWhoseBrokerIsNamedByAddress_NeedsNoNameResolutionException()
    {
        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "");

        Assert.Equal([FocusFirewallRules.BrokerRuleName], rules.Select(r => r.Name));
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

    [Fact]
    public void TheExceptionsGoInBeforeTheBlock_AndGoAgainWithIt()
    {
        var policy = Firewall();
        var park = new FirewallBlockPark(policy, new FakeFirewallRecord(), (_, _) => { });

        park.Engage(Exceptions(), "a test");
        Assert.Equal([FocusFirewallRules.BrokerRuleName, FocusFirewallRules.ResolverRuleName],
                     policy.Rules);

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
