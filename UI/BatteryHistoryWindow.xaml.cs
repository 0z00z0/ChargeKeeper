using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Devices.Power;
using Windows.Graphics;
using Windows.System.Power;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

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
/// When opened from the visible dashboard it animates open, growing from the dashboard's on-screen
/// rect to its final centred rect; when dismissed via focus loss it plays the same animation in
/// reverse, shrinking from wherever it currently is back down into that origin rect before the
/// window actually closes (see <see cref="AnimateRect"/>). If there was no origin rect to begin
/// with (dashboard already hidden when opened), there's nothing sensible to retract into, so
/// dismissal is an instant <see cref="Close"/> as before.
/// </summary>
public sealed partial class BatteryHistoryWindow : Window
{
    // Default/minimum window size in DIPs. The minimum keeps the 28px/36px axis-label columns plus
    // a usable plot from being squeezed to nothing on a tiny work area.
    private const int MinWidth  = 640;
    private const int MinHeight = 420;

    // Open/retract animation: ~10 ms ticks over ~340 ms — long enough to actually see the
    // dashboard's graph grow into (or shrink back out of) the window, short enough to never feel
    // like it's lagging the click or delaying the user getting to the pop-out.
    private const int AnimDurationMs = 340;
    private const int AnimTickMs     = 10;

    private readonly DispatcherTimer _refreshTimer;
    private DispatcherTimer? _animTimer;

    // Open-animation geometry, captured in the ctor and consumed once on the FIRST Activated event.
    // The animation clock must start when the window is actually on screen: starting it in the ctor
    // (before Activate) ran the whole duration out during window realization, so the motion finished
    // before the first frame was ever composed and looked like an instant pop.
    // _originRect is null when the window was opened with no origin (dashboard already hidden) —
    // that's also the signal at close time that there's nothing to retract into.
    private readonly RectInt32 _finalRect;
    private readonly RectInt32? _originRect;
    private bool _animStarted;

    // Set the moment the Closed event fires (final teardown). Stops a stray animation tick from
    // touching a dead AppWindow after Closed.
    private bool _closing;

    // Set the moment a focus-loss dismissal begins — either the instant Close() (no origin rect) or
    // the retract-then-close animation. Guards against a second Deactivated event arriving mid-
    // retract (the window is still "active enough" to activate/deactivate again during its own
    // ~340 ms close animation) from starting a second animation or double-calling Close().
    private bool _dismissing;

