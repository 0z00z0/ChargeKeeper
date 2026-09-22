using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The record of finished sessions: the three outcomes, the row that holds one, and the engine
/// writing exactly one row per ending.
/// </summary>
public class FocusHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The three words a row can carry. They are written into a file a person reads and
    /// parsed back out of one, so a rename makes every earlier row unreadable — the reason they are
    /// literals here rather than read from the code they guard.</summary>
    [Fact]
    public void TheThreeStoredOutcomeWordsAreFixed()
    {
        Assert.Equal("ran-to-time",  FocusHistoryService.Word(FocusSessionOutcome.RanToTime));
        Assert.Equal("ended-early",  FocusHistoryService.Word(FocusSessionOutcome.EndedEarly));
        Assert.Equal("found-stale",  FocusHistoryService.Word(FocusSessionOutcome.FoundStale));

        Assert.Equal("started,due,ended,levers,outcome", FocusHistoryService.HeaderColumns);
    }

    [Fact]
    public void ARowSurvivesBeingWrittenAndReadBack()
    {
        var entry = new FocusHistoryEntry(
            Noon, Noon.AddMinutes(60), Noon.AddMinutes(60),
            BlockedNetwork: true, DimmedScreen: false, CoveredScreen: true, BlockedInput: false,
            FocusSessionOutcome.RanToTime);

        Assert.True(FocusHistoryService.TryParse(FocusHistoryService.Format(entry), out var back));

        Assert.Equal(entry.StartedAt, back.StartedAt);
        Assert.Equal(entry.DueAt, back.DueAt);
        Assert.Equal(entry.EndedAt, back.EndedAt);
        Assert.Equal((true, false, true),
                     (back.BlockedNetwork, back.DimmedScreen, back.CoveredScreen));
        Assert.Equal(FocusSessionOutcome.RanToTime, back.Outcome);
    }

    /// <summary>A comma separates the columns, so the levers cannot be joined with one.</summary>
    [Fact]
    public void TheLeversNeverCarryTheColumnSeparator()
    {
        Assert.Equal("network+screen+cover+input", FocusHistoryService.Levers(true, true, true, true));
        Assert.Equal("none", FocusHistoryService.Levers(false, false, false, false));
        Assert.DoesNotContain(',', FocusHistoryService.Levers(true, true, true, true));
    }

    [Fact]
    public void TheHeaderAndAnythingElseThatIsNotARow_IsNotReadAsOne()
    {
        Assert.False(FocusHistoryService.TryParse(FocusHistoryService.HeaderColumns, out _));
        Assert.False(FocusHistoryService.TryParse("", out _));
        Assert.False(FocusHistoryService.TryParse(
            "2026-09-20T12:00:00+00:00,2026-09-20T13:00:00+00:00,2026-09-20T13:00:00+00:00,network,who-knows",
            out _));
    }

    // ── One row per ending, from the engine itself ──────────────────────────────────────────────

    private sealed class Bed
    {
        public DateTimeOffset Now = Noon;
        public FakeFocusLever Network { get; } = new();
        public FakeFocusLever Screen { get; } = new();
        public FakeFocusLever Cover { get; } = new();
        public FakeFocusLever Input { get; } = new();
        public FakeFocusSessionRecord Record { get; } = new();
        public List<FocusHistoryEntry> Written { get; } = [];
        public FocusSessionEngine Engine { get; }

        public Bed() => Engine = new FocusSessionEngine(
            Network, Screen, Cover, Input, Record, () => Now, (_, _) => { }, Written.Add);
    }

    [Fact]
    public void ASessionThatRanItsLength_IsWrittenDownAsHavingRunToTime()
    {
        var bed = new Bed();
        bed.Engine.Arm(60, blocksNetwork: true, dimsScreen: true, coversScreen: false, blocksInput: false, "a test");

        bed.Now = Noon.AddMinutes(60);
        bed.Engine.Tick();

        var written = Assert.Single(bed.Written);
        Assert.Equal(FocusSessionOutcome.RanToTime, written.Outcome);
        Assert.Equal(Noon, written.StartedAt);
        Assert.Equal(Noon.AddMinutes(60), written.DueAt);
        Assert.Equal(Noon.AddMinutes(60), written.EndedAt);
        Assert.Equal((true, true, false),
                     (written.BlockedNetwork, written.DimmedScreen, written.CoveredScreen));
    }

    [Fact]
    public void ASessionEndedFromHomeAssistant_KeepsTheEndTimeItNeverReached()
    {
        var bed = new Bed();
        bed.Engine.Arm(120, blocksNetwork: true, dimsScreen: false, coversScreen: false, blocksInput: false, "a test");

        bed.Engine.RequestCancel("a test");
        bed.Now = Noon + FocusSessionStages.CancelWait;
        bed.Engine.Tick();
        bed.Engine.RequestCancel("a test");

        var written = Assert.Single(bed.Written);
        Assert.Equal(FocusSessionOutcome.EndedEarly, written.Outcome);
        Assert.Equal(Noon.AddMinutes(120), written.DueAt);
        Assert.Equal(Noon + FocusSessionStages.CancelWait, written.EndedAt);
    }

    [Fact]
    public void ASessionFoundFinishedAtALaterStart_IsWrittenDownAsFoundStale()
    {
        var bed = new Bed { Now = Noon };
        bed.Record.Held = new FocusSessionRecord(
            Noon.AddMinutes(-150), Noon.AddMinutes(-90), true, true, true, true);

        bed.Engine.Start();

        var written = Assert.Single(bed.Written);
        Assert.Equal(FocusSessionOutcome.FoundStale, written.Outcome);
        Assert.Equal(Noon.AddMinutes(-90), written.DueAt);
        Assert.Equal(Noon, written.EndedAt);
    }

    [Fact]
    public void ASessionThatNeverArmed_IsNotWrittenDownAtAll()
    {
        var bed = new Bed();
        bed.Screen.EngageSucceeds = false;

        bed.Engine.Arm(60, blocksNetwork: true, dimsScreen: true, coversScreen: false, blocksInput: false, "a test");

        Assert.Empty(bed.Written);
    }

    // ── The file itself ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheNewestSessionsComeBackFirst()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ck-focus-{Guid.NewGuid():N}.csv");
        try
        {
            FocusHistoryService.UseTestPath(path);

            for (int i = 0; i < 8; i++)
                FocusHistoryService.Record(new FocusHistoryEntry(
                    Noon.AddHours(i), Noon.AddHours(i + 1), Noon.AddHours(i + 1),
                    true, false, false, false, FocusSessionOutcome.RanToTime));

            var recent = FocusHistoryService.Recent(5);

            Assert.Equal(5, recent.Count);
            Assert.Equal(Noon.AddHours(7), recent[0].StartedAt);
            Assert.Equal(Noon.AddHours(3), recent[4].StartedAt);
            Assert.Contains(FocusHistoryService.HeaderColumns, File.ReadAllText(path));
        }
        finally
        {
            FocusHistoryService.UseTestPath(Path.Combine(Path.GetTempPath(), $"ck-focus-unused.csv"));
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
