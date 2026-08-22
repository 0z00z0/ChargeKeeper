using Microsoft.UI.Windowing;

namespace ChargeKeeper.Helpers;

/// <summary>Shared frameless-popup chrome: thin border, no title bar, no caption buttons, and no
/// taskbar or Alt-Tab entry — these popups auto-dismiss on focus loss, so a switcher entry would be
/// pointless.</summary>
internal static class WindowChrome
{
    internal static void ApplyPopup(Microsoft.UI.Xaml.Window window, bool resizable, bool alwaysOnTop)
    {
        window.AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = resizable;
        presenter.IsMaximizable = false;   // meaningless without a title bar's caption buttons
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = alwaysOnTop;
        window.AppWindow.SetPresenter(presenter);
    }
}