    // Set on the FIRST non-Deactivated Activated event this window ever receives. A Deactivated
    // arriving before that point is spurious, not a real "user clicked away" — it can only mean
    // the window hasn't actually finished taking focus yet (e.g. Activate() was called but the OS
    // hasn't delivered the corresponding activation yet). Treating it as a real dismissal was
    // observed as the pop-out opening and then immediately closing again on a fast double-click.
    private bool _everActivated;

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
            _originRect = origin;
            AppWindow.MoveAndResize(origin);
        }
        else
        {
            AppWindow.MoveAndResize(_finalRect);
        }

        // Render immediately so the window doesn't show a blank canvas before the first tick.
        HistoryGraph.Render();
        RefreshStats();

        _refreshTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => { HistoryGraph.Render(); RefreshStats(); };
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
    /// clicking away dismisses the window. Close, not Hide — App's singleton nulls its reference on
    /// Closed and recreates it cheaply, and with no title bar there is no other way to dismiss it.
    /// If an origin rect exists, shrinks the window back into it first (<see cref="AnimateRect"/>)
    /// and closes once that finishes; otherwise closes instantly, same as before.</item>
    /// <item><b>Activated</b> (first time) → start the open animation, now that the window is
    /// actually on screen at its origin rect.</item>
    /// </list>
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_everActivated) return;   // spurious pre-activation deactivate — see field doc
            if (_closing || _dismissing) return;
            _dismissing = true;

            if (_originRect is { } origin)
            {
                // Retract from wherever the window CURRENTLY is — it's resizable, so this may not
                // be _finalRect — back down into the dashboard's rect, then close.
                var current = new RectInt32(
                    AppWindow.Position.X, AppWindow.Position.Y,
                    AppWindow.Size.Width, AppWindow.Size.Height);
                AnimateRect(current, origin, Close);
            }
            else
            {
                // Nothing sensible to retract into — dismiss instantly, as before.
                Close();
            }
            return;
        }

        _everActivated = true;

        // _originRect non-null ⇔ an open animation was requested; _animStarted latches it one-shot.
        if (!_animStarted && _originRect is { } openOrigin)
        {
            _animStarted = true;
            AnimateRect(openOrigin, _finalRect, HistoryGraph.Render);
        }
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread with a guaranteed catch — same pattern
    /// as <see cref="DashboardWindow"/>/<see cref="BatteryHistoryGraphControl"/>'s own RunOnUi. An
    /// exception thrown inside a raw DispatcherQueue.TryEnqueue callback is NOT surfaced to
    /// Application.UnhandledException — it tears the whole process down as an opaque stowed
    /// exception with nothing logged anywhere. The wattage read below completes on a background
    /// thread and touches UI elements afterward; if the window closed in the meantime, catching
    /// here keeps the app alive instead of crashing it.
    /// </summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        try { action(); }
        catch (Exception ex) { AppLog.Error("BatteryHistoryWindow.RunOnUi", ex); }
    });

    /// <summary>
    /// Refreshes the POWER/REMAINING stats from a fresh <see cref="BatteryReport"/>, same formatting
    /// as <see cref="DashboardWindow"/> via <see cref="BatteryStatsFormatter"/>. The adapter wattage
    /// (TODO #41) is memoized in <see cref="ChargerInfoService"/>: the warm path is a plain cached
    /// read painted immediately; only a cold cache (first AC read of a session) does the RPC, off
    /// the UI thread, then repaints.
    /// </summary>
    private void RefreshStats()
    {
        try
        {
            var report = Battery.AggregateBattery.GetReport();
            bool onAC  = BatteryStatsFormatter.IsOnAC(report.Status);
            int  rateMw = report.ChargeRateInMilliwatts ?? 0;

            int? watts = ChargerInfoService.CachedWattage;   // never RPCs — UI-thread safe
            PowerSourceText.Text   = BatteryStatsFormatter.FormatPowerSource(onAC, rateMw, watts);
            TimeRemainingText.Text = BatteryStatsFormatter.FormatTimeRemaining(
                report.ChargeRateInMilliwatts, report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);

            if (onAC && watts is null)
                Task.Run(() =>
                {
                    // Warm the cache off-thread; on success re-enter RefreshStats (now the no-RPC
                    // warm path) so the repaint reads a FRESH report rather than the onAC/rateMw
                    // captured before the RPC — which could be seconds stale if AC changed meanwhile.
                    if (ChargerInfoService.GetRatedWattage() is not null)
                        RunOnUi(RefreshStats);
                });
        }
        catch (Exception ex)
        {
            AppLog.Error("BatteryHistoryWindow.RefreshStats", ex);
        }
    }

    private void ConfigureWindowChrome() =>
        WindowChrome.ApplyPopup(this, resizable: true, alwaysOnTop: false);

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
    /// Animates the window's <see cref="AppWindow"/> rect from <paramref name="from"/> to
    /// <paramref name="to"/> on an ease-out curve, then invokes <paramref name="onComplete"/>.
    /// Shared by both directions: the open-grow animation (dashboard rect → final centred rect,
    /// started from the first Activated event, NOT the ctor — see the class remarks on why) and the
    /// close-retract animation (the window's current rect → dashboard rect). The same ease-out
    /// curve reads well in reverse too: a fast initial shrink that settles gently into the target
    /// rect, rather than a jarring snap right at the dashboard's edge.
    /// <para/>
    /// Any animation already in flight is stopped before starting a new one — e.g. a click-away
    /// landing mid-open (fast enough to still be growing) must not leave that timer ticking
    /// alongside a newly-started retract timer, fighting over the same AppWindow.
    /// <para/>
    /// No per-tick graph redraws — the control's own SizeChanged debounce absorbs the burst of
    /// intermediate sizes; callers that care (only the open path) pass a Render() as
    /// <paramref name="onComplete"/> to land the final frame explicitly.
    /// </summary>
    private void AnimateRect(RectInt32 from, RectInt32 to, Action onComplete)
    {
        _animTimer?.Stop();
        AppWindow.MoveAndResize(from);   // ensure we start exactly at "from"

        long startMs = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AnimTickMs) };
        _animTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_closing) { timer.Stop(); return; }   // window torn down mid-animation

            // Wall-clock progress, not per-tick increments — DispatcherTimer ticks can be late
            // under load, and elapsed-time-based t keeps the total duration honest regardless.
            double t = Math.Min(1.0, (Environment.TickCount64 - startMs) / (double)AnimDurationMs);
            if (t >= 1.0)
            {
                timer.Stop();
                AppWindow.MoveAndResize(to);   // snap exactly to target — no rounding drift
                onComplete();
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic: fast start, gentle landing
            AppWindow.MoveAndResize(new RectInt32(
                Lerp(from.X,      to.X,      eased),
                Lerp(from.Y,      to.Y,      eased),
                Lerp(from.Width,  to.Width,  eased),
                Lerp(from.Height, to.Height, eased)));
        };
        timer.Start();
    }

    /// <summary>Integer lerp for the animation rect's components.</summary>
    private static int Lerp(int from, int to, double t) => from + (int)Math.Round((to - from) * t);
}
