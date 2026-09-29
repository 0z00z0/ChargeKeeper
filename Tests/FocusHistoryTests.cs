using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The record of finished sessions: the words a row stores its outcome under.
/// </summary>
public class FocusHistoryTests
{
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
}
