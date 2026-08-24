using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What the MQTT page persists and what it refuses. The port box is the only free-text numeric
/// setting on the page, so it is the only one that can carry a value no dropdown could produce.
/// </summary>
public class MqttEndpointSettingsTests
{
    // The typed port goes through the same range check as every other bounded setting, so a remote
    // write and a hand-edited settings.json cannot reach anything the box refuses.
    [Fact]
    public void ATypedPort_IsAcceptedOnlyInsideTheRangeASocketCanAddress()
    {
        Assert.Null(SettingRanges.ValidatePort("1883", out int typed));
        Assert.Equal(1883, typed);

        Assert.Null(SettingRanges.ValidatePort(" 65535 ", out int max));
        Assert.Equal(65535, max);
        Assert.Null(SettingRanges.ValidatePort("1", out _));

        Assert.NotNull(SettingRanges.ValidatePort("0", out _));
        Assert.NotNull(SettingRanges.ValidatePort("65536", out _));
    }

    // Anything that is not a plain whole number is refused outright rather than parsed loosely into
    // some nearby port.
    [Fact]
    public void ATypedPort_RefusesAnythingThatIsNotAPlainWholeNumber()
    {
        foreach (string text in new[] { "", "   ", "8o83", "1883.0", "-1", "+443", "1,883", "1883a" })
            Assert.NotNull(SettingRanges.ValidatePort(text, out _));
    }

    [Fact]
    public void TheRangeMatchesWhatTheTransportLayerClampsTo()
    {
        Assert.Equal(1, SettingRanges.BrokerPortMin);
        Assert.Equal(65535, SettingRanges.BrokerPortMax);
        Assert.Equal(("host", SettingRanges.BrokerPortMax),
            MqttTransportEndpoint.Reachability("host", 99999, MqttTransport.Tcp, useTls: false));
        Assert.Equal(("host", SettingRanges.BrokerPortMin),
            MqttTransportEndpoint.Reachability("host", 0, MqttTransport.Tcp, useTls: false));
    }

    // The port is found rather than assumed, so a fresh install starts with none: assuming 1883 is
    // right only for the plain internal case and wrong everywhere else.
    [Fact]
    public void AFreshInstall_HasNoPortAndNoRememberedEndpoint()
    {
        var fresh = new AppSettings();
        Assert.Null(fresh.MqttBrokerPort);
        Assert.Null(fresh.MqttLastGoodEndpoint);
        Assert.Equal(MqttTransportSetting.Auto, fresh.MqttTransportMode);
    }

