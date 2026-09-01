using System;
using System.Linq;
using System.Text.Json;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The history graph's two colouring settings, away from the canvas: what the charge line takes at a
// given level and recorded state, and whether the fade beneath it is drawn. Nothing here builds a
// brush, so all six combinations are reachable without a window — which is the only way they can be
// asserted at all, the app being single-instance and elevated.
public class GraphColouringTests
{
    // An accent belonging to no scale, so "returned the accent" can never be confused with "landed
    // on a palette colour by coincidence".
    private const uint Accent = 0xFF123456;

    private static string Hex(uint argb) => $"{argb:X8}";

    // PowerState is internal, so it cannot appear in a public theory signature; the cases run inside
    // each fact instead, and every assertion names the state it failed on.
    private static PowerState[] EveryState() => Enum.GetValues<PowerState>();

    private static GraphLineColouring[] EveryLineOption() => Enum.GetValues<GraphLineColouring>();

    // ── The three line options ───────────────────────────────────────────────

    [Fact]
    public void OneColour_IsTheAccent_AtEveryLevelAndState()
    {
        for (int soc = 0; soc <= 100; soc++)
        {
            Assert.Equal(Hex(Accent),
                         Hex(GraphColouring.LineColourFor(GraphLineColouring.OneColour, soc, null, Accent)));
            foreach (var state in EveryState())
                Assert.Equal($"{state} at {soc} % {Hex(Accent)}",
                             $"{state} at {soc} % {Hex(GraphColouring.LineColourFor(GraphLineColouring.OneColour, soc, state, Accent))}");
        }
    }

    [Fact]
    public void ByLevel_IsTheOnBatteryScale_AndIgnoresTheRecordedState()
    {
        // By level claims nothing about what the machine was doing, so a point that happens to carry
        // a state must not be coloured differently from one that does not.
        for (int soc = 0; soc <= 100; soc++)
        {
            string expected = Hex(GaugePalette.Sample(GaugePalette.Draining, soc));
            Assert.Equal(expected, Hex(GraphColouring.LineColourFor(GraphLineColouring.ByLevel, soc, null, Accent)));
            foreach (var state in EveryState())
                Assert.Equal($"{state} at {soc} % {expected}",
                             $"{state} at {soc} % {Hex(GraphColouring.LineColourFor(GraphLineColouring.ByLevel, soc, state, Accent))}");
        }
    }

    [Fact]
    public void ByLevel_ReachesBothEndsOfTheOnBatteryScale()
    {
        // Named against the palette's own anchors, so picking the wrong scale fails here rather than
        // only in the sweep above.
        Assert.Equal(Hex(GaugePalette.Ember),
                     Hex(GraphColouring.LineColourFor(GraphLineColouring.ByLevel, 5, null, Accent)));
        Assert.Equal(Hex(GaugePalette.Lavender),
                     Hex(GraphColouring.LineColourFor(GraphLineColouring.ByLevel, 98, null, Accent)));
    }

    [Fact]
    public void ByLevelAndState_TakesTheScaleForTheStateRecordedAtThatPoint()
    {
        foreach (var state in EveryState())
            for (int soc = 0; soc <= 100; soc++)
                Assert.Equal($"{state} at {soc} % {Hex(GaugePalette.FillFor(soc, state))}",
                             $"{state} at {soc} % {Hex(GraphColouring.LineColourFor(GraphLineColouring.ByLevelAndState, soc, state, Accent))}");
    }

    [Fact]
    public void ByLevelAndState_SeparatesTheThreeStates_WhereTheScalesDescribeDifferentThings()
    {
        // 88 % is where all three scales are still on a ramp: nearly full on battery, nearly full and
        // charging, and nearly full held on mains. Telling those apart is what the option is for.
        var seen = EveryState()
            .Select(state => GraphColouring.LineColourFor(GraphLineColouring.ByLevelAndState, 88, state, Accent))
            .ToArray();

        Assert.Equal(3, seen.Distinct().Count());
        Assert.DoesNotContain(Accent, seen);
    }

    [Fact]
    public void ByLevelAndState_KeepsTheAccent_WhereAPointCarriesNoRecordedState()
    {
        // History written before the state was stored says nothing about what was happening, so it
        // keeps the colour it has always had rather than being painted as though it were draining.
        for (int soc = 0; soc <= 100; soc++)
        {
            string actual = Hex(GraphColouring.LineColourFor(GraphLineColouring.ByLevelAndState, soc, null, Accent));
            Assert.Equal($"at {soc} % {Hex(Accent)}", $"at {soc} % {actual}");
            Assert.NotEqual(Hex(GaugePalette.Sample(GaugePalette.Draining, soc)), actual);
        }
    }

    // ── The line option and the renderer's one-brush shortcut ────────────────

    [Fact]
    public void WhereTheLineDoesNotVaryByPoint_EveryPointIsTheAccent()
    {
        // The renderer takes one solid brush when VariesByPoint is false, so the two must agree or a
        // level-coloured line would be drawn flat.
        foreach (var mode in EveryLineOption())
        {
            if (GraphColouring.VariesByPoint(mode)) continue;
            foreach (var state in EveryState())
                for (int soc = 0; soc <= 100; soc++)
                    Assert.Equal($"{mode} {state} at {soc} % {Hex(Accent)}",
                                 $"{mode} {state} at {soc} % {Hex(GraphColouring.LineColourFor(mode, soc, state, Accent))}");
        }
    }

