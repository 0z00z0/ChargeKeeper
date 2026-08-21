using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Paints a standard window title bar in the studio dark palette so it stops clashing with the dark
/// Mica backdrop. Only touches title-bar colours, never the presenter or border, so it is safe to
/// call on any window and a no-op on a frameless popup.
/// </summary>
internal static class TitleBarTheme
{
    // Kept local rather than reusing AppColors, so this helper stays free of WinUI-media coupling.
    private static readonly Color Bg     = Color.FromArgb(0xFF, 0x0a, 0x0f, 0x17); // window/title background
    private static readonly Color Hover  = Color.FromArgb(0xFF, 0x1a, 0x28, 0x40); // caption-button hover
    private static readonly Color Text   = Color.FromArgb(0xFF, 0xdd, 0xe6, 0xf4); // title + glyph foreground

    /// <summary>Applies the studio-dark title-bar colours to <paramref name="appWindow"/>. Never
    /// throws — a title-bar customisation failure must not stop a window from showing.</summary>
    internal static void ApplyDark(AppWindow? appWindow)
    {
        try
        {
            if (appWindow is null) return;

            // AppIcon.ico, not the installer's SetupIcon.ico: the latter's ink tones are drawn for
            // Inno's light chrome and would disappear against this background.
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

            // Mica does not paint the non-client caption area, so leaving these Transparent renders
            // a light strip behind the min/max/close buttons.
            tb.ButtonBackgroundColor         = Bg;
            tb.ButtonInactiveBackgroundColor = Bg;
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