    // The cache is only worth anything if it survives a restart, and settings.json is the only place
    // it lives. A record that does not round-trip would silently cost a full sweep on every start.
    [Fact]
    public void TheRememberedEndpoint_SurvivesSettingsJson()
    {
        var saved = new AppSettings
        {
            MqttBrokerHost       = "mq.laget.no",
            MqttBrokerPort       = null,
            MqttLastGoodEndpoint = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket),
        };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved));

        Assert.Equal(saved.MqttLastGoodEndpoint, reloaded!.MqttLastGoodEndpoint);
        Assert.Null(reloaded.MqttBrokerPort);
    }

    // Nothing that can hold a credential may reach the file's cache entry.
    [Fact]
    public void TheRememberedEndpoint_CarriesNoPassword()
    {
        var settings = new AppSettings
        {
            MqttPassword         = "hunter2",
            MqttLastGoodEndpoint = new MqttEndpointMemory("mq.laget.no", "ck", 443, MqttTransport.WebSocket),
        };

        string entry = JsonSerializer.Serialize(settings.MqttLastGoodEndpoint);
        Assert.DoesNotContain("hunter2", entry);
        Assert.DoesNotContain("assword", entry);
    }

    // Diagnostics describe ChargeKeeper rather than the battery, so they are opted into. Every other
    // group is the surface the feature exists for and stays on.
    [Fact]
    public void AFreshInstall_PublishesEveryGroupExceptDiagnostics()
    {
        var fresh = new AppSettings();

        Assert.True(fresh.MqttPublishBatteryStatus);
        Assert.True(fresh.MqttPublishSmartCharge);
        Assert.True(fresh.MqttPublishKeepAwake);
        Assert.True(fresh.MqttPublishLidClose);
        Assert.True(fresh.MqttPublishNotifications);
        Assert.True(fresh.MqttPublishNetwork);
        Assert.False(fresh.MqttPublishAppDiagnostics);
    }

    // The whole safety case for migrating a stored 1883 rests on Automatic reaching it first, so the
    // sweep's leading TCP candidate is asserted rather than assumed.
    [Fact]
    public void TheRetiredDefault_IsTheFirstPortAutomaticTries()
    {
        Assert.Equal(SettingsService.RetiredDefaultMqttPort, MqttTransportPlan.Ports(MqttTransport.Tcp)[0]);

        var sweep = MqttTransportPlan.Sweep(
            new MqttEndpointRequest("mq.laget.no", "ck", null, MqttTransportSetting.Auto), null);

        // The port is what this is about; which scheme leads on it is EncryptionOrder's business.
        Assert.Equal(SettingsService.RetiredDefaultMqttPort, sweep[0].Port);
        Assert.Equal(MqttTransport.Tcp, sweep[0].Transport);
    }

    // The upgrade case: 1883 in the file is the old default rather than a decision, and leaving it
    // there pins the machine to the internal port wherever it goes.
    [Fact]
    public void TheInheritedDefaultPort_BecomesAutomaticAndIsMarkedDone()
    {
        var s = new AppSettings { MqttBrokerHost = "mq.laget.no", MqttBrokerPort = 1883 };

        Assert.True(SettingsService.RetireDefaultMqttPort(s));
        Assert.Null(s.MqttBrokerPort);
        Assert.True(s.MqttPortDefaultRetiredForAutomatic);
        Assert.Equal("mq.laget.no", s.MqttBrokerHost);
    }

    // The marker is the whole guard: without it, a 1883 picked deliberately would be cleared again on
    // the next start, for ever.
    [Fact]
    public void APortPinnedAfterTheMigration_IsLeftAlone()
    {
        var s = new AppSettings { MqttBrokerPort = 1883 };
        SettingsService.RetireDefaultMqttPort(s);
        s.MqttBrokerPort = 1883;

        Assert.Null(SettingsService.RetireDefaultMqttPort(s));
        Assert.Equal(1883, s.MqttBrokerPort);
    }

    // Any other stored port was typed or picked, so it is a decision and survives untouched.
    [Fact]
    public void APortThatWasNeverTheDefault_IsNotMigrated()
    {
        var s = new AppSettings { MqttBrokerPort = 8883 };

        Assert.False(SettingsService.RetireDefaultMqttPort(s));
        Assert.Equal(8883, s.MqttBrokerPort);
        Assert.True(s.MqttPortDefaultRetiredForAutomatic);
    }

    // A fresh install is already on Automatic; the migration only records that it need not run.
    [Fact]
    public void AFreshInstall_HasNothingToMigrate()
    {
        var s = new AppSettings();

        Assert.False(SettingsService.RetireDefaultMqttPort(s));
        Assert.Null(s.MqttBrokerPort);
        Assert.True(s.MqttPortDefaultRetiredForAutomatic);
    }

    // The marker has to reach settings.json, or the migration runs on every start and reverts a
    // deliberate 1883 each time.
    [Fact]
    public void TheMigrationMarker_SurvivesSettingsJson()
    {
        var saved = new AppSettings { MqttPortDefaultRetiredForAutomatic = true };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved));

        Assert.True(reloaded!.MqttPortDefaultRetiredForAutomatic);
        Assert.False(new AppSettings().MqttPortDefaultRetiredForAutomatic);
    }

    // The encryption switch was a bool and is now three-state. The three paths are not
    // interchangeable: an explicit choice has to survive as an explicit choice, and only a file that
    // never carried the key at all may land on Automatic. This is the shape of defect #120 — an
    // absent key selecting the branch that changes behaviour — so all three are pinned.
    [Fact]
    public void AnExplicitEncryptionSwitch_StaysExplicitAcrossTheUpgrade()
    {
        var wasOn = new AppSettings { MqttUseTls = true };
        Assert.Equal(MqttEncryptionSetting.On, SettingsService.MigrateEncryptionMode(wasOn));
        Assert.Equal(MqttEncryptionSetting.On, wasOn.MqttEncryption);

        var wasOff = new AppSettings { MqttUseTls = false };
        Assert.Equal(MqttEncryptionSetting.Off, SettingsService.MigrateEncryptionMode(wasOff));
        Assert.Equal(MqttEncryptionSetting.Off, wasOff.MqttEncryption);
    }

    // Absent, not false. A file with no encryption key at all — older than the setting, or brand new
    // — is the only one that starts negotiating.
    [Fact]
    public void AnAbsentEncryptionKey_IsTheOnlyOneThatBecomesAutomatic()
    {
        var absent = new AppSettings();
        Assert.Null(absent.MqttUseTls);

        Assert.Equal(MqttEncryptionSetting.Auto, SettingsService.MigrateEncryptionMode(absent));
        Assert.Equal(MqttEncryptionSetting.Auto, absent.MqttEncryption);
    }

    // Once the setting exists it is the answer, and the migration must never revisit it — otherwise
    // a deliberate Automatic would be rewritten from a stale switch on every start.
    [Fact]
    public void AnAlreadyMigratedFile_IsNeverRewritten()
    {
        var s = new AppSettings { MqttUseTls = true };
        SettingsService.MigrateEncryptionMode(s);

        // The old key is cleared on the way through, so the file never holds two answers at once.
        Assert.Null(s.MqttUseTls);

        s.MqttEncryptionMode = MqttEncryptionSetting.Auto;
        s.MqttUseTls = true;
        Assert.Null(SettingsService.MigrateEncryptionMode(s));
        Assert.Equal(MqttEncryptionSetting.Auto, s.MqttEncryption);
    }

    // The choice has to reach settings.json by name, or a reload reads it back as a number that
    // shifts the day someone reorders the enum.
    [Fact]
    public void TheEncryptionChoice_SurvivesSettingsJson()
    {
        var saved = new AppSettings { MqttEncryptionMode = MqttEncryptionSetting.Off };

        string json = JsonSerializer.Serialize(saved);
        Assert.Contains("\"Off\"", json);

        var reloaded = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.Equal(MqttEncryptionSetting.Off, reloaded!.MqttEncryption);
        // A fresh object has made no choice yet; only the migration puts one there.
        Assert.Null(new AppSettings().MqttEncryptionMode);
    }

    // The cache is state, so it has to round-trip complete — an entry that loses the encryption on
    // the way to disk sends Automatic back to a full sweep on every start.
    [Fact]
    public void TheFoundEndpoint_CarriesItsEncryptionThroughSettingsJson()
    {
        var saved = new AppSettings
        {
            MqttLastGoodEndpoint = new MqttEndpointMemory("mq.laget.no", "ck", 8883, MqttTransport.Tcp, true),
        };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved));

        Assert.Equal(saved.MqttLastGoodEndpoint, reloaded!.MqttLastGoodEndpoint);
        Assert.True(reloaded.MqttLastGoodEndpoint!.Encrypted);

        // An entry written before the field existed reads back as "not recorded", never as plain.
        var legacy = JsonSerializer.Deserialize<MqttEndpointMemory>(
            "{\"Host\":\"mq.laget.no\",\"Username\":\"ck\",\"Port\":443,\"Transport\":\"WebSocket\"}");
        Assert.Null(legacy!.Encrypted);
    }
}
