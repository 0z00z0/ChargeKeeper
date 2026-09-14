using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>The one sound every notification uses, chosen on the Notifications page.</summary>
/// <remarks>Stored by name in the settings document, so a member is never renamed.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum NotificationSound
{
    /// <summary>The notification appears with its audio silenced.</summary>
    Silent,

    /// <summary>The notification plays whatever Windows plays for it.</summary>
    WindowsSound,

    IonGlide,
    PixelStep,
    CrispTicks,
}

/// <summary>Which recording of a sound a notification plays: falling for a low battery, rising for
/// a high one, level for everything else.</summary>
internal enum NotificationSoundVariant { Low, Neutral, High }

/// <summary>The sound choices and which file each notification plays. Pure — no audio, no I/O.</summary>
internal static class NotificationSounds
{
    /// <summary>What an installation with no choice stored plays.</summary>
    public const NotificationSound Default = NotificationSound.Silent;

    /// <summary>The choices in the order the Sound box lists them, with their labels.</summary>
    public static readonly IReadOnlyList<(NotificationSound Sound, string Label)> Choices =
    [
        (NotificationSound.Silent,       "Silent"),
        (NotificationSound.WindowsSound, "Windows sound"),
        (NotificationSound.IonGlide,     "Ion Glide"),
        (NotificationSound.PixelStep,    "Pixel Step"),
        (NotificationSound.CrispTicks,   "Crisp Ticks"),
    ];

    /// <summary>Where the recordings sit relative to the executable. The project file copies them
    /// there for both the build output and the publish the installer packages.</summary>
    public const string FolderRelativeToApp = @"Assets\Sounds";

    public static NotificationSoundVariant VariantFor(NotificationKind kind) => kind switch
    {
        NotificationKind.LowBattery  => NotificationSoundVariant.Low,
        NotificationKind.HighBattery => NotificationSoundVariant.High,
        _                            => NotificationSoundVariant.Neutral,
    };

    /// <summary>The recording's file name, or null for a choice that plays no file of the
    /// application's own.</summary>
    public static string? FileName(NotificationSound sound, NotificationSoundVariant variant)
    {
        string? stem = sound switch
        {
            NotificationSound.IonGlide   => "ion-glide",
            NotificationSound.PixelStep  => "pixel-step",
            NotificationSound.CrispTicks => "crisp-ticks",
            _                            => null,
        };
        return stem is null ? null : $"{stem}-{variant.ToString().ToLowerInvariant()}.wav";
    }

    public static bool HasOwnRecording(NotificationSound sound) =>
        FileName(sound, NotificationSoundVariant.Neutral) is not null;

    /// <summary>Every choice but the Windows sound silences the notification's own audio: Silent
    /// wants nothing, and a recording would otherwise play over the Windows sound.</summary>
    public static bool SilencesWindowsAudio(NotificationSound sound) => sound != NotificationSound.WindowsSound;
}
