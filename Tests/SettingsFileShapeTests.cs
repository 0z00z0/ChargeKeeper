using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The grouped on-disk shape: that a flat file still reads, that converting it loses nothing, that
/// the original is kept, and that the key order is the one the Settings window presents.
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
    /// The key order the file is written in, spelled out rather than read back from the shape: a
    /// test deriving the sequence from the writer's own source follows a reordering instead of
    /// catching it. Groups run in navigation order; rows run in the order they appear on the page.
    /// </summary>
    private static readonly string[] ExpectedKeyOrder =
    [
        "version",
        "general",
        "general.StartupDelaySeconds",
        "general.IconMode",
        "general.ShowPercentageIcon",
        "general.PromoteTrayIcons",
        "general.TrayPromotionRestore",
        "general.LastSeenVersion",
        "graph",
        "graph.GraphTimeScale",
        "graph.GraphLineColouring",
        "graph.GraphShadingEnabled",
        "graph.DowntimeGapMinutes",
        "graph.GraphDisplay",
        "smart_charge",
        "smart_charge.Presets",
        "smart_charge.TravelOverrideActive",
        "smart_charge.TravelOverrideRevertStart",
        "smart_charge.TravelOverrideRevertStop",
        "network",
        "network.NetworkProfilesEnabled",
        "network.NetworkLocationRules",
        "network.UnknownNetworkPresetName",
        "network.NetworkRulesKeyedOnPhysicalAdapter",
        "keep_awake",
        "keep_awake.KeepAwakeDisplayOn",
        "keep_awake.KeepAwakePresets",
        "lid_close",
        "lid_close.LidDelayEnabled",
        "lid_close.LidDelayOffAfterSleep",
        "lid_close.LidDelayLockOnClose",
        "lid_close.LidDelayTimeEnabled",
        "lid_close.LidDelayMinutes",
        "lid_close.LidDelayPresets",
        "lid_close.LidDischargeEnabled",
        "lid_close.LidDischargeTargetPercent",
        "lid_close.LidDischargePresets",
        "lid_close.LidThermalCeilingEnabled",
        "lid_close.LidThermalCeilingCelsius",
        "lid_close.LidThermalSleptAtCelsius",
        "lid_close.LidThermalSleptAtUtc",
        "lid_close.LidDelaySavedAcAction",
        "lid_close.LidDelaySavedDcAction",
        "lid_close.LidDelaySavedScheme",
        "notifications",
        "notifications.LowBatteryWarningPct",
        "notifications.LowBatteryWarningEnabled",
        "notifications.HighBatteryWarningPct",
        "notifications.HighBatteryWarningEnabled",
        "notifications.DrainAnomalyPercentPerHour",
        "notifications.DrainAnomalyWarningEnabled",
        "mqtt",
        "mqtt.MqttLastGoodEndpoint",
        "diagnostics",
        "diagnostics.PerformanceGraphEnabled",
        "diagnostics.PerformanceSampleRate",
        "appearance",
        "appearance.OneLineUntilItMatters",
        "window",
        "window.SettingsWindowX",
        "window.SettingsWindowY",
        "window.SettingsWindowWidth",
        "window.SettingsWindowHeight",
    ];

    /// <summary>Group name then each of its keys, in the order they appear in the written file.</summary>
    private static List<string> KeyOrderOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var order = new List<string>();
        foreach (var group in doc.RootElement.EnumerateObject())
        {
            order.Add(group.Name);
            if (group.Value.ValueKind != JsonValueKind.Object) continue;   // the version key
            foreach (var leaf in group.Value.EnumerateObject())
                order.Add($"{group.Name}.{leaf.Name}");
        }
        return order;
    }

    [Fact]
    public void TheFileIsWrittenInTheOrderTheSettingsWindowPresents()
    {
        Assert.True(SettingsService.WriteTo(new AppSettings(), WriteFixture()));

        Assert.Equal(ExpectedKeyOrder, KeyOrderOf(System.IO.File.ReadAllText(File_)));
    }

    /// <summary>Same assertion on the converted file: a migrated flat file must come out in the new
    /// order too, not in whatever order it was read.</summary>
    [Fact]
    public void AConvertedFlatFileIsWrittenInTheSameOrder()
    {
        var loaded = SettingsService.ReadFrom(WriteFixture());
        Assert.NotNull(loaded);
        Assert.True(SettingsService.WriteTo(loaded!, File_));

        Assert.Equal(ExpectedKeyOrder, KeyOrderOf(System.IO.File.ReadAllText(File_)));
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

    /// <summary>A flat file is valid input, so it must load rather than be copied aside as
    /// unreadable and replaced with defaults.</summary>
    [Fact]
    public void AFlatFileLoadsEveryScalarValue()
    {
        var s = SettingsService.ReadFrom(WriteFixture());

        Assert.NotNull(s);
        Assert.Equal(
            "10|Arc|SixHours|ByLevelAndState|True|1|False|||True|40|True|80|True|3|False|True|30|False|" +
            "False|False|50||||True|True|Standard|broker.example.invalid|443|620|72|2600|2244",
            string.Join('|',
                s!.StartupDelaySeconds, s.IconMode, s.GraphTimeScale, s.GraphLineColouring,
                s.GraphShadingEnabled, s.DowntimeGapMinutes,
                s.TravelOverrideActive, s.TravelOverrideRevertStart, s.TravelOverrideRevertStop,
                s.LowBatteryWarningEnabled, s.LowBatteryWarningPct,
                s.HighBatteryWarningEnabled, s.HighBatteryWarningPct,
                s.DrainAnomalyWarningEnabled, s.DrainAnomalyPercentPerHour,
                s.KeepAwakeDisplayOn,
                s.LidDelayEnabled, s.LidDelayMinutes, s.LidDelayLockOnClose, s.LidDelayOffAfterSleep,
                s.LidDischargeEnabled, s.LidDischargeTargetPercent,
                s.LidDelaySavedAcAction, s.LidDelaySavedDcAction, s.LidDelaySavedScheme,
                s.NetworkProfilesEnabled, s.NetworkRulesKeyedOnPhysicalAdapter,
                s.UnknownNetworkPresetName,
                s.MqttLastGoodEndpoint?.Host, s.MqttLastGoodEndpoint?.Port,
                s.SettingsWindowX, s.SettingsWindowY, s.SettingsWindowWidth, s.SettingsWindowHeight));
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

    /// <summary>The original is kept before the first grouped write replaces it, under a name that
    /// says what it is.</summary>
    [Fact]
    public void TheFlatOriginalIsCopiedAsideBeforeTheFirstGroupedWrite()
    {
        var loaded = SettingsService.ReadFrom(WriteFixture());
        Assert.True(SettingsService.WriteTo(loaded!, File_));

        var copies = Directory.GetFiles(_dir, "settings.json.pre-grouping-backup-*");
        Assert.Single(copies);
        Assert.Equal(FlatFixture, System.IO.File.ReadAllText(copies[0]));
    }

    /// <summary>Once grouped, the file is not copied aside again: the backup marks the one
    /// conversion, not every save.</summary>
    [Fact]
    public void AnAlreadyGroupedFileIsNotCopiedAside()
    {
        Directory.CreateDirectory(_dir);
        Assert.True(SettingsService.WriteTo(new AppSettings(), File_));
        Assert.True(SettingsService.WriteTo(new AppSettings(), File_));

        Assert.Empty(Directory.GetFiles(_dir, "settings.json.pre-grouping-backup-*"));
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
    }

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
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
    }

    /// <summary>Genuinely broken JSON must still take the unreadable branch — the flat path widens
    /// what counts as valid input, it does not remove the guard.</summary>
    [Fact]
    public void ABrokenFileIsStillTreatedAsUnreadable()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, "{ not json");

        Assert.Null(SettingsService.ReadFrom(File_));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
    }

    /// <summary>The discriminator, tested both ways: the flat file has no version key, and the file
    /// this build writes declares the version this build reads.</summary>
    [Fact]
    public void TheGroupedAndFlatShapesAreToldApartByTheVersionKey()
    {
        using var flat = JsonDocument.Parse(FlatFixture);
        Assert.Null(SettingsFile.ReadVersion(flat.RootElement));

        Assert.True(SettingsService.WriteTo(new AppSettings(), WriteFixture()));
        using var grouped = JsonDocument.Parse(System.IO.File.ReadAllText(File_));
        Assert.Equal(SettingsFile.CurrentVersion, SettingsFile.ReadVersion(grouped.RootElement));
    }

    /// <summary>A file from a newer build is neither read nor overwritten. Copying it aside as
    /// unreadable would let the next save replace it with defaults.</summary>
    [Fact]
    public void AFileFromANewerBuildIsLeftUntouched()
    {
        Directory.CreateDirectory(_dir);
        Assert.True(SettingsService.WriteTo(new AppSettings(), File_));
        string newer = System.IO.File.ReadAllText(File_)
            .Replace($"\"version\": {SettingsFile.CurrentVersion}",
                     $"\"version\": {SettingsFile.CurrentVersion + 1}", StringComparison.Ordinal);
        Assert.Contains($"\"version\": {SettingsFile.CurrentVersion + 1}", newer, StringComparison.Ordinal);
        System.IO.File.WriteAllText(File_, newer);

        Assert.Null(SettingsService.ReadFrom(File_));
        Assert.False(SettingsService.WriteTo(new AppSettings(), File_));

        Assert.Equal(newer, System.IO.File.ReadAllText(File_));
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.unreadable-*"));
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.pre-grouping-backup-*"));
    }
}
