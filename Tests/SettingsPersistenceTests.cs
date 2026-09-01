using System;
using System.IO;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The settings write path, exercised against a real file. <c>SettingsService</c>'s own path is
/// fixed and shared, so these go through <c>WriteTo</c>/<c>ReadFrom</c> rather than swapping it —
/// swapping it would race every other test class reading <c>Current</c>.
/// </summary>
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ck-settings-test-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A setting held at its default must still be written. An omitted key is
    /// indistinguishable on disk from a setting that never reached the file at all.</summary>
    [Fact]
    public void ASettingAtItsDefaultIsStillNamedInTheFile()
    {
        Assert.True(SettingsService.WriteTo(new AppSettings(), File_));

        string json = System.IO.File.ReadAllText(File_);
        Assert.Contains("\"GraphLineColouring\"", json, StringComparison.Ordinal);
        Assert.Contains("\"GraphShadingEnabled\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGraphSettingsRoundTripAtTheirDefaults()
    {
        var written = new AppSettings();
        Assert.Equal(GraphLineColouring.OneColour, written.GraphLineColouring);
        Assert.True(written.GraphShadingEnabled);

        Assert.True(SettingsService.WriteTo(written, File_));
        var loaded = SettingsService.ReadFrom(File_);

        Assert.NotNull(loaded);
        Assert.Equal(GraphLineColouring.OneColour, loaded!.GraphLineColouring);
        Assert.True(loaded.GraphShadingEnabled);
    }

    /// <summary>Every combination, so neither setting can be persisted only where it differs from
    /// the default and neither can ride on the other's value.</summary>
    [Fact]
    public void TheGraphSettingsRoundTripAtEveryValue()
    {
        foreach (var mode in Enum.GetValues<GraphLineColouring>())
        foreach (bool shading in new[] { true, false })
        {
            var written = new AppSettings { GraphLineColouring = mode, GraphShadingEnabled = shading };
            Assert.True(SettingsService.WriteTo(written, File_));

            var loaded = SettingsService.ReadFrom(File_);

            Assert.NotNull(loaded);
            Assert.Equal($"{mode} {shading}",
                         $"{loaded!.GraphLineColouring} {loaded.GraphShadingEnabled}");
        }
    }

    /// <summary>The rest of the surface, so a key added beside the graph pair is covered by the same
    /// guard rather than needing a test of its own to be noticed.</summary>
    [Fact]
    public void TheWholeSettingsSurfaceRoundTrips()
    {
        var written = new AppSettings
        {
            LowBatteryWarningEnabled  = false,
            HighBatteryWarningEnabled = true,
            HighBatteryWarningPct     = 85,
            StartupDelaySeconds       = 5,
            IconMode                  = TrayIconMode.Numeric,
            GraphTimeScale            = GraphTimeScale.FourteenDays,
            DowntimeGapMinutes        = 0,
            GraphLineColouring        = GraphLineColouring.ByLevelAndState,
            GraphShadingEnabled       = false,
            LidDelayEnabled           = true,
            LidDelayOffAfterSleep     = true,
            LidDischargeEnabled       = true,
            LidDischargeTargetPercent = 30,
        };

        Assert.True(SettingsService.WriteTo(written, File_));
        var loaded = SettingsService.ReadFrom(File_);

        Assert.NotNull(loaded);
        Assert.Equal(
            $"{written.LowBatteryWarningEnabled} {written.HighBatteryWarningEnabled} " +
            $"{written.HighBatteryWarningPct} {written.StartupDelaySeconds} {written.IconMode} " +
            $"{written.GraphTimeScale} {written.DowntimeGapMinutes} {written.GraphLineColouring} " +
            $"{written.GraphShadingEnabled} {written.LidDelayEnabled} {written.LidDelayOffAfterSleep} " +
            $"{written.LidDischargeEnabled} {written.LidDischargeTargetPercent}",
            $"{loaded!.LowBatteryWarningEnabled} {loaded.HighBatteryWarningEnabled} " +
            $"{loaded.HighBatteryWarningPct} {loaded.StartupDelaySeconds} {loaded.IconMode} " +
            $"{loaded.GraphTimeScale} {loaded.DowntimeGapMinutes} {loaded.GraphLineColouring} " +
            $"{loaded.GraphShadingEnabled} {loaded.LidDelayEnabled} {loaded.LidDelayOffAfterSleep} " +
            $"{loaded.LidDischargeEnabled} {loaded.LidDischargeTargetPercent}");
    }

    /// <summary>A write that cannot land says so. Without a reported outcome a settings change that
    /// never reached disk is indistinguishable from one that did.</summary>
    [Fact]
    public void AWriteThatCannotLandIsReported()
    {
        // A path whose parent is an existing FILE: the directory can never be created, so the write
        // fails without depending on permissions the test runner may happen to hold.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        System.IO.File.WriteAllText(blocker, "");

        Assert.False(SettingsService.WriteTo(new AppSettings(), Path.Combine(blocker, "settings.json")));
    }
}
