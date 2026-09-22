using System;
using System.Collections.Generic;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The flap: a charger pulled out and pushed back in every few seconds ran a script on every edge,
/// and whichever edge happened to be last decided what the machine was left doing. The failure is
/// concrete — the charger ends up out, and the script that ran last was the one for it being in, so
/// a sync client keeps running when it should have been paused.
/// </summary>
public class ScriptSettleWindowTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static bool NothingIsRunning(ScriptTrigger _) => false;

    private static Func<ScriptSubject, ScriptTrigger?> Reading(ScriptTrigger state) => _ => state;

    /// <summary>
    /// The whole behaviour in one run: the first event runs, the burst is swallowed, each swallowed
    /// event pushes the countdown out afresh, and the quiet that follows runs the state that is
    /// actually true — not the last event the burst happened to end on.
    /// </summary>
    [Fact]
    public void AFlapEndsInTheStateTheMachineIsActuallyIn()
    {
        var settle = new ScriptSettleWindow();
        var now = Start;

        // Unplugged: this one runs, and it is what the trailing run is measured against.
        Assert.True(settle.Observe(ScriptTrigger.MainsDisconnected, Window, now).Run);

        // Plugged, unplugged, plugged — a burst, none of it running, each pushing the window out.
        foreach (var trigger in new[] { ScriptTrigger.MainsConnected,
                                        ScriptTrigger.MainsDisconnected,
                                        ScriptTrigger.MainsConnected })
        {
            now += TimeSpan.FromSeconds(4);
            Assert.False(settle.Observe(trigger, Window, now).Run);

            // Still inside the window it has just pushed out, so nothing closes.
            Assert.Empty(settle.Expire(now + TimeSpan.FromSeconds(9), Window,
                                       Reading(ScriptTrigger.MainsConnected), NothingIsRunning));
        }

        // Quiet. The charger is in, and that reading — not the burst — is what decides.
        var closed = settle.Expire(now + TimeSpan.FromSeconds(11), Window,
                                   Reading(ScriptTrigger.MainsConnected), NothingIsRunning);

        var closure = Assert.Single(closed);
        Assert.Equal(SettleEnding.TrailingRun, closure.Ending);
        Assert.Equal(ScriptTrigger.MainsConnected, closure.State);
        Assert.Equal(ScriptSubject.Charger, closure.Subject);
        Assert.Equal(3, closure.Ignored);
    }

    /// <summary>
    /// The other half of the same guarantee: a state that came back to where it began runs nothing
    /// a second time. Running the connected script twice is not merely wasteful — the two runs would
    /// overlap and the log would say a firing was skipped, for no event anybody caused.
    /// </summary>
    [Fact]
    public void AStateThatEndedWhereItBegan_RunsNothingASecondTime()
    {
        var settle = new ScriptSettleWindow();

        Assert.True(settle.Observe(ScriptTrigger.MainsConnected, Window, Start).Run);
        Assert.False(settle.Observe(ScriptTrigger.MainsDisconnected, Window, Start + TimeSpan.FromSeconds(3)).Run);

        var closure = Assert.Single(settle.Expire(Start + TimeSpan.FromSeconds(20), Window,
                                                  Reading(ScriptTrigger.MainsConnected), NothingIsRunning));

        Assert.Equal(SettleEnding.AlreadyInThatState, closure.Ending);
        Assert.Equal(1, closure.Ignored);
    }

    /// <summary>A window that swallowed nothing closes without a word. An ordinary lid close is one
    /// line, and a second line saying nothing else happened would double the log for no reading.</summary>
    [Fact]
    public void AWindowThatSwallowedNothing_ClosesSilently()
    {
        var settle = new ScriptSettleWindow();

        Assert.True(settle.Observe(ScriptTrigger.LidClosed, Window, Start).Run);

        Assert.Empty(settle.Expire(Start + TimeSpan.FromSeconds(20), Window,
                                   Reading(ScriptTrigger.LidClosed), NothingIsRunning));
    }

    /// <summary>The lid and the charger settle apart. One countdown for both would let a lid close
    /// swallow a charger edge, which is a script not running for something that did happen.</summary>
    [Fact]
    public void EachSubjectKeepsItsOwnCountdown()
    {
        var settle = new ScriptSettleWindow();

        Assert.True(settle.Observe(ScriptTrigger.MainsConnected, Window, Start).Run);
        Assert.True(settle.Observe(ScriptTrigger.LidClosed, Window, Start + TimeSpan.FromSeconds(1)).Run);

        Assert.Equal(ScriptSubject.Charger, ScriptSettleWindow.SubjectOf(ScriptTrigger.MainsDisconnected));
        Assert.Equal(ScriptSubject.Lid, ScriptSettleWindow.SubjectOf(ScriptTrigger.LidOpened));

        // The network keeps none: leaving is about the profile the machine WAS on, which is not a
        // state that can be read back, and NetworkProfileTransitions already settles a lost reading.
        Assert.Null(ScriptSettleWindow.SubjectOf(ScriptTrigger.NetworkJoined));
        Assert.Null(ScriptSettleWindow.SubjectOf(ScriptTrigger.NetworkLeft));
    }

    /// <summary>A reading that could not be taken is not evidence that nothing moved, so nothing is
    /// claimed and nothing runs.</summary>
    [Fact]
    public void AStateThatCouldNotBeRead_RunsNothingAndSaysSo()
    {
        var settle = new ScriptSettleWindow();

        settle.Observe(ScriptTrigger.MainsConnected, Window, Start);
        settle.Observe(ScriptTrigger.MainsDisconnected, Window, Start + TimeSpan.FromSeconds(2));

        var closure = Assert.Single(settle.Expire(Start + TimeSpan.FromSeconds(20), Window,
                                                  _ => null, NothingIsRunning));

        Assert.Equal(SettleEnding.StateUnreadable, closure.Ending);
        Assert.Null(closure.State);
    }

    /// <summary>
    /// A run still going when the window passes holds the trailing run back for another window
    /// rather than losing it. The one-run-at-a-time gate would otherwise refuse it, and refusing it
    /// leaves the wrong state as the last word — the failure the window exists to stop.
    /// </summary>
    [Fact]
    public void ATrailingRunWaitsForARunInProgressRatherThanBeingDropped()
    {
        var settle = new ScriptSettleWindow();
        bool busy = true;

        settle.Observe(ScriptTrigger.MainsDisconnected, Window, Start);
        settle.Observe(ScriptTrigger.MainsConnected, Window, Start + TimeSpan.FromSeconds(2));

        var due = Start + TimeSpan.FromSeconds(20);
        Assert.Empty(settle.Expire(due, Window, Reading(ScriptTrigger.MainsConnected), _ => busy));

        busy = false;
        var closure = Assert.Single(settle.Expire(due + Window, Window,
                                                  Reading(ScriptTrigger.MainsConnected), _ => busy));

        Assert.Equal(SettleEnding.TrailingRun, closure.Ending);
        Assert.Equal(ScriptTrigger.MainsConnected, closure.State);
    }

    /// <summary>
    /// One line per window, not one per event. Twenty ignored events in a burst must not bury every
    /// other entry under twenty near-identical lines, and the count reaches the reader on the line
    /// that closes the window instead.
    /// </summary>
    [Fact]
    public void ABurstAnnouncesItselfOnce()
    {
        var settle = new ScriptSettleWindow();
        var now = Start;

        settle.Observe(ScriptTrigger.MainsConnected, Window, now);

        List<bool> announcements = [];
        for (int i = 0; i < 20; i++)
        {
            now += TimeSpan.FromSeconds(1);
            var trigger = i % 2 == 0 ? ScriptTrigger.MainsDisconnected : ScriptTrigger.MainsConnected;
            announcements.Add(settle.Observe(trigger, Window, now).AnnounceIgnored);
        }

        Assert.Single(announcements, announced => announced);
        Assert.True(announcements[0]);

        var closure = Assert.Single(settle.Expire(now + Window, Window,
                                                  Reading(ScriptTrigger.MainsDisconnected), NothingIsRunning));
        Assert.Equal(20, closure.Ignored);
    }
}
