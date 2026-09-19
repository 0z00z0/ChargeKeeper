using Microsoft.UI.Xaml;
using Windows.Graphics;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Sizes a frameless-style popup window (About, What's new) to its own content: grown to fit and
/// capped at <see cref="WindowFit.FirstOpenHeightFraction"/> of the work area, then re-centred
/// within it. The first activation arrives before the first layout pass, so the caller retries the
/// fit when the content panel takes its real height — this holds the state that makes a retry cheap
/// (fit and log once per open, not once per layout pass).
/// </summary>
internal sealed class PopupWindowFit
{
    private readonly Window _window;
    private readonly FrameworkElement _panel;
    private readonly Thickness _scrollerPadding;
    private readonly int _widthDip;
    private readonly int _minHeightDip;
    private readonly string _label;

    private int _fittedHeightPx;
    private bool _deferralLogged;

    /// <param name="window">The popup being sized.</param>
    /// <param name="panel">The scroller's content panel — its own height, not the scroller's
    /// viewport, since the panel's height is independent of the window's and is final after the
    /// first layout.</param>
    /// <param name="scrollerPadding">The scroller's padding, added to the panel's height.</param>
    /// <param name="widthDip">The window's fixed width in DIPs.</param>
    /// <param name="minHeightDip">The floor the window never shrinks below.</param>
    /// <param name="label">Names the window in the one log line per open.</param>
    internal PopupWindowFit(Window window, FrameworkElement panel, Thickness scrollerPadding,
                            int widthDip, int minHeightDip, string label)
    {
        _window          = window;
        _panel           = panel;
        _scrollerPadding = scrollerPadding;
        _widthDip        = widthDip;
        _minHeightDip    = minHeightDip;
        _label           = label;
    }

    internal void FitToContent()
    {
        double panel = _panel.ActualHeight;
        double scale = _panel.XamlRoot?.RasterizationScale ?? 0;
        if (panel <= 0 || scale <= 0)
        {
            if (!_deferralLogged)
                AppLog.Info($"{_label} fit deferred: content not laid out yet.");
            _deferralLogged = true;
            return;
        }

        double content = panel + _scrollerPadding.Top + _scrollerPadding.Bottom;

        // AppWindow sizes are physical px; everything measured above is DIPs.
        var pos      = _window.AppWindow.Position;
        var size     = _window.AppWindow.Size;
        int chromePx = size.Height - _window.AppWindow.ClientSize.Height;

        if (NativeMethods.WorkAreaForRect(pos.X, pos.Y, size.Width, size.Height) is not { } work)
        {
            AppLog.Info($"{_label} fit skipped: the monitor's work area could not be read.");
            return;
        }

        int heightPx = WindowFit.PopupHeightPx(content, scale, chromePx, work.H, _minHeightDip);
        int widthPx  = Math.Min(WindowFit.ToPhysicalPixels(_widthDip, scale), work.W);
        if (heightPx == _fittedHeightPx) return;
        _fittedHeightPx = heightPx;

        AppLog.Info($"{_label} fit: content {content:F0} DIP at scale {scale}, chrome {chromePx} px, " +
                    $"cap {WindowFit.FirstOpenHeightCap(work.H)} px of work area {work.W}x{work.H} px " +
                    $"-> {_widthDip}x{heightPx / scale:F0} DIP = {widthPx}x{heightPx} px.");

        var rect = new RectInt32(work.X + (work.W - widthPx) / 2, work.Y + (work.H - heightPx) / 2,
                                 widthPx, heightPx);
        _window.AppWindow.MoveAndResize(rect);
    }
}
