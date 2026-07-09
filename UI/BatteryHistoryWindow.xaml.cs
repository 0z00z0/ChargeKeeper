using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.UI;

/// <summary>
/// Bigger, resizable "pop-out" view of the battery-history graph — opened via the "⤢ Expand"
/// button on <see cref="DashboardWindow"/>'s embedded <see cref="BatteryHistoryGraphControl"/>.
/// Hosts the identical control (same drawing code, same shared <c>GraphTimeScale</c> setting and
/// <c>BatteryHistoryService</c> in-memory window as the small dashboard — this is a bigger view of
/// the same graph, not an independently-scoped second one), just given room to grow: a normal
/// title bar, resizable/maximizable, not always-on-top, and NOT auto-hidden on focus loss like the
/// tray popup — it stays open until closed via the title bar, like any other window.
/// </summary>
public sealed partial class BatteryHistoryWindow : Window
{
    // Default/minimum client size in DIPs. The minimum keeps the 28px/36px axis-label columns plus
    // a usable plot from being squeezed to nothing if the user drags the window very small.
    private const int MinWidth  = 640;
    private const int MinHeight = 420;

    private readonly DispatcherTimer _refreshTimer;

    public BatteryHistoryWindow()
    {
        InitializeComponent();
        Title = "Battery History — ChargeKeeper";
        ConfigureWindowChrome();
        PlaceWindow();

        // Render immediately so the window doesn't show a blank canvas before the first tick.
        HistoryGraph.Render();

        _refreshTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => HistoryGraph.Render();
        _refreshTimer.Start();

        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void ConfigureWindowChrome()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
        presenter.IsResizable   = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        presenter.IsAlwaysOnTop = false;
        AppWindow.SetPresenter(presenter);
    }

    /// <summary>
    /// Sizes the window to ~70% width × 65% height of the monitor under the cursor (clamped to a
    /// sane minimum) and centers it there. Uses the same
    /// <see cref="NativeMethods.GetCursorMonitorMetrics"/> helper as DashboardWindow/AboutWindow,
    /// but centers rather than tray-anchors — this is a "see details" window the user keeps open,
    /// not a transient popup.
    /// </summary>
    private void PlaceWindow()
    {
        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int workW = work.Right  - work.Left;
        int workH = work.Bottom - work.Top;

        int cw = Math.Max((int)(MinWidth  * scale), (int)(workW * 0.70));
        int ch = Math.Max((int)(MinHeight * scale), (int)(workH * 0.65));

        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.Left + (workW - outer.Width)  / 2,
            work.Top  + (workH - outer.Height) / 2));
    }
}
