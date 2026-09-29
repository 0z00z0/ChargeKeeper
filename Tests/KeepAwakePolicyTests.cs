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

    // ShouldExpire

    [Fact]
    public void ShouldExpire_SleptPastTheExpiry_IsDueOnWake()
    {
        // The resume path's whole reason to exist: the timer's due time elapsed while suspended.
        var session = Session(new(KeepAwakeKind.Duration, TimeSpan.FromHours(1), null), At(9, 0));
        Assert.True(KeepAwakePolicy.ShouldExpire(At(14, 0), session.ExpiresAt));
    }

    // Persisted preset shape

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
