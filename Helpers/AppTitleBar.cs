using Microsoft.UI.Xaml;
using ZeroZero.Controls.WinUI;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The studio-dark title bar on a standard window, so it stops clashing with the dark Mica
/// backdrop, with the application's own icon. The painting is the shared title-bar theming; the
/// colours and the icon are this application's.
/// </summary>
internal static class AppTitleBar
{
    // Mica does not paint the non-client caption area, so the button colours are given too; left
    // unset they render a light strip behind the caption buttons.
    private const uint Background = 0xFF0A0F17;
    private const uint Hover      = 0xFF1A2840;
    private const uint Text       = 0xFFDDE6F4;

    internal static TitleBarPalette Palette { get; } = new(
        Background:               Background,
        InactiveBackground:       Background,
        Foreground:               Text,
        InactiveForeground:       Text,
        ButtonBackground:         Background,
        ButtonInactiveBackground: Background,
        ButtonForeground:         Text,
        ButtonInactiveForeground: Text,
        ButtonHoverBackground:    Hover,
        ButtonHoverForeground:    Text,
        ButtonPressedBackground:  Hover,
        ButtonPressedForeground:  Text);

    /// <summary>Applies the icon and the dark title bar. Never throws — a title-bar customisation
    /// failure must not stop a window from showing.</summary>
    internal static void Apply(Window window)
    {
        try
        {
            // AppIcon.ico, not the installer's SetupIcon.ico: the latter's ink tones are drawn for
            // Inno's light chrome and would disappear against this background.
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath)) window.AppWindow.SetIcon(icoPath);
        }
        catch (Exception ex) { AppLog.Error("AppTitleBar.SetIcon", ex); }

        try
        {
            TitleBarTheming.Apply(window, ElementTheme.Dark, Palette);
        }
        catch (Exception ex) { AppLog.Error("AppTitleBar.Apply", ex); }
    }
}
