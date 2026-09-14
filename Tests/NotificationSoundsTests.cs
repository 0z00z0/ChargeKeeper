using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The notification switches and the one sound they share. A switch paired with the wrong
/// notification, a recording missing from the output, or a sound playing while Windows holds
/// notifications back all reach a person with nothing failing loudly.
/// </summary>
public class NotificationSoundsTests
{
    [Theory]
    [InlineData(nameof(NotificationKind.ChargeComplete))]
    [InlineData(nameof(NotificationKind.ChargingStarted))]
    [InlineData(nameof(NotificationKind.SleptWhileHot))]
    [InlineData(nameof(NotificationKind.SettingsNotSaved))]
    [InlineData(nameof(NotificationKind.ScriptFailed))]
    public void ANotificationThatHadNoSwitchIsOnByDefault(string kind) =>
        Assert.True(NotificationSwitches.IsOn(new AppSettings(), Enum.Parse<NotificationKind>(kind)));

    [Theory]
    [InlineData(nameof(NotificationKind.LowBattery))]
    [InlineData(nameof(NotificationKind.HighBattery))]
    [InlineData(nameof(NotificationKind.DrainAnomaly))]
    [InlineData(nameof(NotificationKind.ChargeComplete))]
    [InlineData(nameof(NotificationKind.ChargingStarted))]
    [InlineData(nameof(NotificationKind.SleptWhileHot))]
    [InlineData(nameof(NotificationKind.SettingsNotSaved))]
    [InlineData(nameof(NotificationKind.ScriptFailed))]
    // Named rather than typed: the enum is internal and a public test method cannot take it.
    public void SwitchingOneNotificationOffSwitchesOffThatOneAlone(string name)
    {
        var kind = Enum.Parse<NotificationKind>(name);
        var settings = new AppSettings();
        foreach (var each in Enum.GetValues<NotificationKind>()) NotificationSwitches.Set(settings, each, true);

        NotificationSwitches.Set(settings, kind, false);

        foreach (var each in Enum.GetValues<NotificationKind>())
            Assert.True(NotificationSwitches.IsOn(settings, each) == (each != kind),
                        $"switching {kind} off left {each} {(NotificationSwitches.IsOn(settings, each) ? "on" : "off")}.");
    }

    /// <summary>The switch is read before anything is shown or played, and a switch that is off
    /// returns before either.</summary>
    [Fact]
    public void ASwitchedOffNotificationIsNeitherShownNorHeard()
    {
        string body = SourceMethods.Body(File.ReadAllText(RepoFiles.Find("Services/ToastService.cs")), "TryShow");

        int check = body.IndexOf("NotificationSwitches.IsOn", StringComparison.Ordinal);
        int show  = body.IndexOf("AppNotificationManager.Default.Show", StringComparison.Ordinal);
        int play  = body.IndexOf("NotificationSoundPlayer.PlayFor", StringComparison.Ordinal);

        Assert.True(check >= 0, "the notification path no longer reads the switch.");
        Assert.True(show > check && play > show, "the switch is read after the notification is shown or played.");
        Assert.Contains("return;", body[check..show], StringComparison.Ordinal);
    }

    /// <summary>The names are what the settings document stores.</summary>
    [Fact]
    public void TheSoundIsStoredByNameAndDefaultsToSilent()
    {
        Assert.Equal(["Silent", "WindowsSound", "IonGlide", "PixelStep", "CrispTicks"],
                     Enum.GetNames<NotificationSound>());
        Assert.Equal(NotificationSound.Silent, new AppSettings().NotificationSound);
    }

    [Theory]
    [InlineData(nameof(NotificationKind.LowBattery),       "pixel-step-low.wav")]
    [InlineData(nameof(NotificationKind.HighBattery),      "pixel-step-high.wav")]
    [InlineData(nameof(NotificationKind.DrainAnomaly),     "pixel-step-neutral.wav")]
    [InlineData(nameof(NotificationKind.ChargeComplete),   "pixel-step-neutral.wav")]
    [InlineData(nameof(NotificationKind.ScriptFailed),     "pixel-step-neutral.wav")]
    public void LowBatteryFallsHighBatteryRisesTheRestStayLevel(string kind, string file) =>
        Assert.Equal(file, NotificationSounds.FileName(NotificationSound.PixelStep,
                                                       NotificationSounds.VariantFor(Enum.Parse<NotificationKind>(kind))));

    [Fact]
    public void OnlyTheWindowsSoundKeepsWindowsAudioAndNeitherItNorSilentPlaysARecording()
    {
        Assert.False(NotificationSounds.SilencesWindowsAudio(NotificationSound.WindowsSound));
        Assert.True(NotificationSounds.SilencesWindowsAudio(NotificationSound.Silent));
        Assert.True(NotificationSounds.SilencesWindowsAudio(NotificationSound.IonGlide));
        Assert.False(NotificationSounds.HasOwnRecording(NotificationSound.WindowsSound));
        Assert.False(NotificationSounds.HasOwnRecording(NotificationSound.Silent));
    }

    [Fact]
    public void EveryRecordingIsInTheBuildOutput()
    {
        var missing = new List<string>();
        int named = 0;
        foreach (var (sound, _) in NotificationSounds.Choices)
            foreach (var variant in Enum.GetValues<NotificationSoundVariant>())
                if (NotificationSounds.FileName(sound, variant) is { } file)
                {
                    named++;
                    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, NotificationSounds.FolderRelativeToApp, file)))
                        missing.Add(file);
                }

        Assert.Equal(9, named);
        Assert.True(missing.Count == 0,
            $"missing from the build output ({AppContext.BaseDirectory}): {string.Join(", ", missing)}.");
    }

    [Theory]
    [InlineData(5,    false, true)]
    [InlineData(null, false, true)]
    [InlineData(1,    false, false)]
    [InlineData(2,    false, false)]
    [InlineData(3,    false, false)]
    [InlineData(4,    false, false)]
    [InlineData(6,    false, false)]
    [InlineData(7,    false, false)]
    [InlineData(5,    true,  false)]
    public void TheApplicationsOwnSoundPlaysOnlyWhileWindowsAcceptsNotifications(int? state, bool focus, bool plays) =>
        Assert.Equal(plays, new NotificationQuietReading(state, focus).AllowsSound);
}
