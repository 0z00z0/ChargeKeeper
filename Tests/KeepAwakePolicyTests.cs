using System.Text.Json;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The pure clock and expiry rules behind keep-awake — no OS hold, no timer.
public class KeepAwakePolicyTests
{
    // A fixed offset everywhere so the assertions are the same on any machine's local time zone.
    private static readonly TimeSpan Cet = TimeSpan.FromHours(2);

    private static DateTimeOffset At(int hour, int minute, int day = 15) =>
        new(2026, 8, day, hour, minute, 0, Cet);

    private static KeepAwakeSession Session(KeepAwakeRequest request, DateTimeOffset now) =>
        new(request, now, KeepAwakePolicy.ExpiryFor(request, now));

    // ExpiryFor

    [Fact]
    public void ExpiryFor_Duration_IsNowPlusTheSpan()
    {
        var now = At(9, 0);
        var expiry = KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(90), null), now);
        Assert.Equal(At(10, 30), expiry);
    }

    [Fact]
    public void ExpiryFor_UntilTime_StillAhead_IsToday()
    {
        var expiry = KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)), At(9, 0));
        Assert.Equal(At(17, 0), expiry);
    }

    [Fact]
    public void ExpiryFor_UntilTime_AlreadyPast_RollsOverToTomorrow()
    {
        // "until 17:00" asked at 17:05 means the next 17:00; any other reading expires immediately.
        var expiry = KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)), At(17, 5));
        Assert.Equal(At(17, 0, day: 16), expiry);
    }

    [Fact]
    public void ExpiryFor_UntilTime_ExactlyNow_RollsOverToTomorrow()
    {
        // At 17:00:00 sharp, "until 17:00" that resolved to right now would expire on its first tick.
        var expiry = KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)), At(17, 0));
        Assert.Equal(At(17, 0, day: 16), expiry);
    }

    // One Fact per kind rather than a [Theory]: KeepAwakeKind is internal, so it cannot appear in a
    // public test method's signature.
    [Fact]
    public void ExpiryFor_KindsWithoutAClock_HaveNoExpiry()
    {
        Assert.Null(KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.UntilNetworkChange, null, null), At(9, 0)));
        Assert.Null(KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.Indefinite, null, null), At(9, 0)));
    }

    [Fact]
    public void ExpiryFor_MissingOwnField_IsTreatedAsNoExpiry_NotAnInstantOne()
    {
        // Reachable by hand-editing settings.json. "Never expires" is recoverable by toggling it
        // off; "expires immediately" would make the feature look broken.
        Assert.Null(KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.Duration, null, null), At(9, 0)));
        Assert.Null(KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.UntilTime, null, null), At(9, 0)));
    }

    [Fact]
    public void ExpiryFor_ZeroOrNegativeDuration_HasNoExpiry()
    {
        Assert.Null(KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.Duration, TimeSpan.Zero, null), At(9, 0)));
        Assert.Null(KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(-5), null), At(9, 0)));
    }

    // DST-adjacent instants

    [Fact]
    public void ExpiryFor_Duration_AcrossTheDstBoundary_IsExactElapsedTime()
    {
        // The European autumn switch. A duration is elapsed time, so three hours is three hours of
        // real time regardless of what the wall clock does.
        var now = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(2));
        var expiry = KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.Duration, TimeSpan.FromHours(3), null), now);
        Assert.Equal(now.AddHours(3), expiry);
        Assert.Equal(TimeSpan.FromHours(3), expiry!.Value - now);
    }

    [Fact]
    public void ExpiryFor_UntilTime_ResolvesInNowsOwnOffset_AcrossTheDstBoundary()
    {
        // Accepted trade-off: an until-time resolves in now's UTC offset, so a rollover across a DST
        // switch lands an hour off in wall-clock terms. That keeps this a pure function rather than
        // a time-zone-rule lookup.
        var now = new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.FromHours(2));   // still CEST
        var expiry = KeepAwakePolicy.ExpiryFor(new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)), now);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.FromHours(2)), expiry);
    }

    // ShouldExpire

    [Fact]
    public void ShouldExpire_OnlyAtOrAfterTheExpiryInstant()
    {
        var expiry = At(17, 0);
        Assert.False(KeepAwakePolicy.ShouldExpire(At(16, 59), expiry));
        Assert.True(KeepAwakePolicy.ShouldExpire(expiry, expiry));      // exactly due counts
        Assert.True(KeepAwakePolicy.ShouldExpire(At(17, 1), expiry));
    }

    [Fact]
    public void ShouldExpire_NoExpiry_NeverExpires()
    {
        Assert.False(KeepAwakePolicy.ShouldExpire(At(23, 59), null));
    }

    [Fact]
    public void ShouldExpire_SleptPastTheExpiry_IsDueOnWake()
    {
        // The resume path's whole reason to exist: the timer's due time elapsed while suspended.
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromHours(1), null), At(9, 0));
        Assert.True(KeepAwakePolicy.ShouldExpire(At(14, 0), session.ExpiresAt));
    }

    // DescribeRemaining

    [Fact]
    public void DescribeRemaining_Duration_ReadsAsHoursAndMinutesLeft()
    {
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromHours(3), null), At(9, 0));
        Assert.Equal("2 h 12 m left", KeepAwakePolicy.DescribeRemaining(At(9, 48), session));
    }

    [Fact]
    public void DescribeRemaining_Duration_RoundsThePartialMinuteUp()
    {
        // A session started as "90 m" must read 1 h 30 m on its very first render, not 1 h 29 m
        // because a few milliseconds have gone.
        var start   = At(9, 0);
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(90), null), start);
        Assert.Equal("1 h 30 m left", KeepAwakePolicy.DescribeRemaining(start, session));
        Assert.Equal("1 h 30 m left", KeepAwakePolicy.DescribeRemaining(start.AddMilliseconds(100), session));
    }

    [Fact]
    public void DescribeRemaining_WholeHours_OmitTheZeroMinutes()
    {
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromHours(3), null), At(9, 0));
        Assert.Equal("3 h left", KeepAwakePolicy.DescribeRemaining(At(9, 0), session));
    }

    [Fact]
    public void DescribeRemaining_UnderAnHour_OmitsTheHours()
    {
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(30), null), At(9, 0));
        Assert.Equal("30 m left", KeepAwakePolicy.DescribeRemaining(At(9, 0), session));
    }

    [Fact]
    public void DescribeRemaining_PastTheExpiry_ReadsExpiring()
    {
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(30), null), At(9, 0));
        Assert.Equal("expiring", KeepAwakePolicy.DescribeRemaining(At(10, 0), session));
    }

    [Fact]
    public void DescribeRemaining_UntilTime_NamesTheClockTime_NotACountdown()
    {
        var session = Session(new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)), At(9, 0));
        Assert.Equal("until 17:00", KeepAwakePolicy.DescribeRemaining(At(9, 0), session));
    }

    [Fact]
    public void DescribeRemaining_UntilNetworkChange_NamesTheTrigger()
    {
        var session = Session(new(KeepAwakeKind.UntilNetworkChange, null, null), At(9, 0));
        Assert.Equal("until network changes", KeepAwakePolicy.DescribeRemaining(At(9, 0), session));
    }

    [Fact]
    public void DescribeRemaining_Indefinite_ReadsUntilTurnedOff()
    {
        var session = Session(new(KeepAwakeKind.Indefinite, null, null), At(9, 0));
        Assert.Equal("until turned off", KeepAwakePolicy.DescribeRemaining(At(9, 0), session));
    }

    // ShortLabel (dashboard preset chips)

    [Theory]
    [InlineData(30, "30m")]
    [InlineData(59, "59m")]
    [InlineData(60, "1h")]
    [InlineData(180, "3h")]
    [InlineData(90, "1h30")]
    [InlineData(125, "2h5")]
    public void ShortLabel_Duration_IsTheCompactSpan(int minutes, string expected) =>
        Assert.Equal(expected, KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(minutes), null)));

    [Fact]
    public void ShortLabel_UntilTime_IsTheClockTime() =>
        Assert.Equal("17:00", KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0))));

    [Fact]
    public void ShortLabel_UntilNetworkChange_IsTheFixedNetChip() =>
        Assert.Equal("Net", KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.UntilNetworkChange, null, null)));

    [Fact]
    public void ShortLabel_NamedPreset_ReadsAsItsName()
    {
        // Settings shows a named preset by name; a chip showing "1h30" for the same object is the
        // drift the single formatter exists to prevent.
        var named = new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromMinutes(90), null, "Meeting");
        Assert.Equal("Meeting", KeepAwakePolicy.ShortLabel(named));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShortLabel_NoUsableName_FallsBackToTheSpan(string? name)
    {
        var request = new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromMinutes(90), null, name);
        Assert.Equal("1h30", KeepAwakePolicy.ShortLabel(request));
    }

    [Fact]
    public void SpanLabel_IgnoresTheName_SoAnEditableSpanStaysParseable()
    {
        // What seeds the "Expires" box: seeding it with the name would make the next save reject it.
        var named = new KeepAwakeRequest(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0), "Meeting");
        Assert.Equal("17:00", KeepAwakePolicy.SpanLabel(named));
    }

    [Fact]
    public void ShortLabel_Indefinite_AndMalformedRequests_ReadAsNoExpiry()
    {
        // A kind whose own field is unset has no clock expiry, so labelling it with a span would
        // promise an end that never comes.
        Assert.Equal("∞", KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.Indefinite, null, null)));
        Assert.Equal("∞", KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.Duration, null, null)));
        Assert.Equal("∞", KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.Duration, TimeSpan.Zero, null)));
        Assert.Equal("∞", KeepAwakePolicy.ShortLabel(new(KeepAwakeKind.UntilTime, null, null)));
    }

    // DefaultRequest (bare "on", no span picked)

    [Fact]
    public void DefaultRequest_TakesTheFirstPreset_BecauseSettingsOrderIsPriorityOrder()
    {
        var presets = new List<KeepAwakeRequest>
        {
            new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)),
            new(KeepAwakeKind.Duration, TimeSpan.FromMinutes(30), null),
        };
        Assert.Equal(presets[0], KeepAwakePolicy.DefaultRequest(presets));
    }

    [Fact]
    public void DefaultRequest_WithNoPresets_HoldsUntilTurnedOff() =>
        Assert.Equal(new KeepAwakeRequest(KeepAwakeKind.Indefinite, null, null),
                     KeepAwakePolicy.DefaultRequest([]));

    // Persisted preset shape

    [Fact]
    public void KeepAwakePresets_RoundTripThroughJson()
    {
        // KeepAwakeRequest is a positional record that lands in settings.json, and hand-editing that
        // file is how presets are configured, so it has to survive a round trip.
        var settings = new AppSettings();
        string json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(settings.KeepAwakePresets, loaded!.KeepAwakePresets);   // records compare by value
        Assert.Equal(4, loaded.KeepAwakePresets.Count);
        Assert.Contains(loaded.KeepAwakePresets, p => p.Duration == TimeSpan.FromMinutes(30));
        Assert.Contains(loaded.KeepAwakePresets, p => p.Until == new TimeOnly(17, 0));
        Assert.Contains("\"UntilTime\"", json);   // the kind stays human-readable in the file
    }

    [Fact]
    public void KeepAwakePreset_Name_IsOptional_SoAnOlderSettingsFileStillLoads()
    {
        // A settings.json without the optional Name property must deserialise unchanged rather than
        // fail on the positional record's missing constructor argument.
        const string legacy = """
            {"KeepAwakePresets":[{"Kind":"Duration","Duration":"01:30:00","Until":null}]}
            """;
        var loaded = JsonSerializer.Deserialize<AppSettings>(legacy);

        Assert.NotNull(loaded);
        var preset = Assert.Single(loaded!.KeepAwakePresets);
        Assert.Equal(TimeSpan.FromMinutes(90), preset.Duration);
        Assert.Null(preset.Name);
    }

    [Fact]
    public void KeepAwakePreset_Name_RoundTripsWhenSet()
    {
        var settings = new AppSettings
        {
            KeepAwakePresets = [new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0), "End of day")],
        };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(loaded);
        Assert.Equal(settings.KeepAwakePresets, loaded!.KeepAwakePresets);   // records compare by value
        Assert.Equal("End of day", Assert.Single(loaded.KeepAwakePresets).Name);
    }

    // OS hold

    [Fact]
    public void SetThreadExecutionState_AcceptsTheHoldAndTheRelease()
    {
        // A wrong P/Invoke signature fails silently at runtime with a 0 return, which is the one
        // failure mode a keep-awake feature cannot afford. The call returns the previous state.
        uint held = NativeMethods.SetThreadExecutionState(
            NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
        uint released = NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        Assert.NotEqual(0u, held);
        Assert.NotEqual(0u, released);
    }
}
