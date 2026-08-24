using System.Reflection;

namespace ChargeKeeper.Helpers;

/// <summary>Process-wide app identity, resolved once and shared so the tray tooltip, the About
/// window and the update check cannot drift on version format or fallback text.</summary>
internal static class AppInfo
{
    /// <summary>Product name — the single literal for dialog titles, tooltips, etc.</summary>
    public const string Name = "ChargeKeeper";

    /// <summary>Three-part display version, e.g. "1.2.19".</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
}
