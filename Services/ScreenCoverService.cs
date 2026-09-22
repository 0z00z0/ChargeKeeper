using ChargeKeeper.Helpers;
using ChargeKeeper.UI;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace ChargeKeeper.Services;

/// <summary>
/// The focus session's screen cover: one black window per attached display, held up for the length
/// of a session and taken down with it.
/// </summary>
/// <remarks>
/// <para>The tick does four things a second: it puts every cover back at the top of the topmost
/// band, rebuilds the set when the displays change, draws the countdown, and decides whether the
/// panel is showing. One timer rather than four, because each is cheap and all four have to answer
/// while a session runs.</para>
/// <para>Input is read, never taken. The reveal follows <see cref="NativeMethods.SinceLastInput"/>,
/// which reports that somebody touched the machine without reporting what they did and covers touch
/// as well as keyboard and mouse — so no hook is installed and no keystroke is seen.</para>
/// </remarks>
internal static class ScreenCoverService
{
    /// <summary>How long the panel stays up after the last input. Long enough to read the line and
    /// the countdown, short enough that the screen is black again before anyone looks away.</summary>
    private static readonly TimeSpan RevealFor = TimeSpan.FromSeconds(4);

    /// <summary>One second: the countdown moves in minutes, and a window created topmost after a
    /// cover sits above it for at most this long.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private static DispatcherQueue? _ui;
    private static Func<FocusSnapshot> _session = () => FocusSnapshot.None;
    private static DispatcherQueueTimer? _tick;

    private static readonly List<ScreenCoverWindow> _windows = [];
    private static IReadOnlyList<RectInt32> _covering = [];

    /// <summary>True from the moment a cover is asked for until it is taken down, whether or not the
    /// windows have been built yet — the build is marshalled onto the UI thread.</summary>
    internal static bool IsShowing { get; private set; }

    /// <summary>Whether there is anything to cover. A machine with no attached display would get a
    /// lever that changes nothing, which is refused rather than armed.</summary>
    internal static bool HasDisplay => NativeMethods.AllDisplayBounds().Count > 0;

    /// <summary>Hands the service the thread its windows live on and the session they follow.
    /// Called before <see cref="FocusSessionService.Start"/>, which can ask for a cover at once when
    /// it resumes a session the machine was switched off during.</summary>
    internal static void Start(DispatcherQueue ui, Func<FocusSnapshot> session)
    {
        _ui      = ui;
        _session = session;
    }

    /// <summary>Raises the cover. False only when the UI thread could not be reached, which is a
    /// lever that did not engage.</summary>
    internal static bool Show(string cause)
    {
        if (_ui is not { } ui) return false;
        if (IsShowing) return true;

        IsShowing = true;
        AppLog.Info($"Focus: the screen cover goes up ({cause}).");
        return ui.TryEnqueue(Raise);
    }

    /// <summary>Takes the cover down. True even when none was up: nothing is owed back.</summary>
    internal static bool Hide(string cause)
    {
        if (!IsShowing) return true;
        IsShowing = false;

        if (_ui is not { } ui) return false;
        AppLog.Info($"Focus: the screen cover comes down ({cause}).");
        return ui.TryEnqueue(Drop);
    }

    private static void Raise()
    {
        try
        {
            Rebuild();

            _tick ??= _ui!.CreateTimer();
            _tick.Interval = TickInterval;
            _tick.Tick -= OnTick;
            _tick.Tick += OnTick;
            _tick.Start();

            OnTick(null!, null!);
        }
        catch (Exception ex) { AppLog.Error("ScreenCoverService.Raise", ex); }
    }

    private static void Drop()
    {
        try
        {
            _tick?.Stop();
            foreach (var window in _windows)
            {
                try { window.Dismiss(); }
                catch (Exception ex) { AppLog.Error("ScreenCoverService.Close", ex); }
            }
            _windows.Clear();
            _covering = [];
        }
        catch (Exception ex) { AppLog.Error("ScreenCoverService.Drop", ex); }
    }

    private static void OnTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            // Defence, not the normal route: the lever takes the cover down when the session ends.
            // A cover with no session behind it is a black screen nobody can explain.
            var session = _session();
            if (!session.IsRunning) { Hide("no session is running"); return; }

            // A display added, removed, moved or re-resolved while a session runs changes this list,
            // and the covers are rebuilt against it. A cover that is gone is rebuilt too: whatever
            // took it down, it is back within a second, and this does not depend on knowing how.
            var displays = NativeMethods.AllDisplayBounds();
            if (!SameDisplays(displays, _covering)) Rebuild();
            else if (AnyCoverIsGone()) { AppLog.Info("Focus: a screen cover had gone, and is put back."); Rebuild(); }

            var reading  = FocusCoverCountdown.For(session, DateTimeOffset.Now);
            string levers = Levers(session);
            bool revealed = NativeMethods.SinceLastInput() is { } since && since < RevealFor;

            foreach (var window in _windows)
            {
                window.KeepOnTop();
                window.Apply(reading, levers, revealed);
            }
        }
        catch (Exception ex) { AppLog.Error("ScreenCoverService.OnTick", ex); }
    }

    /// <summary>Builds one cover per attached display, each sized from that display's own bounds.
    /// Torn down and built again rather than moved: a rebuild is rare and reusing a window across a
    /// display change is what leaves a strip uncovered.</summary>
    private static void Rebuild()
    {
        var displays = NativeMethods.AllDisplayBounds();

        foreach (var window in _windows)
        {
            try { window.Dismiss(); }
            catch (Exception ex) { AppLog.Error("ScreenCoverService.Rebuild close", ex); }
        }
        _windows.Clear();

        foreach (var bounds in displays)
        {
            try
            {
                var window = new ScreenCoverWindow();
                window.Cover(bounds);
                _windows.Add(window);
            }
            catch (Exception ex) { AppLog.Error("ScreenCoverService.Rebuild create", ex); }
        }

        _covering = displays;
        AppLog.Info($"Focus: the screen cover is over {_windows.Count} display(s).");
    }

    /// <summary>Whether any cover has been taken down under the service. A cover with no window
    /// left is a display showing whatever is behind it, which the rebuild puts right.</summary>
    private static bool AnyCoverIsGone()
    {
        foreach (var window in _windows) if (window.IsGone) return true;
        return false;
    }

    private static bool SameDisplays(IReadOnlyList<RectInt32> a, IReadOnlyList<RectInt32> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].X != b[i].X || a[i].Y != b[i].Y ||
                a[i].Width != b[i].Width || a[i].Height != b[i].Height)
                return false;
        return true;
    }

    /// <summary>What the session is doing, for the panel. The cover says it rather than leaving the
    /// machine looking broken to whoever is sitting at it.</summary>
    private static string Levers(FocusSnapshot session)
    {
        var levers = new List<string>(4);
        if (session.BlocksNetwork) levers.Add("the network is blocked");
        if (session.DimsScreen)    levers.Add("the screen is dimmed");
        if (session.CoversScreen)  levers.Add("the screen is covered");
        if (session.BlocksInput)   levers.Add("the mouse and keyboard are blocked");
        return levers.Count == 0 ? "" : $"While it runs, {string.Join(", ", levers)}.";
    }
}
