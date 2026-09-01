using System;
using System.Collections.Generic;
using System.Threading;
using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;

namespace ChargeKeeper.Tests;

/// <summary>An <see cref="IMqttSettingsStore"/> over a field. The module's whole storage dependency
/// is three members, so a test needs no file and no directory.</summary>
internal sealed class FakeMqttSettingsStore : IMqttSettingsStore
{
    private readonly MqttSettings _settings = new();

    public int Writes { get; private set; }

    public MqttSettings Read() => _settings;

    public void Update(Action<MqttSettings> mutate)
    {
        mutate(_settings);
        Writes++;
        Changed?.Invoke();
    }

    public event Action? Changed;
}

/// <summary>Records what the charge-control seam was asked to do, so a command can be judged on the
/// call it produced rather than on a vendor RPC.</summary>
internal sealed class FakeChargeControl : IChargeControlActions
{
    public (int Start, int Stop) Current { get; set; } = (60, 80);

    public List<(int Start, int Stop)> Applied { get; } = [];
    public List<bool> SmartChargeSet { get; } = [];
    public List<string> PresetsApplied { get; } = [];
    public int ChargeToFullCalls { get; private set; }

    public (int Start, int Stop) CurrentThresholds() => Current;

    public void ApplyThresholds(int start, int stop) => Applied.Add((start, stop));

    public void SetSmartChargeEnabled(bool enable) => SmartChargeSet.Add(enable);

    public void ChargeToFullOnce() => ChargeToFullCalls++;

    public void ApplyPreset(string name) => PresetsApplied.Add(name);
}

/// <summary>Records every settings write a command produces, and offers whatever preset list a test
/// wants the two selects to advertise.</summary>
internal sealed class FakeSettingsActions : ISettingsActions
{
    /// <summary>Every call, as "member=value", in order. One list keeps a test's assertion about
    /// which setter ran as short as the assertion about the value.</summary>
    public List<string> Calls { get; } = [];

    public List<string> Presets { get; set; } = ["Daily", "Travel"];

    public IReadOnlyList<string> PresetNames() => Presets;

    public void SetKeepAwake(bool on) => Calls.Add($"KeepAwake={on}");
    public void StartKeepAwake(KeepAwakeRequest request) => Calls.Add($"StartKeepAwake={request.Kind}");
    public void SetKeepAwakeDisplayOn(bool on) => Calls.Add($"KeepAwakeDisplayOn={on}");
    public void SetLidDelay(bool on) => Calls.Add($"LidDelay={on}");
    public void SetLidDelayMinutes(int minutes) => Calls.Add($"LidDelayMinutes={minutes}");
    public void SetLidDelayLock(bool on) => Calls.Add($"LidDelayLock={on}");
    public void SetLidDelayOffAfterSleep(bool on) => Calls.Add($"LidDelayOffAfterSleep={on}");
    public void SetSmartStandby(bool on) => Calls.Add($"SmartStandby={on}");
    public void SetLowBatteryWarning(bool on) => Calls.Add($"LowBatteryWarning={on}");
    public void SetLowBatteryLevel(int percent) => Calls.Add($"LowBatteryLevel={percent}");
    public void SetHighBatteryWarning(bool on) => Calls.Add($"HighBatteryWarning={on}");
    public void SetHighBatteryLevel(int percent) => Calls.Add($"HighBatteryLevel={percent}");
    public void SetDrainWarning(bool on) => Calls.Add($"DrainWarning={on}");
    public void SetDrainRate(int percentPerHour) => Calls.Add($"DrainRate={percentPerHour}");
    public void SetNetworkProfiles(bool on) => Calls.Add($"NetworkProfiles={on}");
    public void SetUnknownNetworkPreset(string? name) => Calls.Add($"UnknownNetworkPreset={name ?? "<null>"}");
    public void SetStartupDelay(int seconds) => Calls.Add($"StartupDelay={seconds}");
    public void SetIconMode(TrayIconMode mode) => Calls.Add($"IconMode={mode}");
    public void SetDowntimeGap(int minutes) => Calls.Add($"DowntimeGap={minutes}");
}

