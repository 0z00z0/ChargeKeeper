using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace LenovoTray;

/// <summary>
/// Invisible host window — a 1×1 off-screen placeholder so WinUI has a window to own without
/// anything appearing on-screen or in the taskbar / Alt-Tab switcher. Process lifetime is NOT tied
/// to this window closing: App sets DispatcherShutdownMode.OnExplicitShutdown, so only
/// Application.Current.Exit() (via App.Shutdown, e.g. the tray "Exit" command) ends the process.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Exclude from taskbar and Alt-Tab.
        AppWindow.IsShownInSwitchers = false;

        // Remove chrome so nothing is visible even if the window flickers on-screen.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);

        // Park it far off-screen as a belt-and-suspenders measure.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
    }
}
