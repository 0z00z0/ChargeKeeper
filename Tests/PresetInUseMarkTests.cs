using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>A preset applied by a network profile, the tray or Home Assistant moves the Presets list's in-use mark.</summary>
public class PresetInUseMarkTests
{
    [Fact]
    public void ThePresetsListFollowsEveryChargeControlChange()
    {
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml.cs")));

        Assert.Matches(@"ChargeControlService\.StateChanged\s*\+=\s*OnChargeControlStateChanged;", source);
        Assert.Matches(@"void OnChargeControlStateChanged\(\)\s*=>\s*RunOnUi\(\(\) => RefreshPresetActivationStates\(", source);
    }
}