/// <summary>Composes the entity table over fakes, and the two snapshots it reads. Every default is a
/// plausible mid-range machine, so a test states only the field it is about.</summary>
internal static class MqttTestBed
{
    /// <summary>What a working laptop with numeric charge thresholds reports.</summary>
    public static LiveState Live(
        int soc = 72, string? batteryState = null, int powerMw = 45_000, bool isCharging = true,
        bool onAc = true, string? health = "Good", int? remainingMinutes = 25,
        bool smartChargeEnabled = true, int? chargeStart = 60, int? chargeStop = 80,
        int? adapterWatts = 65, string? activePreset = "Daily",
        int? fullMwh = 51_000, int? designMwh = 57_000, bool lowPowerMode = false) =>
        new(soc, batteryState ?? LiveStateBuilder.StateCharging, lowPowerMode, powerMw, isCharging,
            onAc, health, remainingMinutes, smartChargeEnabled, chargeStart, chargeStop, adapterWatts,
            activePreset, fullMwh, designMwh);

    public static SurfaceState Surface(
        bool travelOverride = false, bool keepAwake = false, string keepAwakeFor = "1 h",
        DateTimeOffset? keepAwakeExpires = null, bool keepAwakeDisplayOn = false,
        bool lidDelay = true, int lidDelayMinutes = 10, bool lidDelayLock = true,
        bool lidDelayOffAfterSleep = false,
        bool smartStandby = false, bool lowBatteryWarning = true, int lowBatteryLevel = 20,
        bool highBatteryWarning = false, int highBatteryLevel = 90, bool drainWarning = true,
        int drainRate = 3, bool networkProfiles = true, string? unknownNetworkPreset = null,
        string? networkAlias = "Ethernet", string? networkIp = "10.0.0.5",
        string? networkAdapter = "Intel I219-V", string? matchedProfile = "Home",
        string appVersion = "1.17.0", int startupDelay = 5,
        TrayIconMode iconMode = TrayIconMode.Arc, int downtimeGap = 15) =>
        new(travelOverride, keepAwake, keepAwakeFor, keepAwakeExpires, keepAwakeDisplayOn,
            lidDelay, lidDelayMinutes, lidDelayLock, lidDelayOffAfterSleep,
            smartStandby, lowBatteryWarning, lowBatteryLevel,
            highBatteryWarning, highBatteryLevel, drainWarning, drainRate, networkProfiles,
            unknownNetworkPreset ?? PresetEditValidator.UnknownNetworkSentinel,
            networkAlias, networkIp, networkAdapter, matchedProfile, appVersion, startupDelay,
            iconMode, downtimeGap);

    /// <summary>The sources, with every reader answering the same snapshot every time.</summary>
    public static MqttEntitySources Sources(
        LiveState? live = null, SurfaceState? surface = null,
        PublishCapabilities? capabilities = null, Func<PublishCapabilities>? capabilityReader = null,
        IChargeControlActions? charge = null, ISettingsActions? settings = null)
    {
        var caps = capabilities ?? PublishCapabilities.Full;
        return new MqttEntitySources
        {
            Live = () => live,
            Surface = () => surface,
            Capabilities = capabilityReader ?? (() => caps),
            Charge = charge ?? new FakeChargeControl(),
            Settings = settings ?? new FakeSettingsActions(),
        };
    }

    /// <inheritdoc cref="MqttEntityCatalog.Build"/>
    public static MqttEntitySet Build(
        LiveState? live = null, SurfaceState? surface = null,
        PublishCapabilities? capabilities = null, Func<PublishCapabilities>? capabilityReader = null,
        IChargeControlActions? charge = null, ISettingsActions? settings = null) =>
        MqttEntityCatalog.Build(
            Sources(live, surface, capabilities, capabilityReader, charge, settings));

    /// <summary>The whole table with both snapshots present, for a test about declarations only.</summary>
    public static MqttEntitySet Declared() => Build(Live(), Surface());

    /// <summary>A group snapshot with every declared group in a given state, for the gating tests.</summary>
    public static PublishGroupSnapshot Groups(params (string Key, bool On)[] states)
    {
        var store = new FakeMqttSettingsStore();
        var set = new PublishGroupSet(store, MqttPublishGroups.Declared);
        foreach (var (key, on) in states) set.Set(key, on);
        return set.Snapshot();
    }

    /// <summary>Runs an accepted verdict's work to completion, so a test can assert on what it did.</summary>
    public static void Run(MqttCommandVerdict verdict)
    {
        Xunit.Assert.True(verdict.IsAccepted, $"The verdict was {verdict.Outcome}: {verdict.Detail}");
        verdict.Run!(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The command entity behind an id, for a test about what one payload does.</summary>
    public static MqttCommandEntity Command(MqttEntitySet set, string entityId) =>
        Xunit.Assert.IsAssignableFrom<MqttCommandEntity>(set.Find(entityId));
}
