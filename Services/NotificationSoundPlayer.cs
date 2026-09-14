using System.Runtime.InteropServices;

namespace ChargeKeeper.Services;

/// <summary>Plays the application's own notification recordings through the Windows multimedia
/// player, which needs no package and returns at once.</summary>
internal static class NotificationSoundPlayer
{
    private const uint SND_ASYNC     = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME  = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);

    /// <summary>Plays the recording for a notification that has just been shown, unless Windows is
    /// holding notifications back. Nothing plays for Silent or the Windows sound.</summary>
    public static void PlayFor(NotificationKind kind, NotificationSound sound)
    {
        if (NotificationSounds.FileName(sound, NotificationSounds.VariantFor(kind)) is not { } file)
            return;

        var quiet = NotificationQuietState.Read();
        if (!quiet.AllowsSound)
        {
            AppLog.Info(NotificationMessages.SoundHeldBack(kind, quiet));
            return;
        }

        Play(file);
    }

    /// <summary>The Sound box's play button: the level recording, whatever Windows is doing,
    /// because a person asked to hear it.</summary>
    public static void Preview(NotificationSound sound)
    {
        if (NotificationSounds.FileName(sound, NotificationSoundVariant.Neutral) is { } file)
            Play(file);
    }

    private static void Play(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, NotificationSounds.FolderRelativeToApp, fileName);
        try
        {
            // SND_NODEFAULT: a missing or unreadable file stays silent rather than playing the
            // Windows default in its place.
            if (!PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT))
                AppLog.Info(NotificationMessages.SoundNotPlayed(fileName, File.Exists(path)));
        }
        catch (Exception ex)
        {
            AppLog.Error("NotificationSoundPlayer.Play", ex);
        }
    }
}
