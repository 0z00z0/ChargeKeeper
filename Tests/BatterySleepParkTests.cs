using System;
using System.Collections.Generic;
using System.IO;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The battery sleep timeout is a Windows setting the application changes for the length of each
/// lid-close wait. A value not put back leaves the machine never sleeping on battery, with nothing on
/// screen saying why. Every case runs against a fake scheme: no test writes the real one.
/// </summary>
public class BatterySleepParkTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Other    = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private sealed class FakeScheme(BatterySleepValue active) : IBatterySleepSetting
    {
        public BatterySleepValue Active { get; set; } = active;
        public Dictionary<Guid, uint> Values { get; } = new() { [active.Scheme] = active.Seconds };
        public List<BatterySleepValue> Writes { get; } = [];
        public bool FailWrites { get; set; }
        public List<string>? Order { get; set; }

        public BatterySleepValue? ReadActive() => Active with { Seconds = Values[Active.Scheme] };

        public bool Write(BatterySleepValue value)
        {
            Order?.Add("write");
            if (FailWrites) return false;
            Writes.Add(value);
            Values[value.Scheme] = value.Seconds;
            return true;
        }
    }

    private sealed class FakeRecord : IBatterySleepRecord
    {
        public BatterySleepValue? Held { get; set; }
        public bool FailSaves { get; set; }
        public List<string>? Order { get; set; }

        public BatterySleepValue? Read() => Held;

        public bool Save(BatterySleepValue value)
        {
            Order?.Add("save");
            if (FailSaves) return false;
            Held = value;
            return true;
        }

        public void Clear() => Held = null;
    }

    private static BatterySleepPark Park(FakeScheme scheme, FakeRecord record, Func<bool> waiting,
                                         List<string>? lines = null) =>
        new(scheme, record, waiting, (what, cause) => lines?.Add($"{what} — cause: {cause}"));

    /// <summary>Every way a wait ends reaches <see cref="BatterySleepPark.Restore"/>: the value put back
    /// is the one taken, into the scheme it was taken from, even after the active plan changed.</summary>
    [Theory]
    [InlineData(600u)]
    [InlineData(1u)]
    [InlineData(18000u)]
    [InlineData(uint.MaxValue)]
    public void TheValueTakenIsTheValuePutBack_IntoItsOwnScheme(uint original)
    {
        var scheme = new FakeScheme(new BatterySleepValue(Balanced, original));
        var record = new FakeRecord();
        bool waiting = true;
        var park = Park(scheme, record, () => waiting);

        park.Park("a lid-close wait started");
        Assert.Equal(BatterySleepPark.Never, scheme.Values[Balanced]);
        Assert.Equal(new BatterySleepValue(Balanced, original), record.Held);

        // The plan is switched mid-wait; the restore must not land in it.
        scheme.Values[Other] = 900;
        scheme.Active = new BatterySleepValue(Other, 900);

        waiting = false;
        Assert.True(park.Restore("the lid was opened"));

        Assert.Equal(original, scheme.Values[Balanced]);
        Assert.Equal(900u, scheme.Values[Other]);
        Assert.Equal(new BatterySleepValue(Balanced, original), scheme.Writes[^1]);
        Assert.Null(record.Held);
    }

    /// <summary>The persisted capture is the crash recovery: a fresh process finding it at start puts
    /// the value back, and a restore that fails keeps it for the start after.</summary>
    [Fact]
    public void ARecordLeftByADeadProcessIsRestoredAtStart_AndKeptWhenTheRestoreFails()
    {
        var scheme = new FakeScheme(new BatterySleepValue(Balanced, BatterySleepPark.Never)) { FailWrites = true };
        var record = new FakeRecord { Held = new BatterySleepValue(Balanced, 600) };
        var lines  = new List<string>();

        Assert.False(Park(scheme, record, () => false, lines).Restore("crash recovery at startup"));
        Assert.Equal(new BatterySleepValue(Balanced, 600), record.Held);
        Assert.Contains(lines, l => l.Contains("retrying at next start", StringComparison.Ordinal));

        scheme.FailWrites = false;
        Assert.True(Park(scheme, record, () => false, lines).Restore("crash recovery at startup"));
        Assert.Equal(600u, scheme.Values[Balanced]);
        Assert.Null(record.Held);
        Assert.Contains(lines, l => l.StartsWith("Windows battery sleep timeout back to 10 min", StringComparison.Ordinal));
    }

    /// <summary>Nothing is written outside a wait, and a restore arriving while a new wait runs leaves
    /// that wait's park in place.</summary>
    [Fact]
    public void NothingChangesWhenNoWaitRuns()
    {
        var scheme = new FakeScheme(new BatterySleepValue(Balanced, 600));
        var record = new FakeRecord();

        Park(scheme, record, () => false).Park("a lid-close wait started");
        Assert.True(Park(scheme, record, () => false).Restore("the lid was opened"));

        Assert.Empty(scheme.Writes);
        Assert.Null(record.Held);

        record.Held = new BatterySleepValue(Balanced, 600);
        scheme.Values[Balanced] = BatterySleepPark.Never;
        Assert.True(Park(scheme, record, () => true).Restore("an earlier wait ended"));
        Assert.Empty(scheme.Writes);
        Assert.NotNull(record.Held);
    }

    /// <summary>The record reaches disk before the setting changes, and a record that cannot be saved
    /// leaves the setting alone — otherwise a crash strands the machine on "never".</summary>
    [Fact]
    public void TheRecordIsSavedBeforeTheSettingChanges_AndNoRecordMeansNoChange()
    {
        var order  = new List<string>();
        var scheme = new FakeScheme(new BatterySleepValue(Balanced, 600)) { Order = order };
        var record = new FakeRecord { Order = order };

        Park(scheme, record, () => true).Park("a lid-close wait started");
        Assert.Equal(["save", "write"], order);

        var refused = new FakeRecord { FailSaves = true };
        var fresh   = new FakeScheme(new BatterySleepValue(Balanced, 600));
        Park(fresh, refused, () => true).Park("a lid-close wait started");
        Assert.Empty(fresh.Writes);
        Assert.Equal(600u, fresh.Values[Balanced]);
    }

    /// <summary>A record still held — a restore failed — is never replaced by whatever the scheme now
    /// carries, or the original is lost for good at the next wait. A record for a plan that is no
    /// longer active is put back into that plan before the active one is parked.</summary>
    [Fact]
    public void AHeldRecordIsNeverRecapturedByTheNextWait()
    {
        var scheme = new FakeScheme(new BatterySleepValue(Balanced, 900));
        var record = new FakeRecord { Held = new BatterySleepValue(Balanced, 600) };

        Park(scheme, record, () => true).Park("a lid-close wait started");

        Assert.Equal(new BatterySleepValue(Balanced, 600), record.Held);
        Assert.Equal(BatterySleepPark.Never, scheme.Values[Balanced]);

        var switched = new FakeScheme(new BatterySleepValue(Other, 900));
        switched.Values[Balanced] = BatterySleepPark.Never;
        var stale = new FakeRecord { Held = new BatterySleepValue(Balanced, 600) };

        Park(switched, stale, () => true).Park("a lid-close wait started");

        Assert.Equal(600u, switched.Values[Balanced]);
        Assert.Equal(BatterySleepPark.Never, switched.Values[Other]);
        Assert.Equal(new BatterySleepValue(Other, 900), stale.Held);
    }

    /// <summary>The record survives settings.json exactly: a value the section type rounded or dropped
    /// would restore something the owner never set.</summary>
    [Fact]
    public void TheRecordSurvivesTheSettingsDocumentExactly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ck-sleep-park-{Guid.NewGuid():N}");
        string file = Path.Combine(dir, "settings.json");
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new AppSettings
            {
                LidDelaySavedBatterySleepSeconds = uint.MaxValue,
                LidDelaySavedBatterySleepScheme  = Balanced.ToString(),
            };
            Assert.True(SettingsService.WriteTo(settings, file));

            var loaded = SettingsService.ReadFrom(file)!;
            Assert.Equal(uint.MaxValue, loaded.LidDelaySavedBatterySleepSeconds);
            Assert.Equal(Balanced.ToString(), loaded.LidDelaySavedBatterySleepScheme);

            Assert.True(SettingsService.WriteTo(new AppSettings { LidDelaySavedBatterySleepSeconds = 0 }, file));
            Assert.Equal(0u, SettingsService.ReadFrom(file)!.LidDelaySavedBatterySleepSeconds);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// The service is static and owns a real power scheme, so the wiring of each ending path is read
    /// out of the source: the delay, the battery target and the temperature ceiling all end in
    /// <c>Complete</c>; the lid opening, Lid delay switched off and application exit all end in
    /// <c>CancelDelay</c>; a crash is covered at <c>Start</c>.
    /// </summary>
    [Fact]
    public void EveryWayAWaitEndsPutsTheTimeoutBack()
    {
        string source   = File.ReadAllText(RepoFiles.Find("Services/LidDelayService.cs"));
        string complete = SourceMethods.Body(source, "Complete");
        string cancel   = SourceMethods.Body(source, "CancelDelay");

        int restore = complete.IndexOf("_batterySleep.Restore(", StringComparison.Ordinal);
        Assert.True(restore >= 0, "a wait reaching its end does not put the battery sleep timeout back.");
        Assert.True(restore < complete.IndexOf("SuspendOffThisThread(", StringComparison.Ordinal),
                    "the restore must come before the suspend, which does not return until the machine wakes.");

        Assert.Contains("_batterySleep.Restore(", cancel, StringComparison.Ordinal);
        Assert.Contains("CancelDelay(", SourceMethods.Body(source, "Stop"), StringComparison.Ordinal);
        Assert.Contains("CancelDelay(", SourceMethods.Body(source, "SetEnabled"), StringComparison.Ordinal);
        Assert.Contains("CancelDelay(\"the lid was opened\")", SourceMethods.Body(source, "OnLidState"),
                        StringComparison.Ordinal);
        Assert.Contains("_batterySleep.Restore(", SourceMethods.Body(source, "Start"), StringComparison.Ordinal);
        Assert.Contains("_batterySleep.Park(", SourceMethods.Body(source, "StartDelay"), StringComparison.Ordinal);
    }
}
