using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Paints a standard window title bar in the studio dark palette so it stops clashing with the
/// dark Mica backdrop the windows use. Only touches the title-bar colours (never the presenter /
/// border), so it is safe to call on any window; on a frameless popup it is simply a no-op that
/// the guard below swallows. Caption-button backgrounds are left transparent so the Mica shows
/// through, matching the rest of the chrome.
/// </summary>
internal static class TitleBarTheme
{
    // Studio dark palette (see 0z0-design). Kept local rather than reusing AppColors so this helper
    // stays self-contained around Windows.UI.Color / AppWindowTitleBar with no WinUI-media coupling.
    private static readonly Color Bg     = Color.FromArgb(0xFF, 0x0a, 0x0f, 0x17); // window/title background
    private static readonly Color Hover  = Color.FromArgb(0xFF, 0x1a, 0x28, 0x40); // caption-button hover
    private static readonly Color Text   = Color.FromArgb(0xFF, 0xdd, 0xe6, 0xf4); // title + glyph foreground

    /// <summary>
    /// Applies the studio-dark title-bar colours to <paramref name="appWindow"/>. Guarded and
    /// gated behind <see cref="AppWindowTitleBar.IsCustomizationSupported"/> so it can never throw
    /// out to the caller (a title-bar customisation failure must not stop a window from showing).
    /// </summary>
    internal static void ApplyDark(AppWindow? appWindow)
    {
        try
        {
            if (appWindow is null) return;

            // Make the taskbar / Alt-Tab / title-bar icon the current steel battery rather than
            // whatever the window inherited or cached. AppIcon.ico ships to the output dir (Content).
            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(icoPath)) appWindow.SetIcon(icoPath);
            }
            catch (Exception ex) { AppLog.Error("TitleBarTheme.SetIcon", ex); }

            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            var tb = appWindow.TitleBar;

            tb.BackgroundColor         = Bg;
            tb.InactiveBackgroundColor = Bg;
            tb.ForegroundColor         = Text;
            tb.InactiveForegroundColor = Text;

            // Transparent caption-button backgrounds let the Mica backdrop show through.
            tb.ButtonBackgroundColor         = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor         = Text;
            tb.ButtonHoverForegroundColor    = Text;
            tb.ButtonHoverBackgroundColor    = Hover;
        }
        catch (Exception ex)
        {
            AppLog.Error("TitleBarTheme.ApplyDark", ex);
        }
    }
}
