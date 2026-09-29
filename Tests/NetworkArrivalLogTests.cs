using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class NetworkArrivalLogTests
{
    [Fact]
    public void Describe_ProfileCarryingAPresetAHoldAndAScript_NamesAllThree()
    {
        var rule = new NetworkLocationRule
        {
            Id = "office", Name = "NettPost [Kablet]", AdapterMac = "AA:BB:CC:DD:EE:FF",
            PresetName = "Office", KeepAwakeHere = true,
        };
        var settings = new AppSettings
        {
            NetworkProfilesEnabled = true,
            Presets = [new ThresholdPreset("Office", 60, 80)],
            NetworkLocationRules = [rule],
            Scripts = [new ScriptDefinition("s1", "Mount drives", ScriptTrigger.NetworkJoined, "net use", "office")],
        };

        Assert.Equal(
            "Network profile 'NettPost [Kablet]' matched: Smart Charge preset 'Office' (60/80); " +
            "keep-awake until the network changes; script 'Mount drives' on joining",
            NetworkArrivalLog.Describe(settings, rule));
    }

    [Fact]
    public void Describe_ProfileCarryingNothing_SaysNothingIsConfigured()
    {
        var rule = new NetworkLocationRule { Id = "cafe", Name = "Cafe", AdapterMac = "AA:BB:CC:DD:EE:FF" };
        var settings = new AppSettings { NetworkProfilesEnabled = true, NetworkLocationRules = [rule] };

        Assert.Equal("Network profile 'Cafe' matched: nothing is configured for it",
                     NetworkArrivalLog.Describe(settings, rule));
    }

    /// <summary>An event's handlers run in the order they subscribed, so the summary lands above the
    /// reactions' own lines only while it subscribes before all of them.</summary>
    [Fact]
    public void Start_IsWiredBeforeEveryOtherLocationChangeSubscriber()
    {
        string source = File.ReadAllText(RepoFiles.Find("App.xaml.cs"));
        int summary = source.IndexOf("NetworkArrivalLog.Start();", StringComparison.Ordinal);

        Assert.True(summary >= 0, "App.xaml.cs no longer starts NetworkArrivalLog.");
        foreach (var later in new[] { "NetworkScriptWatcher.Instance.Start();", "NetworkLocationService.Start();",
                                      "KeepAwakeService.Start();", "NetworkLocationService.LocationChanged +=",
                                      "new TrayMenu(" })
            Assert.True(source.IndexOf(later, StringComparison.Ordinal) > summary, $"{later} comes first.");
    }
}
