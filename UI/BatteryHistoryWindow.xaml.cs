using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.UI;

/// <summary>
/// Bigger, resizable "pop-out" view of the battery-history graph — opened from
/// <see cref="DashboardWindow"/>'s embedded <see cref="BatteryHistoryGraphControl"/>.
/// Hosts the identical control (same drawing code, same shared <c>GraphTimeScale</c> setting and
/// <c>BatteryHistoryService</c> in-memory window as the small dashboard — this is a bigger view of
/// the same graph, not an independently-scoped second one), just given room to grow. Frameless
/// (border, no title bar — same chrome as DashboardWindow/AboutWindow) but still resizable, and
/// dismissed the same way as the tray popup: it closes itself on focus loss. With no title-bar X,
/// clicking away is the ONLY way to dismiss it — App's singleton recreates it cheaply next time.
/// When opened from the visible dashboard it animates open, growing from the dashboard's
/// on-screen rect to its final centred rect (see <see cref="AnimateOpen"/>).
/// </summary>
public sealed partial class BatteryHistoryWindow : Window
{
    // Default/minimum window size in DIPs. The minimum keeps the 28px/36px axis-label columns plus
    // a usable plot from being squeezed to nothing on a tiny work area.
    private const int MinWidth  = 640;
    private const int MinHeight = 420;

    // Open animation: ~10 ms ticks over ~180 ms — long enough to read as the dashboard's graph
    // growing into a window, short enough to never feel like the window lags the click.
    private const int AnimDurationMs = 180;
    private const int AnimTickMs     = 10;

    private readonly DispatcherTimer _refreshTimer;
    private DispatcherTimer? _animTimer;

    // Open-animation geometry, captured in the ctor and consumed once on the FIRST Activated event.
    // The animation clock must start when the window is actually on screen: starting it in the ctor
    // (before Activate) ran the whole 180 ms out during window realization, so the motion finished
    // before the first frame was ever composed and looked like an instant pop.
    private readonly RectInt32 _finalRect;
    private RectInt32 _originRect;
    private bool _pendingAnimation;
    private bool _animStarted;

    // Set the moment closing starts. Guards the Deactivated auto-close against re-entering Close()
    // mid-teardown, and stops a stray animation tick from touching a dead AppWindow after Closed.
    private bool _closing;

    /// <param name="originRect">
    /// The tray dashboard's current on-screen rect (physical px) to animate open from, or null to
    /// place the window at its final rect directly (e.g. dashboard already hidden).
    /// </param>
    public BatteryHistoryWindow(RectInt32? originRect = null)
    {
        InitializeComponent();
        Title = "Battery History — ChargeKeeper";
        ConfigureWindowChrome();

        _finalRect = ComputeFinalRect();
        if (originRect is { } origin)
        {
            // Place the window at the origin NOW so its first painted frame is the small dashboard
            // rect; the grow-to-final animation is kicked off once the window is shown (OnActivated).
            _originRect       = origin;
            _pendingAnimation = true;
            AppWindow.MoveAndResize(origin);
        }
        else
        {
            AppWindow.MoveAndResize(_finalRect);
        }

        // Render immediately so the window doesn't show a blank canvas before the first tick.
        HistoryGraph.Render();

        _refreshTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => HistoryGraph.Render();
        _refreshTimer.Start();

        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            _closing = true;
            _refreshTimer.Stop();
            _animTimer?.Stop();
        };
    }

    /// <summary>
    /// Two jobs, keyed on activation state:
    /// <list type="bullet">
    /// <item><b>Deactivated</b> → popup-style dismissal mirroring <see cref="DashboardWindow"/>:
    /// clicking away closes the window. Close, not Hide — App's singleton nulls its reference on
    /// Closed and recreates it cheaply, and with no title bar there is no other way to dismiss it.</item>
    /// <item><b>Activated</b> (first time) → start the open animation, now that the window is
    /// actually on screen at its origin rect.</item>
    /// </list>
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_closing) return;
            _closing = true;
            Close();
            return;
        }

        if (_pendingAnimation && !_animStarted)
        {
            _animStarted = true;
            AnimateOpen(_originRect, _finalRect);
        }
    }

    private void ConfigureWindowChrome()
    {
        // No taskbar/Alt-Tab entry: with the auto-close-on-blur behaviour, alt-tabbing away closes
        // the window anyway, so a switcher entry would be pointless (same rationale as
        // DashboardWindow/AboutWindow).
        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = true;
        presenter.IsMaximizable = false;  // meaningless without a title bar's caption buttons
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = false;
        AppWindow.SetPresenter(presenter);
    }

    /// <summary>
    /// Final placement: ~70% width × 65% height of the monitor under the cursor (clamped to a sane
    /// minimum) and centred there. Uses the same <see cref="NativeMethods.GetCursorMonitorMetrics"/>
    /// helper as DashboardWindow/AboutWindow, but centres rather than tray-anchors. Returns the
    /// OUTER rect rather than applying a client size — the open animation needs one rect it can
    /// interpolate towards with MoveAndResize, and on a frameless window the non-client area is
    /// only the thin resize border, so outer ≈ client for the 70%/65% target.
    /// </summary>
    private RectInt32 ComputeFinalRect()
    {
        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int workW = work.Right  - work.Left;
        int workH = work.Bottom - work.Top;

        int w = Math.Max((int)(MinWidth  * scale), (int)(workW * 0.70));
        int h = Math.Max((int)(MinHeight * scale), (int)(workH * 0.65));

        return new RectInt32(
            work.Left + (workW - w) / 2,
            work.Top  + (workH - h) / 2,
            w, h);
    }

    /// <summary>
    /// Grows the window from <paramref name="origin"/> (the tray dashboard's on-screen rect) to
    /// <paramref name="finalRect"/> on an ease-out curve, so the pop-out reads as the dashboard's
    /// graph expanding rather than an unrelated window appearing. Started from the first Activated
    /// event (window on screen), NOT the ctor. No per-tick graph redraws — the control's own
    /// SizeChanged debounce absorbs the burst of intermediate sizes; one explicit Render() lands
    /// the final frame.
    /// </summary>
    private void AnimateOpen(RectInt32 origin, RectInt32 finalRect)
    {
        AppWindow.MoveAndResize(origin);   // ensure we start exactly at the origin rect

        long startMs = Environment.TickCount64;
        _animTimer   = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AnimTickMs) };
        _animTimer.Tick += (_, _) =>
        {
            if (_closing) { _animTimer?.Stop(); return; }   // window torn down mid-animation

            // Wall-clock progress, not per-tick increments — DispatcherTimer ticks can be late
            // under load, and elapsed-time-based t keeps the total duration honest regardless.
            double t = Math.Min(1.0, (Environment.TickCount64 - startMs) / (double)AnimDurationMs);
            if (t >= 1.0)
            {
                _animTimer!.Stop();
                AppWindow.MoveAndResize(finalRect);   // snap exactly to target — no rounding drift
                HistoryGraph.Render();
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic: fast start, gentle landing
            AppWindow.MoveAndResize(new RectInt32(
                Lerp(origin.X,      finalRect.X,      eased),
                Lerp(origin.Y,      finalRect.Y,      eased),
                Lerp(origin.Width,  finalRect.Width,  eased),
                Lerp(origin.Height, finalRect.Height, eased)));
        };
        _animTimer.Start();
    }

    /// <summary>Integer lerp for the animation rect's components.</summary>
    private static int Lerp(int from, int to, double t) => from + (int)Math.Round((to - from) * t);
}
