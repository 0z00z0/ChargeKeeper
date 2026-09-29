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

    // ── Carrying values across ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheNodeId_BecomesTheDeviceIdUnchanged() =>
        // It is the unique_id stem. Anything but an exact carry-over renames all sixty-five entities.
        Assert.Equal("chargekeeper_office_x1",
                     Migrated("""{"MqttNodeId":"chargekeeper_office_x1"}""").DeviceId);

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
