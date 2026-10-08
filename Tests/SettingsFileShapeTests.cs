using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The grouped on-disk shape: that nothing a person set is lost or zeroed on the way through, and
/// that a document this build cannot read is left untouched.
/// </summary>
public class SettingsFileShapeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ck-shape-test-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "settings.json");

    private static string FlatFixture =>
        System.IO.File.ReadAllText(RepoFiles.Find(Path.Combine("Tests", "Fixtures", "flat-settings.json")));

    private string WriteFixture()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, FlatFixture);
        return File_;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The loss guard. Reflects over <c>AppSettings</c> — a different source from the shape — so a
    /// persisted setting that never reached a group is named here rather than vanishing from the
    /// file. Sets, not counts: a count matches for the wrong reason.
    /// </summary>
    [Fact]
    public void EveryPersistedSettingLandsInExactlyOneGroup()
    {
        var persisted = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var grouped = typeof(SettingsFile)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(g => g.PropertyType.IsNested)          // skips the version key, which groups nothing
            .SelectMany(g => g.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(grouped.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));

        string missing = string.Join(", ", persisted.Except(grouped, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string extra   = string.Join(", ", grouped.Except(persisted, StringComparer.Ordinal).Order(StringComparer.Ordinal));

        Assert.Equal("", $"not in any group: [{missing}]  in a group but not persisted: [{extra}]"
                             .Replace("not in any group: []  in a group but not persisted: []", "", StringComparison.Ordinal));
    }

    /// <summary>The collections are where a silent loss would hurt most and show least: a dropped
    /// preset or network rule looks like the user deleted it.</summary>
    [Fact]
    public void TheCollectionsSurviveTheRoundTripByContent()
    {
        var before = SettingsService.ReadFrom(WriteFixture());
        Assert.NotNull(before);
        Assert.True(SettingsService.WriteTo(before!, File_));
        var after = SettingsService.ReadFrom(File_);
        Assert.NotNull(after);

        Assert.Equal(Describe(before!), Describe(after!));
        Assert.Equal(
            "Desk 50-55; Away 80-95; Standard 60-80 || " +
            "Duration/00:30:00//; Duration/03:00:00//; UntilTime//17:00:00/; UntilTime//09:00:00/Until 09:00 || " +
            "50; 15 || " +
            "Mobile@00:00:5E:00:53:01/192.0.2.0/24>Away awake=False; " +
            "Office@00:00:5E:00:53:02/198.51.100.0/24>Desk awake=True; " +
            "Second home@00:00:5E:00:53:03/203.0.113.0/24>Desk awake=False; " +
            "Home@00:00:5E:00:53:04/192.0.2.128/25>Desk awake=False",
            Describe(after!));
    }

    private static string Describe(AppSettings s) => string.Join(" || ",
        string.Join("; ", s.Presets.Select(p => $"{p.Name} {p.Start}-{p.Stop}")),
        // TimeOnly renders per culture; the TimeSpan it maps to does not.
        string.Join("; ", s.KeepAwakePresets.Select(k => $"{k.Kind}/{k.Duration}/{k.Until?.ToTimeSpan()}/{k.Name}")),
        string.Join("; ", s.LidDischargePresets.Select(t => $"{t.Percent}{(t.Name is null ? "" : " " + t.Name)}")),
        string.Join("; ", s.NetworkLocationRules.Select(r =>
            $"{r.Name}@{r.AdapterMac}/{r.IpCidr}>{r.PresetName} awake={r.KeepAwakeHere}")));

    /// <summary>An empty document reads as this application's defaults, not the section types'. The
    /// two differ: a section type declares no preset list and no delay, so binding an empty document
    /// as sections would leave every list empty and every level at zero.</summary>
    [Fact]
    public void AnAbsentFileYieldsNothingAndAnEmptyOneYieldsDefaults()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(SettingsService.ReadFrom(File_));

        System.IO.File.WriteAllText(File_, "{}");
        var loaded = SettingsService.ReadFrom(File_);

        Assert.NotNull(loaded);
        var defaults = new AppSettings();
        Assert.Equal(Describe(defaults), Describe(loaded!));
        Assert.Equal(defaults.LidDelayMinutes, loaded!.LidDelayMinutes);
        Assert.Empty(Directory.GetFiles(_dir, "settings.*.bad.json"));
    }

    /// <summary>An installed document carries a Notifications section without the sound and the five
    /// newer switches. It has to read every one of those notifications as on and the sound as
    /// Silent; the section type alone would read the switches as off.</summary>
    [Fact]
    public void NotificationKeysAbsentFromTheSectionReadAsOnAndSilent()
    {
        string[] newer =
        [
            "NotificationSound", "ChargeCompleteNoticeEnabled", "ChargingStartedNoticeEnabled",
            "SleptWhileHotWarningEnabled", "SettingsNotSavedWarningEnabled", "ScriptFailedWarningEnabled",
            "AwakeHoldWarningEnabled", "AwakeHoldWarningHours",
        ];

        Directory.CreateDirectory(_dir);
        var chosen = new AppSettings { NotificationSound = NotificationSound.IonGlide, ScriptFailedWarningEnabled = false };
        Assert.True(SettingsService.WriteTo(chosen, File_));

        string written = System.IO.File.ReadAllText(File_);
        Assert.Contains("\"NotificationSound\": \"IonGlide\"", written, StringComparison.Ordinal);
        Assert.False(SettingsService.ReadFrom(File_)!.ScriptFailedWarningEnabled);

        var root    = System.Text.Json.Nodes.JsonNode.Parse(written)!.AsObject();
        var section = root[SettingsFile.NotificationsKey]!.AsObject();
        foreach (string key in newer) Assert.True(section.Remove(key), $"{key} was not written.");
        System.IO.File.WriteAllText(File_, root.ToJsonString());

        var loaded = SettingsService.ReadFrom(File_)!;
        Assert.Equal(NotificationSound.Silent, loaded.NotificationSound);
        Assert.True(loaded.ChargeCompleteNoticeEnabled,    nameof(loaded.ChargeCompleteNoticeEnabled));
        Assert.True(loaded.ChargingStartedNoticeEnabled,   nameof(loaded.ChargingStartedNoticeEnabled));
        Assert.True(loaded.SleptWhileHotWarningEnabled,    nameof(loaded.SleptWhileHotWarningEnabled));
        Assert.True(loaded.SettingsNotSavedWarningEnabled, nameof(loaded.SettingsNotSavedWarningEnabled));
        Assert.True(loaded.ScriptFailedWarningEnabled,     nameof(loaded.ScriptFailedWarningEnabled));
        Assert.True(loaded.AwakeHoldWarningEnabled,        nameof(loaded.AwakeHoldWarningEnabled));
        Assert.Equal(AwakeHoldPolicy.DefaultWarnAfterHours, loaded.AwakeHoldWarningHours);
    }

    /// <summary>A section without the thermal ceiling, the sample rate or the Power activity switch
    /// reads the application's defaults, not 0 °C, the fastest rate and a hidden section.</summary>
    [Fact]
    public void ThermalCeilingSampleRateAndPowerActivityAbsentFromTheirSectionsReadAsDefaults()
    {
        Directory.CreateDirectory(_dir);
        Assert.True(SettingsService.WriteTo(new AppSettings(), File_));

        var root = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(File_))!.AsObject();
        Assert.True(root[SettingsFile.LidCloseKey]!.AsObject().Remove("LidThermalCeilingCelsius"));
        Assert.True(root[SettingsFile.DiagnosticsKey]!.AsObject().Remove("PerformanceSampleRate"));
        Assert.True(root[SettingsFile.AppearanceKey]!.AsObject().Remove("ShowPowerActivityInDashboard"));
        System.IO.File.WriteAllText(File_, root.ToJsonString());

        var loaded = SettingsService.ReadFrom(File_)!;
        Assert.Equal(new AppSettings().LidThermalCeilingCelsius, loaded.LidThermalCeilingCelsius);
        Assert.Equal(PerformanceSampleRates.Default, loaded.PerformanceSampleRate);
        Assert.True(loaded.ShowPowerActivityInDashboard);
    }

    /// <summary>Genuinely broken JSON is set aside and yields nothing — the flat path widens what
    /// counts as valid input, it does not remove the guard. Nothing is returned rather than the
    /// section types' own defaults, which are not this application's: handing those back would lower
    /// every preset list and every level to zero without a word.</summary>
    [Fact]
    public void ABrokenFileIsSetAsideAndYieldsNothing()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, "{ not json");

        Assert.Null(SettingsService.ReadFrom(File_));
        Assert.Single(Directory.GetFiles(_dir, "settings.*.bad.json"));
    }

    /// <summary>A document from a newer build is neither read nor overwritten, on either version key.
    /// Setting it aside as unreadable would let the next save replace it with defaults.</summary>
    [Theory]
    [InlineData(SettingsFile.VersionKey)]
    [InlineData(SettingsStore.StoreVersionKey)]
    public void AFileFromANewerBuildIsLeftUntouched(string versionKey)
    {
        Directory.CreateDirectory(_dir);
        Assert.True(SettingsService.WriteTo(new AppSettings(), File_));

        string newer = System.IO.File.ReadAllText(File_)
            .Replace($"\"{SettingsStore.StoreVersionKey}\": {SettingsFile.CurrentVersion}",
                     $"\"{versionKey}\": {SettingsFile.CurrentVersion + 1}", StringComparison.Ordinal);
        Assert.Contains($"\"{versionKey}\": {SettingsFile.CurrentVersion + 1}", newer, StringComparison.Ordinal);
        System.IO.File.WriteAllText(File_, newer);

        Assert.Null(SettingsService.ReadFrom(File_));
        Assert.False(SettingsService.WriteTo(new AppSettings(), File_));

        Assert.Equal(newer, System.IO.File.ReadAllText(File_));
        Assert.Empty(Directory.GetFiles(_dir, "settings.*.bad.json"));
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.pre-grouping-backup-*"));
    }
}
