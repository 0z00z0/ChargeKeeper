using System.Linq;
using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The seven publish groups: their keys, the defaults a fresh installation starts on, and the
/// per-key persistence that keeps a user's choices attached to the group they were made about.
/// </summary>
public class MqttPublishGroupTests
{
    private static PublishGroupSet NewSet() =>
        new(new FakeMqttSettingsStore(), MqttPublishGroups.Declared);

    [Fact]
    public void SevenGroupsAreDeclared_OnePerSettingsPage() =>
        Assert.Equal(
            ["battery_status", "smart_charge", "keep_awake", "lid_close",
             "notifications", "network", "app_diagnostics"],
            MqttPublishGroups.Declared.Select(g => g.Key));

    [Fact]
    public void EveryFeatureGroupShipsOn_AndOnlyAppDiagnosticsShipsOff()
    {
        // The published surface is the point of a feature, and a group is switched off to reduce it
        // rather than to opt into it. Diagnostics are the exception, and describe the app.
        foreach (var group in MqttPublishGroups.Declared)
            Assert.Equal(group.Key != MqttPublishGroups.AppDiagnostics, group.DefaultOn);
    }

    [Fact]
    public void OnlyTheGroupWhoseDefaultNeedsJustifying_CarriesADescription() =>
        Assert.Equal([MqttPublishGroups.AppDiagnostics],
                     MqttPublishGroups.Declared.Where(g => g.Description.Length > 0).Select(g => g.Key));

    [Fact]
    public void EveryGroup_SaysWhatIsInItBehindItsOwnIcon() =>
        // A row with no info text gets no icon at all, which is better than one opening on nothing.
        Assert.All(MqttPublishGroups.Declared, g => Assert.NotEqual("", g.Info));

    [Fact]
    public void EveryLabel_IsDistinctSoNoTwoRowsReadTheSame() =>
        Assert.Equal(MqttPublishGroups.Declared.Count,
                     MqttPublishGroups.Declared.Select(g => g.Label).Distinct().Count());

    [Fact]
    public void AGroupNobodyHasTouched_TakesItsOwnDeclaredDefault()
    {
        var snapshot = NewSet().Snapshot();

        Assert.True(snapshot.IsEnabled(MqttPublishGroups.BatteryStatus));
        Assert.False(snapshot.IsEnabled(MqttPublishGroups.AppDiagnostics));
    }

    [Fact]
    public void SwitchingAGroup_IsStoredAgainstItsKeyAndLeavesTheOthersAlone()
    {
        var set = NewSet();
        set.Set(MqttPublishGroups.Network, false);

        var snapshot = set.Snapshot();
        Assert.False(snapshot.IsEnabled(MqttPublishGroups.Network));
        foreach (var group in MqttPublishGroups.Declared.Where(g => g.Key != MqttPublishGroups.Network))
            Assert.Equal(group.DefaultOn, snapshot.IsEnabled(group.Key));
    }

    [Fact]
    public void SwitchingAGroupOnceOn_SurvivesTheDefaultBeingOff()
    {
        var set = NewSet();
        set.Set(MqttPublishGroups.AppDiagnostics, true);
        Assert.True(set.Snapshot().IsEnabled(MqttPublishGroups.AppDiagnostics));
    }

    [Fact]
    public void SeveralGroupsAtOnce_CostOneWriteRatherThanOnePerGroup()
    {
        var store = new FakeMqttSettingsStore();
        var set = new PublishGroupSet(store, MqttPublishGroups.Declared);

        set.Set([
            new(MqttPublishGroups.Network, false),
            new(MqttPublishGroups.Notifications, false),
        ]);

        Assert.Equal(1, store.Writes);
    }
}
