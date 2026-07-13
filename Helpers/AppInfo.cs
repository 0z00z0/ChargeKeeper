using System.Reflection;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Process-wide app identity. Resolved once (it can't change within a run) and shared so the
/// tray tooltip, the About window, and the update check can't drift on format or fallback text —
/// they previously each rolled their own <c>Assembly...Version?.ToString(3)</c> with different
/// fallbacks ("?" vs "unknown").
/// </summary>
internal static class AppInfo
{
    /// <summary>Product name — the single literal for dialog titles, tooltips, etc.</summary>
    public const string Name = "ChargeKeeper";

    /// <summary>Three-part display version, e.g. "1.2.19".</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
}
