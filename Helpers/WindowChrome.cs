using Microsoft.UI.Windowing;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Shared frameless-popup window chrome: no taskbar/Alt-Tab entry (these popups auto-dismiss on
/// focus loss, so a switcher entry would be pointless), thin border, no title bar, no caption
/// buttons. The three popup-style windows (dashboard, pop-out graph, name-location prompt) each
/// carried a verbatim copy of this block, differing only in the two flags exposed here.
/// </summary>
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
