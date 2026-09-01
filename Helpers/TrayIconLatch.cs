using ChargeKeeper.Services;
using ChargeKeeper.Vendors;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Every input the live tray icon is drawn from. Value equality is the repaint dedupe key, so an
/// input <see cref="IconGenerator.RenderBatteryIcon"/> reads but this record omits is a change that
/// silently never repaints.
/// </summary>
/// <remarks>The power state is here in full, not as an on-AC flag: charging and idle-on-mains are
/// painted from different scales, so an edge between them at an unchanged level must repaint. The
/// flow is separate from the state and moves independently of it — a machine drawing more than its
/// adapter supplies turns the flow round while the state and the level stand still.</remarks>
internal readonly record struct TrayIconRequest(
    int Pct, PowerState State, TrayIconMode Mode, ChargeThresholdState? Threshold, PowerFlow? Flow = null);

/// <summary>
/// What the tray icon is actually showing, committed by the repaint itself rather than by the
/// caller that asked for one. A repaint marshalled onto the UI thread can be refused or throw, and
/// recording it as applied before it lands dedupes every later tick carrying the same state — on AC
/// held at a stop threshold, for hours.
/// </summary>
internal sealed class TrayIconLatch
{
    // Written by the UI thread, read by the battery-report thread. Its own lock, never held across
    // anything, so it cannot form an ordering with App's battery-report lock.
    private readonly System.Threading.Lock _gate = new();
    private TrayIconRequest? _painted;

    /// <summary>Whether <paramref name="request"/> differs from what is on screen. True until the
    /// first confirmed repaint.</summary>
    internal bool NeedsRepaint(TrayIconRequest request)
    {
        using (_gate.EnterScope()) return _painted != request;
    }

    /// <summary>Records a repaint that completed. Only the render calls this.</summary>
    internal void MarkPainted(TrayIconRequest request)
    {
        using (_gate.EnterScope()) _painted = request;
    }

    /// <summary>Forgets what is on screen, for the changes that move the pixels without moving the
    /// request — a new tray-slot DPI, or a tray icon the shell recreated.</summary>
    internal void Invalidate()
    {
        using (_gate.EnterScope()) _painted = null;
    }

    /// <summary>The reading a forced repaint draws. Before the first battery report there is none,
    /// and 0 % stands in: no-opping instead leaves a style, slot-size or tray-recreate change
    /// invisible until the next tick, which on AC at a stop threshold does not arrive for hours.</summary>
    internal static (int Pct, PowerState State, PowerFlow? Flow) ReadingOrUnknown(
        (int Pct, PowerState State, PowerFlow? Flow) lastReading) =>
        lastReading.Pct >= 0 ? lastReading : (0, PowerState.Discharging, null);
}
