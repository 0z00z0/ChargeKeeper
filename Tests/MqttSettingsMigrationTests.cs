using System.Text.Json;
using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The move of the broker block out of settings.json and into the module's own mqtt.json.
/// </summary>
/// <remarks>
/// The rule the network-rule migration had to learn, applied here: <b>an absent key is not a
/// value</b>. A file that says nothing about MQTT is already migrated or brand new, and either way
/// the defaults are what should stand; a key that is present is carried exactly; nothing is ever
/// cleared on the strength of a key that is not there.
/// </remarks>
public class MqttSettingsMigrationTests
{
    private static JsonElement Legacy(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static MqttSettings Migrated(string json)
    {
        var target = new MqttSettings();
        MqttSettingsMigration.Apply(Legacy(json), target);
        return target;
    }

    // ── Nothing to move ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFileWithNoMqttKeysAtAll_IsAlreadyMigratedAndNothingIsWritten()
    {
        var target = new MqttSettings { Host = "broker.lan", Enabled = true };
        Assert.False(MqttSettingsMigration.Apply(Legacy("""{"StartupDelaySeconds":5}"""), target));

        // Not cleared, not defaulted: the file said nothing, so it decided nothing.
        Assert.Equal("broker.lan", target.Host);
        Assert.True(target.Enabled);
    }

    [Fact]
    public void AnEmptySettingsDocument_CarriesNothing() =>
        Assert.False(MqttSettingsMigration.Apply(Legacy("{}"), new MqttSettings()));

    [Fact]
    public void OneMqttKeyIsEnough_ToSayThereIsSomethingToMove() =>
        Assert.True(MqttSettingsMigration.Carries(Legacy("""{"MqttBrokerHost":"broker.lan"}""")));

    // ── Carrying values across ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWholeBrokerBlock_IsCarriedAcrossExactly()
    {
        var moved = Migrated("""
        {
          "HomeAssistantEnabled": true,
          "MqttBrokerHost": "broker.lan",
          "MqttBrokerPort": 8883,
          "MqttPortDefaultRetiredForAutomatic": true,
          "MqttUsername": "chargekeeper",
          "MqttPassword": "hunter2",
          "MqttEncryptionMode": "On",
          "MqttTransportMode": "WebSocket",
          "MqttDiscoveryPrefix": "ha",
          "MqttDeviceName": "Office ThinkPad",
          "MqttNodeId": "chargekeeper_office_x1"
        }
        """);

        Assert.True(moved.Enabled);
        Assert.Equal("broker.lan", moved.Host);
        Assert.Equal(8883, moved.Port);
        Assert.Equal("chargekeeper", moved.Username);
        Assert.Equal("hunter2", moved.Password);
        Assert.Equal(MqttEncryptionMode.On, moved.EncryptionMode);
        Assert.Equal(MqttTransportMode.WebSocket, moved.TransportMode);
        Assert.Equal("ha", moved.DiscoveryPrefix);
        Assert.Equal("Office ThinkPad", moved.DeviceName);
        Assert.Equal("chargekeeper_office_x1", moved.DeviceId);
    }

    [Fact]
    public void TheNodeId_BecomesTheDeviceIdUnchanged() =>
        // It is the unique_id stem. Anything but an exact carry-over renames all forty-six entities.
        Assert.Equal("chargekeeper_office_x1",
                     Migrated("""{"MqttNodeId":"chargekeeper_office_x1"}""").DeviceId);

    [Fact]
    public void AKeyTheOldFileDoesNotCarry_LeavesTheTargetAlone()
    {
        // A settings.json synced in from another machine legitimately carries half the block.
        var target = new MqttSettings { Username = "already-set", DiscoveryPrefix = "ha" };
        MqttSettingsMigration.Apply(Legacy("""{"MqttBrokerHost":"broker.lan"}"""), target);

        Assert.Equal("broker.lan", target.Host);
        Assert.Equal("already-set", target.Username);
        Assert.Equal("ha", target.DiscoveryPrefix);
    }

    [Fact]
    public void AnExplicitlyEmptyValue_IsStillAValueAndIsCarried()
    {
        var target = new MqttSettings { Username = "already-set" };
        MqttSettingsMigration.Apply(Legacy("""{"MqttUsername":""}"""), target);
        Assert.Equal("", target.Username);
    }

    [Fact]
    public void AKeyOfTheWrongShape_IsIgnoredRatherThanTakenAsADefault()
    {
        // A hand-edited file, or one written by something else. Reading a null host as "" would clear
        // a broker the user had set.
        var target = new MqttSettings { Host = "broker.lan", Enabled = true };
        MqttSettingsMigration.Apply(
            Legacy("""{"MqttBrokerHost":null,"HomeAssistantEnabled":"yes"}"""), target);

        Assert.Equal("broker.lan", target.Host);
        Assert.True(target.Enabled);
    }

    // ── The publish groups ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachPublishFlag_BecomesItsOwnGroupKey()
    {
        var moved = Migrated("""
        {
          "MqttPublishBatteryStatus": true,
          "MqttPublishSmartCharge": false,
          "MqttPublishKeepAwake": true,
          "MqttPublishLidClose": false,
          "MqttPublishNotifications": true,
          "MqttPublishNetwork": false,
          "MqttPublishAppDiagnostics": true
        }
        """);

        Assert.True(moved.Groups[MqttPublishGroups.BatteryStatus]);
        Assert.False(moved.Groups[MqttPublishGroups.SmartCharge]);
        Assert.True(moved.Groups[MqttPublishGroups.KeepAwake]);
        Assert.False(moved.Groups[MqttPublishGroups.LidClose]);
        Assert.True(moved.Groups[MqttPublishGroups.Notifications]);
        Assert.False(moved.Groups[MqttPublishGroups.Network]);
        Assert.True(moved.Groups[MqttPublishGroups.AppDiagnostics]);
    }

    [Fact]
    public void APublishFlagTheOldFileNeverHad_LeavesTheGroupToItsOwnDefault()
    {
        // A group added after that file was last written starts where its author intended, not off.
        var moved = Migrated("""{"MqttPublishNetwork":false}""");

        Assert.False(moved.Groups[MqttPublishGroups.Network]);
        Assert.DoesNotContain(MqttPublishGroups.AppDiagnostics, moved.Groups.Keys);
    }

    // ── The inherited default port ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThePortInheritedFromWhen1883WasTheDefault_BecomesAutomatic() =>
        // Automatic sweeps TCP 1883 as its first candidate, so a broker genuinely there still answers
        // on the first attempt.
        Assert.Null(Migrated("""{"MqttBrokerPort":1883}""").Port);

    [Fact]
    public void A1883ChosenAfterTheRetirementRan_IsAChoiceAndIsKept() =>
        Assert.Equal(1883,
            Migrated("""{"MqttBrokerPort":1883,"MqttPortDefaultRetiredForAutomatic":true}""").Port);

    [Fact]
    public void AnyOtherPort_IsCarriedWhetherOrNotTheRetirementHadRun()
    {
        Assert.Equal(8883, Migrated("""{"MqttBrokerPort":8883}""").Port);
        Assert.Equal(8883,
            Migrated("""{"MqttBrokerPort":8883,"MqttPortDefaultRetiredForAutomatic":true}""").Port);
    }

    [Fact]
    public void AnExplicitlyAutomaticPort_IsCarriedAsAutomatic()
    {
        var target = new MqttSettings { Port = 1883 };
        MqttSettingsMigration.Apply(Legacy("""{"MqttBrokerPort":null,"MqttBrokerHost":"broker.lan"}"""), target);
        Assert.Null(target.Port);
    }

    [Fact]
    public void APortKeyThatIsNotThere_CarriesNothing() =>
        Assert.False(MqttSettingsMigration.Port(Legacy("""{"MqttBrokerHost":"x"}""")).Present);

    // ── The two-state encryption switch ─────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeValuedSetting_WinsWhereverTheOldFileHasOne() =>
        Assert.Equal(MqttEncryptionMode.Off,
            Migrated("""{"MqttEncryptionMode":"Off","MqttUseTls":true}""").EncryptionMode);

    [Theory]
    [InlineData("true", MqttEncryptionMode.On)]
    [InlineData("false", MqttEncryptionMode.Off)]
    public void AnExplicitOldSwitch_BecomesAnExplicitSettingRatherThanOneThatNegotiates(
        string value, MqttEncryptionMode expected) =>
        Assert.Equal(expected, Migrated($$"""{"MqttUseTls":{{value}}}""").EncryptionMode);

    [Fact]
    public void NeitherEncryptionKey_LeavesTheModulesOwnAutomaticStanding()
    {
        // An absent key is not a false. Reading it as one would put an install that never chose
        // anything onto a pinned setting.
        Assert.Null(MqttSettingsMigration.Encryption(Legacy("""{"MqttBrokerHost":"broker.lan"}""")));
        Assert.Equal(MqttEncryptionMode.Auto, Migrated("""{"MqttBrokerHost":"broker.lan"}""").EncryptionMode);
    }

    [Fact]
    public void AnOldSwitchExplicitlyNull_IsStillNothingChosen() =>
        Assert.Null(MqttSettingsMigration.Encryption(Legacy("""{"MqttUseTls":null}""")));

    // ── Running it against real files ───────────────────────────────────────────────────────────

    [Fact]
    public void WithTheModulesFileAlreadyThere_TheMoveDoesNotRunASecondTime()
    {
        using var dir = new TempDirectory();
        string legacy = dir.Write("settings.json", """{"MqttBrokerHost":"stale.lan"}""");
        dir.Write(MqttSettingsFile.DefaultFileName, """{"Host":"current.lan"}""");

        var store = new FakeMqttSettingsStore();
        store.Update(s => s.Host = "current.lan");

        Assert.False(MqttSettingsMigration.Run(legacy, dir.Path, store));
        Assert.Equal("current.lan", store.Read().Host);
    }

    [Fact]
    public void WithNoModuleFileYet_TheMoveRunsAndWritesThroughTheStore()
    {
        using var dir = new TempDirectory();
        string legacy = dir.Write("settings.json",
            """{"MqttBrokerHost":"broker.lan","HomeAssistantEnabled":true}""");

        var store = new FakeMqttSettingsStore();
        Assert.True(MqttSettingsMigration.Run(legacy, dir.Path, store));
        Assert.Equal("broker.lan", store.Read().Host);
        Assert.True(store.Read().Enabled);
    }

    [Fact]
    public void WithNoSettingsFileAtAll_ThereIsNothingToMove()
    {
        using var dir = new TempDirectory();
        Assert.False(MqttSettingsMigration.Run(
            System.IO.Path.Combine(dir.Path, "settings.json"), dir.Path, new FakeMqttSettingsStore()));
    }

    [Fact]
    public void AnUnreadableSettingsFile_LeavesTheStoreAloneRatherThanTakingStartupWithIt()
    {
        using var dir = new TempDirectory();
        string legacy = dir.Write("settings.json", "{ this is not json");

        var store = new FakeMqttSettingsStore();
        Assert.False(MqttSettingsMigration.Run(legacy, dir.Path, store));
        Assert.Equal(0, store.Writes);
    }

    private sealed class TempDirectory : System.IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ck-mqtt-migration-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            string full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
