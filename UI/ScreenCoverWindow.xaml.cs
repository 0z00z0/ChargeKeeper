using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ChargeKeeper.UI;

/// <summary>
/// One display's black cover, held up for the length of a focus session. It takes the screen and
/// nothing else: it never activates, the mouse passes through it, and whatever had the keyboard
/// keeps it, so a program already running carries on.
/// </summary>
/// <remarks>
/// <para>Bounds come from the display this cover is for, in physical pixels, so a mixed-scale
/// arrangement leaves no strip uncovered. The panel it reveals is sized in device-independent units
/// and scaled as one, so it is the same physical size whatever that display's scale factor is.</para>
/// <para>Nothing here ends a session. The cover goes when the session does, and a run that died
/// leaves no cover behind because a window does not outlive its process.</para>
/// </remarks>
internal sealed partial class ScreenCoverWindow : Window
{
    // The dashboard gauge's proportions on a 220-unit canvas: the largest radius that keeps the
    // stroke's outer edge inside it.
    private const double Cx = 110;
    private const double Cy = 110;
    private const double Radius = 92;

    private readonly SolidColorBrush _fill = new();

    private IntPtr _hwnd;

    internal ScreenCoverWindow()
    {
        InitializeComponent();
        Title = "ChargeKeeper focus session";

        RingTrack.Data = RingGeometry.Arc(Cx, Cy, Radius, RingGeometry.StartAngle, RingGeometry.Sweep);
        RingFill.Stroke = _fill;

        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        // Before the first show: an extended style set afterwards is not reliably picked up.
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        NativeMethods.MakeClickThroughAndUnfocusable(_hwnd);
    }

    /// <summary>Puts the cover over <paramref name="bounds"/> — one display's whole panel in
    /// physical pixels, the taskbar's strip included — and shows it without taking focus.</summary>
    internal void Cover(RectInt32 bounds)
    {
        AppWindow.MoveAndResize(bounds);
        AppWindow.Show(activateWindow: false);
        KeepOnTop();
    }

    /// <summary>Puts the cover back at the top of the topmost band. Called on the tick, because a
    /// window created topmost after this one sits above it until this runs again.</summary>
    internal void KeepOnTop() => NativeMethods.RaiseToTopmost(_hwnd);

    /// <summary>Draws the countdown and decides whether the panel is showing. The ring empties as
    /// the session runs down and takes the draining scale's colour at whatever is left.</summary>
    internal void Apply(FocusCoverReading? reading, string levers, bool revealed)
    {
        if (reading is { } r)
        {
            RingFill.Data = RingGeometry.Arc(Cx, Cy, Radius, RingGeometry.StartAngle,
                                             RingGeometry.Sweep * r.FractionLeft);
            _fill.Color   = AppColors.FromPacked(r.Argb);
            MinutesText.Text = r.MinutesLeft.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }

        LeversText.Text  = levers;
        Reveal.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
    }
}
