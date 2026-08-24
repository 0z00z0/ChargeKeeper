using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ChargeKeeper;

/// <summary>
/// Invisible 1×1 off-screen host window, so WinUI has a window to own. Closing it does not end the
/// process — App sets DispatcherShutdownMode.OnExplicitShutdown, so only Application.Current.Exit() does.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AppWindow.IsShownInSwitchers = false;

        // Remove chrome so nothing is visible even if the window flickers on-screen.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
    }
}
