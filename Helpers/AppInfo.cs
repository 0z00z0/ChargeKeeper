using ZeroZero.Primitives;

namespace ChargeKeeper.Helpers;

/// <summary>Process-wide app identity, resolved once and shared so the tray tooltip, the About
/// window and the update check cannot drift on version format or fallback text.</summary>
internal static class AppInfo
{
    /// <summary>Product name — the single literal for dialog titles, tooltips, etc.</summary>
    public const string Name = "ChargeKeeper";

    /// <summary>Three-part display version, e.g. "1.2.19".</summary>
    public static string Version { get; } = NumberOf(AssemblyVersionText.Read(typeof(AppInfo).Assembly));

    /// <summary>The version without its source-revision suffix. The update check compares this
    /// against a release tag and the release notes are keyed on it, so the commit the build stamps
    /// after the plus sign stays in the file's metadata and out of every comparison.</summary>
    internal static string NumberOf(string reported)
    {
        int plus   = reported.IndexOf('+');
        var number = plus < 0 ? reported : reported[..plus];
        return number.Length == 0 ? "unknown" : number;
    }
}
