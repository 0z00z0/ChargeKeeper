using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// A keep-awake preset starts a session rather than setting a value, so attribution runs over the
// session's own request — no service, no OS hold, no clock.
public class ActiveKeepAwakePresetPolicyTests
{
    private static List<KeepAwakeRequest> TwoPresets() =>
    [
        new(KeepAwakeKind.Duration,  TimeSpan.FromMinutes(30), null),
        new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)),
    ];

    private static KeepAwakeSession SessionFrom(KeepAwakeRequest request) =>
        new(request, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void MatchIndex_SessionStartedFromTheSecondPreset_ReturnsThatIndex()
    {
        // Deliberately the second entry: always returning the head of the list would pass on the first.
        var presets = TwoPresets();

        Assert.Equal(1, ActiveKeepAwakePresetPolicy.MatchIndex(presets, SessionFrom(presets[1])));
    }

    [Fact]
    public void MatchIndex_SessionMatchingNoPreset_ReturnsMinusOne()
    {
        // What the custom box produces: a span no saved preset carries.
        var typed = new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromMinutes(45), null);

        Assert.Equal(-1, ActiveKeepAwakePresetPolicy.MatchIndex(TwoPresets(), SessionFrom(typed)));
    }

    [Fact]
    public void MatchIndex_NetworkSession_ReturnsMinusOne()
    {
        // A network rule's hold is nobody's preset, so no row may claim it.
        var fromNetwork = new KeepAwakeRequest(KeepAwakeKind.UntilNetworkChange, null, null);

        Assert.Equal(-1, ActiveKeepAwakePresetPolicy.MatchIndex(TwoPresets(), SessionFrom(fromNetwork)));
    }

    [Fact]
    public void MatchIndex_NoSessionRunning_ReturnsMinusOne()
    {
        Assert.Equal(-1, ActiveKeepAwakePresetPolicy.MatchIndex(TwoPresets(), null));
    }

    [Fact]
    public void MatchIndex_TwoPresetsWithIdenticalSpans_ReturnsTheFirstInListOrder()
    {
        List<KeepAwakeRequest> presets =
        [
            new(KeepAwakeKind.Duration, TimeSpan.FromHours(2), null),
            new(KeepAwakeKind.Duration, TimeSpan.FromHours(2), null),
        ];

        Assert.Equal(0, ActiveKeepAwakePresetPolicy.MatchIndex(presets, SessionFrom(presets[1])));
    }

    [Fact]
    public void MatchIndex_SameSpanUnderADifferentName_ReturnsMinusOne()
    {
        // The name is part of the preset, so a renamed preset is a different one — which is also
        // what makes an edited preset stop claiming the session it started.
        List<KeepAwakeRequest> presets = [new(KeepAwakeKind.Duration, TimeSpan.FromHours(2), null, "Build")];
        var unnamed = new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromHours(2), null);

        Assert.Equal(-1, ActiveKeepAwakePresetPolicy.MatchIndex(presets, SessionFrom(unnamed)));
    }

    [Fact]
    public void MatchIndex_EmptyPresetList_ReturnsMinusOne()
    {
        var running = new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromHours(1), null);

        Assert.Equal(-1, ActiveKeepAwakePresetPolicy.MatchIndex([], SessionFrom(running)));
    }

    [Fact]
    public void MatchIndex_NullPresetList_ReturnsMinusOne()
    {
        var running = new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromHours(1), null);

        Assert.Equal(-1, ActiveKeepAwakePresetPolicy.MatchIndex(null, SessionFrom(running)));
    }
}
