using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>A hand-built panel setup hears of state changes only from here; else Broker reads Automatic.</summary>
public class MqttPanelStateRelayTests
{
    [Fact]
    public void TheSetupHandedToThePanelIsNotifiedOfEveryStateChange_AndReleasedOnClose()
    {
        string file = Path.Combine("UI", "SettingsWindow.xaml.cs");
        string init = RepoFiles.MethodBody(file, "void InitialiseMqttPanel(");
        string closed = RepoFiles.MethodBody(file, "void OnClosed(");

        string handed = Regex.Match(init, @"MqttPanel\.Initialise\((\w+)\)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(handed), "The panel is no longer given a named setup.");
        string relay = Regex.Match(init, @"(\w+)\s*=\s*_\s*=>\s*" + handed + @"\.NotifyConnectionStateChanged\(\)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(relay), "No relay notifies the setup the panel was given.");
        Assert.Contains($"mqtt.StateChanged += {relay};", init);
        Assert.Contains($"_mqtt.StateChanged -= {relay};", closed);
    }
}
