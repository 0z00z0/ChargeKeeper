using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The settings document as the shared sectioned store holds it, exercised against a copy of an
/// installed document rather than a synthetic one. The fixture is the document an installation
/// carries, with the broker host, the adapter addresses, the network ranges and the location names
/// replaced by documentation placeholders — the repository is public.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ck-store-test-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "settings.json");

    private static string GroupedFixture =>
        System.IO.File.ReadAllText(RepoFiles.Find(Path.Combine("Tests", "Fixtures", "grouped-settings.json")));

    /// <summary>Writes the installed document into a directory of this test's own. The live file is
    /// never opened: the application is running and writing to it.</summary>
    private string WriteFixture(string? text = null)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, text ?? GroupedFixture);
        return File_;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Every value of the installed document, named one by one rather than compared against
    /// a round trip: a round trip agrees with itself when both directions are wrong.</summary>
    private static string Describe(AppSettings s) => string.Join("|",
        s.StartupDelaySeconds, s.IconMode, s.PromoteTrayIcons, s.TrayPromotionRestore.Count,
        s.LastSeenVersion,
        s.GraphTimeScale, s.GraphLineColouring, s.GraphShadingEnabled, s.DowntimeGapMinutes,
        s.GraphDisplay,
        string.Join(";", s.Presets.Select(p => $"{p.Name} {p.Start}-{p.Stop}")),
        s.TravelOverrideActive, s.TravelOverrideRevertStart, s.TravelOverrideRevertStop,
        s.NetworkProfilesEnabled,
        string.Join(";", s.NetworkLocationRules.Select(r =>
            $"{r.Name}@{r.AdapterMac}/{r.IpCidr}>{r.PresetName} awake={r.KeepAwakeHere}")),
        s.UnknownNetworkPresetName, s.NetworkRulesKeyedOnPhysicalAdapter,
        s.KeepAwakeDisplayOn,
        // TimeOnly renders per culture; the TimeSpan it maps to does not.
        string.Join(";", s.KeepAwakePresets.Select(k => $"{k.Kind}/{k.Duration}/{k.Until?.ToTimeSpan()}/{k.Name}")),
        s.LidDelayEnabled, s.LidDelayOffAfterSleep, s.LidDelayOffWhenCharging, s.LidDelayLockOnClose,
        s.LidDelayTimeEnabled, s.LidDelayMinutes,
        string.Join(";", s.LidDelayPresets.Select(p => $"{p.Minutes}/{p.Name}")),
        s.LidDischargeEnabled, s.LidDischargeTargetPercent,
        string.Join(";", s.LidDischargePresets.Select(p => $"{p.Percent}/{p.Name}")),
        s.LidThermalCeilingEnabled, s.LidThermalCeilingCelsius,
        s.LidThermalSleptAtCelsius, s.LidThermalSleptAtUtc,
        s.LidDelaySavedAcAction, s.LidDelaySavedDcAction, s.LidDelaySavedScheme,
        s.LowBatteryWarningPct, s.LowBatteryWarningEnabled,
        s.HighBatteryWarningPct, s.HighBatteryWarningEnabled,
        s.DrainAnomalyPercentPerHour, s.DrainAnomalyWarningEnabled,
        s.MqttLastGoodEndpoint?.Host, s.MqttLastGoodEndpoint?.Username, s.MqttLastGoodEndpoint?.Port,
        s.MqttLastGoodEndpoint?.Transport, s.MqttLastGoodEndpoint?.Encrypted,
        s.PerformanceGraphEnabled, s.PerformanceSampleRate,
        s.OneLineUntilItMatters, s.ShowPercentageIcon, s.HideGraphInDashboard,
        s.SettingsWindowX, s.SettingsWindowY, s.SettingsWindowWidth, s.SettingsWindowHeight);

    /// <summary>The value of every setting the installed document carries, spelled out rather than
    /// read back from the fixture, so a section that stops binding is named here instead of quietly
    /// handing back the section type's defaults.</summary>
    private const string InstalledValues =
        "0|Arc|True|1|1.47.1|" +
        "TwelveHours|ByLevelAndState|True|5|System|" +
        "Daily 60-80;Travel 80-100;Docking 45-55|False|||" +
        "True|Office [Docking]@00:00:5E:00:53:01/192.0.2.0/23>Docking awake=False;" +
        "Second home [Wireless]@00:00:5E:00:53:02/198.51.100.0/24>Daily awake=False|Daily|True|" +
        "False|Duration/00:30:00//;Duration/01:00:00//;Duration/03:00:00//;" +
        "UntilTime//17:00:00/;UntilTime//06:00:00/|" +
        "True|True|True|True|False|120|10/;30/;120/|True|10|30/;10/|True|85|||1|1|" +
        "381b4222-f694-41f0-9685-ff5bb260df2e|" +
        "15|True|90|True|3|True|" +
        "broker.example.invalid|mqtt|443|WebSocket|True|" +
        "True|OneHz|True|True|False|870|0|2100|2316";

    [Fact]
    public void EverySectionOfAnInstalledDocumentReadsBack()
    {
        var loaded = SettingsService.ReadFrom(WriteFixture());

        Assert.NotNull(loaded);
        Assert.Equal(InstalledValues, Describe(loaded!));
    }

    /// <summary>A write lands, the document is still valid afterwards, and the values survive a
    /// further read. The one moved value is named, so a write that saved nothing cannot pass.</summary>
    [Fact]
    public void AWriteLandsAndTheValuesSurviveAFurtherRead()
    {
        var loaded = SettingsService.ReadFrom(WriteFixture());
        Assert.NotNull(loaded);

        loaded!.LidDischargeTargetPercent = 42;
        Assert.True(SettingsService.WriteTo(loaded, File_));

        // Still a document, still every section and the two version keys.
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File_));
        Assert.Equal(
            $"{SettingsStore.StoreVersionKey},{SettingsFile.VersionKey}," +
            string.Join(",", SettingsFile.SectionNames),
            string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name)));

        var again = SettingsService.ReadFrom(File_);
        Assert.NotNull(again);
        Assert.Equal(InstalledValues.Replace("|True|10|30/;10/|", "|True|42|30/;10/|", StringComparison.Ordinal),
                     Describe(again!));
    }

    /// <summary>This application's own version key is not the store's, and the store does not touch a
    /// top-level key it does not own. The two stand side by side, and the key an installation already
    /// carries keeps its value.</summary>
    [Fact]
    public void AVersionKeyTheDocumentAlreadyCarriesSurvivesAWrite()
    {
        var loaded = SettingsService.ReadFrom(WriteFixture());
        loaded!.LidDelayMinutes = 45;
        Assert.True(SettingsService.WriteTo(loaded, File_));

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File_));
        Assert.Equal(SettingsFile.CurrentVersion, SettingsFile.ReadVersion(doc.RootElement));
        Assert.True(doc.RootElement.TryGetProperty(SettingsStore.StoreVersionKey, out var store));
        Assert.Equal(SettingsFile.CurrentVersion, store.GetInt32());
    }

    /// <summary>A document with no version key of any kind reads and writes. Absent must never be
    /// read as newer: that would refuse every write and lock a person out of their own settings.</summary>
    [Fact]
    public void ADocumentWithNoVersionKeyReadsAndWrites()
    {
        string text = GroupedFixture.Replace($"\"{SettingsFile.VersionKey}\": {SettingsFile.CurrentVersion},",
                                             "", StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{SettingsFile.VersionKey}\"", text, StringComparison.Ordinal);

        var loaded = SettingsService.ReadFrom(WriteFixture(text));
        Assert.NotNull(loaded);
        Assert.Equal(InstalledValues, Describe(loaded!));

        loaded!.LidDelayMinutes = 45;
        Assert.True(SettingsService.WriteTo(loaded, File_));
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File_));
        Assert.Equal($"{SettingsStore.StoreVersionKey}," + string.Join(",", SettingsFile.SectionNames),
                     string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name)));
    }

    /// <summary>An old lower-case version key belongs to neither side, so it is carried across
    /// untouched and the document ends up holding it beside the store's own.</summary>
    [Fact]
    public void AnOldLowerCaseVersionKeyIsCarriedAcrossUntouched()
    {
        string text = GroupedFixture.Replace(
            $"\"{SettingsFile.VersionKey}\": {SettingsFile.CurrentVersion},",
            $"\"{SettingsFile.VersionKey}\": {SettingsFile.CurrentVersion},\r\n  \"version\": 1,",
            StringComparison.Ordinal);

        var loaded = SettingsService.ReadFrom(WriteFixture(text));
        Assert.NotNull(loaded);
        loaded!.LidDelayMinutes = 45;
        Assert.True(SettingsService.WriteTo(loaded, File_));

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File_));
        Assert.Equal(
            $"{SettingsStore.StoreVersionKey},{SettingsFile.VersionKey},version," +
            string.Join(",", SettingsFile.SectionNames),
            string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
    }

    /// <summary>A save that moves nothing leaves the document byte for byte as it was, so no section
    /// needs comparing before it is offered. Every section that does move lays the whole document
    /// down again, measured at roughly 24 ms, which is what makes the difference worth pinning.</summary>
    [Fact]
    public void ASaveThatMovesNothingRewritesNothing()
    {
        string before = System.IO.File.ReadAllText(WriteFixture());

        Assert.True(SettingsService.WriteTo(SettingsService.ReadFrom(File_)!, File_));

        Assert.Equal(before, System.IO.File.ReadAllText(File_));
    }

    /// <summary>
    /// The store's one silent failure, refused rather than followed. A section is bound by its exact
    /// spelling: one the document spells in another case binds nothing and hands back the type's
    /// declared defaults, with no exception, no event and nothing in a log. Reading that as settings
    /// would load zeros over a person's presets, and writing it would leave the document carrying two
    /// spellings of one section.
    /// </summary>
    [Fact]
    public void ASectionSpelledInAnotherCaseIsRefusedRatherThanReadAsDefaults()
    {
        string text = GroupedFixture.Replace($"\"{SettingsFile.SmartChargeKey}\":", "\"smartcharge\":",
                                             StringComparison.Ordinal);
        Assert.Contains("\"smartcharge\":", text, StringComparison.Ordinal);

        string path = WriteFixture(text);

        Assert.Null(SettingsService.ReadFrom(path));
        Assert.False(SettingsService.WriteTo(new AppSettings(), path));
        Assert.Equal(text, System.IO.File.ReadAllText(path));
    }

    /// <summary>
    /// A refused write reaches a person. The store returns the refusal rather than raising it, so
    /// without this wiring a settings change that never reached disk looks saved on screen and is
    /// gone at the next start. Asserted against the shipped source, because the write path is fixed
    /// to the installed document and cannot be exercised here.
    /// </summary>
    [Fact]
    public void ARefusedSaveIsWiredToANotification()
    {
        string app = System.IO.File.ReadAllText(RepoFiles.Find("App.xaml.cs"));
        Assert.Contains("SettingsService.SaveFailed", app, StringComparison.Ordinal);
        Assert.Contains("ToastService.NotifySettingsNotSaved", app, StringComparison.Ordinal);

        string service = System.IO.File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "SettingsService.cs")));
        // Raised only where the write did not land, and only once until one does.
        Assert.Contains("report = !saved && !_saveFailureReported;", service, StringComparison.Ordinal);
        Assert.Contains("_saveFailureReported = !saved;", service, StringComparison.Ordinal);
        Assert.Contains("if (report) SaveFailed?.Invoke();", service, StringComparison.Ordinal);
    }
}