    [Fact]
    public void WhereTheLineVariesByPoint_TheColourActuallyMovesWithTheLevel()
    {
        foreach (var mode in EveryLineOption())
        {
            if (!GraphColouring.VariesByPoint(mode)) continue;

            var seen = Enumerable.Range(0, 101)
                .Select(soc => GraphColouring.LineColourFor(mode, soc, PowerState.Discharging, Accent))
                .ToArray();

            Assert.DoesNotContain(Accent, seen);
            Assert.True(seen.Distinct().Count() > 1, $"{mode} returned one colour at every level.");
        }
    }

    // ── The shading, and its independence from the line ──────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheShadingSettingAloneDecidesTheShading(bool shadingEnabled)
    {
        foreach (var mode in EveryLineOption())
            Assert.Equal($"{mode} {shadingEnabled}", $"{mode} {GraphColouring.ShouldShade(mode, shadingEnabled)}");

        // Nor does a line value from outside the enum reach the shading decision.
        Assert.Equal(shadingEnabled, GraphColouring.ShouldShade((GraphLineColouring)99, shadingEnabled));
    }

    [Fact]
    public void TheTwoSettingsAreIndependent_SoAllSixCombinationsExist()
    {
        // The count is the assertion: coupling the shading to the line option in any way collapses
        // six pairs to fewer.
        var seen = EveryLineOption()
            .SelectMany(_ => new[] { true, false },
                        (mode, shading) => (Line: mode, Shades: GraphColouring.ShouldShade(mode, shading)))
            .Distinct()
            .ToArray();

        Assert.Equal(6, seen.Length);
    }

    // ── Defaults, and values a settings file can hold ────────────────────────

    [Fact]
    public void TheDefaults_AreTodaysBehaviour()
    {
        var fresh = new AppSettings();

        Assert.Equal(GraphLineColouring.OneColour, fresh.GraphLineColouring);
        Assert.True(fresh.GraphShadingEnabled);
        Assert.False(GraphColouring.VariesByPoint(fresh.GraphLineColouring));
        Assert.True(GraphColouring.ShouldShade(fresh.GraphLineColouring, fresh.GraphShadingEnabled));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public void ALineValueNamingNoOption_FallsBackToOneColour_RatherThanThrowing(int stored)
    {
        var mode = (GraphLineColouring)stored;

        Assert.Equal(GraphLineColouring.OneColour, GraphColouring.Normalise(mode));
        Assert.False(GraphColouring.VariesByPoint(mode));

        Assert.Equal(Hex(Accent), Hex(GraphColouring.LineColourFor(mode, 50, null, Accent)));
        foreach (var state in EveryState())
            for (int soc = 0; soc <= 100; soc++)
                Assert.Equal($"{stored} {state} at {soc} % {Hex(Accent)}",
                             $"{stored} {state} at {soc} % {Hex(GraphColouring.LineColourFor(mode, soc, state, Accent))}");
    }

    [Fact]
    public void ANumberInSettingsJson_ReachesTheAppAsAnUndefinedOption_NotAFailedLoad()
    {
        // JsonStringEnumConverter accepts integers as well as names, so a hand-edited number does NOT
        // take the whole file down the "unreadable, defaults loaded" path — it arrives here as an
        // undefined enum value, which is the case the fallback above exists for.
        var loaded = JsonSerializer.Deserialize<AppSettings>(
            """{"GraphLineColouring":99,"GraphShadingEnabled":false}""")!;

        Assert.False(Enum.IsDefined(loaded.GraphLineColouring));
        Assert.Equal(GraphLineColouring.OneColour, GraphColouring.Normalise(loaded.GraphLineColouring));
        // The shading setting beside it is still honoured: one bad value does not disturb the other.
        Assert.False(GraphColouring.ShouldShade(loaded.GraphLineColouring, loaded.GraphShadingEnabled));
    }

    [Fact]
    public void ASettingsFileCarryingNeitherKey_LandsOnTodaysBehaviour()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.Equal(GraphLineColouring.OneColour, loaded.GraphLineColouring);
        Assert.True(loaded.GraphShadingEnabled);
    }

    [Fact]
    public void EveryOptionNameSurvivesTheRoundTripThroughSettingsJson()
    {
        foreach (var mode in EveryLineOption())
        {
            string json   = $$"""{"GraphLineColouring":"{{mode}}"}""";
            var    loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
            Assert.Equal(mode, loaded.GraphLineColouring);
        }
    }

    // ── The Settings combo's coupling to the enum ────────────────────────────

    [Fact]
    public void EveryLineOptionHasExactlyOneItemInTheSettingsCombo_TaggedWithItsOwnName()
    {
        // This combo is coupled by Tag rather than by position, which is what makes inserting an item
        // safe — but only while every Tag still names a member and every member still has an item.
        string markup = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")));

        int start = markup.IndexOf("x:Name=\"GraphLineColouringCombo\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "GraphLineColouringCombo is no longer declared in SettingsWindow.xaml.");
        int end = markup.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, "GraphLineColouringCombo's items are no longer inside it.");

        var tags = System.Text.RegularExpressions.Regex
            .Matches(markup[start..end], @"<ComboBoxItem\b[^>]*\bTag=""(?<tag>[^""]*)""")
            .Select(m => m.Groups["tag"].Value)
            .ToArray();

        Assert.Equal(EveryLineOption().Select(m => m.ToString()).Order(), tags.Order());
    }
}
