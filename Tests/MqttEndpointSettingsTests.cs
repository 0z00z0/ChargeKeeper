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
}
