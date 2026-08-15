using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;
using ChargeKeeper.Features;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// The Settings window (TODO #19) — replaces the tray menu's old 4-level-deep
/// Settings ▸ Network profiles ▸ Add configuration ▸ &lt;preset&gt; tree with a proper,
/// titled, resizable NavigationView window (left sidebar + content pane — "Concept A" from the
/// issue), using <see cref="SettingsCard"/>/<see cref="SettingsExpander"/> rows for the same
/// visual language PowerToys Settings and Files use.
///
/// <para>
/// Save model (smart commit, no global Save button):
/// <list type="bullet">
/// <item>Toggles/dropdowns apply immediately on change.</item>
/// <item>Ordinary text/number fields commit on focus-loss or Enter (NumberBox already defers its
/// own <c>Value</c> updates from raw typing until then — see <see cref="OnStartupDelayChanged"/>
/// and friends; only a spin-button click or a Home-Assistant broker field needs anything hand-
/// wired).</item>
/// <item>The Home Assistant/MQTT broker fields (host/port/user/pass/TLS/prefix, plus the #87 device
/// name — it forces the same reconnect/republish) are the ONE exception: they stage locally and
/// commit as a batch behind the explicit "Apply" button (see <see cref="OnHaApplyClicked"/>), so
/// <c>HomeAssistantService</c> reconnects at most once per edit session, never per keystroke. The
/// device ID is deliberately NOT in that batch — see <see cref="OnChangeNodeIdClicked"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// Every commit funnels through <see cref="SettingsService.Update"/> and then
/// <see cref="TrayMenu.ReconcileFromExternalChange"/> (bare — no toast; this IS the window the
/// user is looking at). Device-affecting or network-affecting side effects
/// (<c>ChargeThresholdService.SetThresholds</c>, the Home-Assistant reconnect callback) fire only
/// for the specific commit that actually needs them, not unconditionally on every keystroke.
/// </para>
///
/// <para>
/// Single reusable instance owned by <c>App</c> (see <c>App.ShowSettingsWindow</c>), mirroring
/// the existing <c>_dashboard</c>/<c>_historyWindow</c> singleton pattern. Deliberately does NOT
/// use <see cref="ChargeKeeper.Helpers.WindowChrome.ApplyPopup"/> — that chrome auto-dismisses on
/// focus loss, which would close this window mid-edit (e.g. while typing a broker password into
/// another app's copy/paste flow, or just alt-tabbing away). Plain default WinUI3 chrome already
/// gives a titled, resizable, taskbar/Alt-Tab-visible window with no extra code.
/// </para>
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private const string AppName = AppInfo.Name;
    // First-open default (DIPs, scaled to the monitor and capped to its work area). Sized as a
    // sensible max so the window is never oversized on a large/ultrawide screen — the content
    // otherwise lays out ~2580 px wide.
    private const int DefaultWidth  = 1200;
    private const int DefaultHeight = 750;

    private readonly TrayMenu _menu;
    private readonly Action   _onHomeAssistantChanged;

    // Raised after the preset LIST changes (add/rename/delete). HA's "Charge preset" select carries
    // its options inside the retained discovery config, published at connect time — without this the
    // dropdown keeps offering the old names until the next reconnect.
    private readonly Action   _onPresetsChanged;

    // Guards LoadXxx()'s programmatic control assignments from re-entering their own
    // changed/toggled/selection handlers and queuing a bogus commit — same pattern as
    // DashboardWindow's _updatingSliders. One shared flag is safe here: every LoadXxx() call runs
    // synchronously to completion (no awaited gap) before the next one starts.
    private bool _updating;

    // Every preset row's 700ms commit-debounce DispatcherTimer, tracked so RebuildPresetRows()
    // and OnClosed can Stop() them: a timer left running after its row is discarded (a DIFFERENT
    // preset renamed/added/deleted meanwhile, or the whole window closing) would otherwise fire
    // later against a detached row and either silently overwrite a fresh value with a stale one,
    // or — if the window closed — touch a torn-down window. Same failure class DashboardWindow's
    // own threshold-debounce timer is stopped in its Closed handler to avoid.
    private readonly List<DispatcherTimer> _presetDebounceTimers = [];

    public SettingsWindow(TrayMenu menu, Action onHomeAssistantChanged, Action onPresetsChanged)
    {
        _menu = menu;
        _onHomeAssistantChanged = onHomeAssistantChanged;
        _onPresetsChanged       = onPresetsChanged;

        InitializeComponent();
        Title = "ChargeKeeper Settings";

        // Pane-footer version line (issue #51). Same AppInfo.Version source the About box uses, so
        // the two can't drift; a plain field read that can't throw, safe before the SafeInit steps.
        VersionText.Text = $"v{AppInfo.Version}";

        // NOTHING below may throw out of the constructor. App.ShowSettingsWindow only assigns
        // _settings and calls Activate() once `new SettingsWindow(...)` returns — so a throw here
        // leaves an orphaned, never-shown window AND makes every later "Settings…" click leak
        // another hidden one (the "Settings window never appears" symptom, reproduced on a
        // multi-monitor setup where the window-placement API faulted). Each step is best-effort:
        // one failing piece degrades on its own instead of hiding the whole window.
        SafeInit(nameof(ConfigureWindowChrome), ConfigureWindowChrome);
        SafeInit(nameof(RefreshAllSections), RefreshAllSections);
        SafeInit(nameof(WireHaBrokerFieldEditHandlers), WireHaBrokerFieldEditHandlers);
        SafeInit(nameof(LoadAboutOnce), LoadAboutOnce);
        // Must run BEFORE the first layout pass: MeasureTallestPageExtent sizes the window to the
        // tallest page, and the Smart Charge page is much shorter once the preset and
        // network-profile sections are hidden on fixed-mode hardware. Applying it only on tab
        // switch would size the window for sections that never appear.
        SafeInit(nameof(ApplyThresholdCapabilityToSmartChargePage), ApplyThresholdCapabilityToSmartChargePage);
        SafeInit(nameof(WireKeepAwakeHandlers), WireKeepAwakeHandlers);
        SafeInit("SelectInitialSection", () =>
        {
            Nav.SelectedItem = Nav.MenuItems[0];
            ShowSection("General");
        });

        Closed += OnClosed;
    }

    private bool _aboutLoaded;

    /// <summary>
    /// Populates the embedded About panel — payload and card width both from
    /// <see cref="AboutContent"/>, so this surface cannot drift from the standalone
    /// <see cref="AboutWindow"/>.
    ///
    /// <para>MUST run at most once per window, and self-enforces that rather than trusting its one
    /// call site. <c>BrandAboutControl.SetInfo</c> is NOT idempotent: it APPENDS a line per external
    /// library to its credits panel and ADDS a repo-button Click handler, with no clear or
    /// unsubscribe. A second call therefore duplicates all six credit rows and makes one "GitHub"
    /// click open two browser tabs. That matters because this sits next to
    /// <see cref="RefreshAllSections"/>, which re-runs on every re-activation of an already-open
    /// window and after a settings reload — moving the About line into it looks like the natural
    /// tidy-up and would silently grow the credits list on each re-open. The guard makes that
    /// refactor harmless instead of a bug; the payload is static for the process lifetime
    /// (name/version/credits), so there is nothing to refresh anyway.</para>
    ///
    /// <para>Fixing <c>SetInfo</c> itself is the better repair, but <c>BrandAboutControl</c> lives in
    /// the shared 0z0-shared repo and is consumed by the sibling tray apps; guard here, from the
    /// side that knows its own call pattern.</para>
    /// </summary>
    private void LoadAboutOnce()
    {
        if (_aboutLoaded) return;
        _aboutLoaded = true;   // set BEFORE the call: a SetInfo that threw half-way through appending
                               // has already mutated the panel, so a retry would duplicate, not repair

        AboutCard.MaxWidth = AboutContent.ContentWidthDip;
        AboutInline.SetInfo(AboutContent.Build());
    }

    /// <summary>
    /// Runs one constructor step, swallowing + logging any failure so it cannot prevent the window
    /// from being shown. See the constructor note for why a throw out of the ctor is fatal to the
    /// whole window.
    /// </summary>
    private static void SafeInit(string step, Action body)
    {
        try { body(); }
        catch (Exception ex) { AppLog.Error($"SettingsWindow ctor step '{step}'", ex); }
    }

    /// <summary>
    /// Re-reads every section's controls from live settings. Called once from the constructor,
    /// and again by <c>App.ShowSettingsWindow</c> whenever the ALREADY-OPEN window is re-activated
    /// (a fresh "Settings…" click while it's still open, not a raw Alt-Tab) — otherwise a change
    /// made outside the window while it sat in the background (e.g. "Reload settings from file"
    /// from the tray menu, or an out-of-band edit to settings.json) would keep showing stale
    /// values here indefinitely. Any Home-Assistant broker field the user had typed but not yet
    /// clicked Apply on is discarded by this re-sync, same as closing the window would do.
    /// </summary>
    internal void RefreshAllSections()
    {
        LoadGeneral();
        LoadSmartCharge();
        LoadNotifications();
        LoadNetwork();
        LoadKeepAwake();
        LoadHomeAssistant();
    }

    // ── Window chrome / lifecycle ────────────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        var rect = ComputeInitialRect();
        // AppWindow.MoveAndResize is the same call the History window uses successfully on this
        // machine; guarded regardless so a placement failure can never stop the window from showing.
        try { AppWindow.MoveAndResize(rect); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.MoveAndResize", ex); }

        // Dark-theme the standard title bar so it matches the Mica BaseAlt backdrop.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        // The content cannot be measured yet — SettingsCard is templated, and a control outside a
        // live visual tree reports no useful size. Grow to fit once it has laid out.
        ContentScroller.Loaded += OnContentScrollerLoaded;
    }

    private bool _fittedToContent;

    private void OnContentScrollerLoaded(object sender, RoutedEventArgs e)
    {
        ContentScroller.Loaded -= OnContentScrollerLoaded;
        if (_fittedToContent) return;
        _fittedToContent = true;
        try { FitWindowToContent(); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.FitWindowToContent", ex); }
    }

    /// <summary>
    /// Grows the window so the tallest page fits without a vertical scrollbar, then re-clamps it to
    /// the work area (issue: Settings opened scrolled, and with a rect saved while docked it opened
    /// taller than the laptop panel).
    ///
    /// <para>The extra height is taken from the ScrollViewer's own overflow — extent minus viewport —
    /// rather than by adding up padding, the NavigationView header and the title bar. Those are what
    /// the two differ by, so measuring the difference gets all of them for free and cannot drift when
    /// the chrome changes.</para>
    /// </summary>
    private void FitWindowToContent()
    {
        double viewport = ContentScroller.ViewportHeight;
        double extent   = MeasureTallestPageExtent();
        if (viewport <= 0 || extent <= 0) return;   // not laid out yet — leave the opening rect alone

        var pos  = AppWindow.Position;
        var size = AppWindow.Size;

        // DesiredSize/extent are DIPs, MoveAndResize takes physical px — unscaled, this is 75% short
        // on the 175% laptop panel.
        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        int required = size.Height + (int)Math.Ceiling(Math.Max(0, extent - viewport) * scale);

        if (NativeMethods.WorkAreaForRect(pos.X, pos.Y, size.Width, size.Height) is not { } work) return;

        var (x, y, w, h) = WindowFit.Fit((pos.X, pos.Y, size.Width, size.Height), required, work);
        AppLog.Info($"SettingsWindow fit: extent={extent:F0} viewport={viewport:F0} scale={scale} " +
                    $"required={required} work={work.W}x{work.H} -> {w}x{h} @ {x},{y}");
        if (x == pos.X && y == pos.Y && w == size.Width && h == size.Height) return;

        try { AppWindow.MoveAndResize(new RectInt32(x, y, w, h)); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.FitMoveAndResize", ex); }
    }

    /// <summary>
    /// Height (DIPs) the scrollable content would take on its LONGEST page — currently Smart Charge,
    /// which absorbed the network profiles.
    ///
    /// <para>All six panels are siblings in one Grid cell and every inactive one is Collapsed, so
    /// measuring as-is only ever sizes the page that happens to be open. Making them all visible
    /// makes the Grid report the tallest of them (they overlap, so it is a max, not a sum), which is
    /// the number the window must fit. Visibility is restored before returning, so nothing the user
    /// sees changes.</para>
    /// </summary>
    private double MeasureTallestPageExtent()
    {
        FrameworkElement[] panels =
            [GeneralPanel, SmartChargePanel, KeepAwakePanel, NotificationsPanel, HomeAssistantPanel, AboutPanel];

        var saved = new Visibility[panels.Length];
        for (int i = 0; i < panels.Length; i++)
        {
            saved[i] = panels[i].Visibility;
            panels[i].Visibility = Visibility.Visible;
        }

        try
        {
            SectionHost.UpdateLayout();
            SectionHost.Measure(new Windows.Foundation.Size(SectionHost.ActualWidth, double.PositiveInfinity));
            return SectionHost.DesiredSize.Height + ContentScroller.Padding.Top + ContentScroller.Padding.Bottom;
        }
        finally
        {
            for (int i = 0; i < panels.Length; i++) panels[i].Visibility = saved[i];
            SectionHost.UpdateLayout();
        }
    }

    /// <summary>
    /// The window's opening rect (physical px): the saved size+position when one exists (clamped
    /// onto a currently-connected monitor), otherwise a default centred on the monitor under the
    /// cursor and capped to its work area so it is never oversized on a large screen. Both paths
    /// deliberately use the native MonitorFromPoint route, NOT DisplayArea.FindAll — the latter
    /// faulted in the constructor on a multi-monitor setup and, because a throw there left the
    /// window unactivated, made the Settings window never appear (the placement was lost and the
    /// window fell back to its oversized content-default size).
    /// </summary>
    private static RectInt32 ComputeInitialRect()
    {
        var s = SettingsService.Current;
        if (s.SettingsWindowX is { } x && s.SettingsWindowY is { } y &&
            s.SettingsWindowWidth is { } w && s.SettingsWindowHeight is { } h &&
            w > 0 && h > 0)
        {
            var (cx, cy, cw, ch) = NativeMethods.ClampRectToNearestMonitor(x, y, w, h);
            return new RectInt32(cx, cy, cw, ch);
        }

        return NativeMethods.CenterRectOnCursorMonitor(DefaultWidth, DefaultHeight);
    }

    /// <summary>
    /// Persists the window's final on-screen rect (physical pixels) to
    /// <see cref="SettingsService"/> — WinUIEx's own <c>PersistenceId</c> is NOT used here: it
    /// stores through <c>Windows.Storage.ApplicationData</c>, unavailable to this unpackaged app.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs e)
    {
        var pos  = AppWindow.Position;
        var size = AppWindow.Size;

        // Clamp before storing, never after reading: a rect saved on a monitor that is later gone
        // (docked, then closed on the laptop panel) is what put the window off-screen and oversized
        // in the first place. requiredHeight 0 — this must validate what the user chose, not resize it.
        var (x, y, w, h) = NativeMethods.ClampRectToNearestMonitor(pos.X, pos.Y, size.Width, size.Height);

        SettingsService.Update(s =>
        {
            s.SettingsWindowX      = x;
            s.SettingsWindowY      = y;
            s.SettingsWindowWidth  = w;
            s.SettingsWindowHeight = h;
        });

        StopAllPresetDebounceTimers();

        // Static event, instance handler: without this the closed window stays reachable from
        // KeepAwakeService for the process's life and keeps touching a torn-down UI tree.
        KeepAwakeService.StateChanged -= OnKeepAwakeStateChanged;
        _keepAwakeTicker.Stop();

        // An in-flight connection test outlives the window by up to its 10 s budget; cancelling makes
        // its continuation bail before it touches a torn-down control.
        _haProbeCts?.Cancel();
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto this window's UI thread — same guarded pattern as
    /// <c>BatteryHistoryGraphControl.RunOnUi</c>: an unhandled exception thrown inside a raw
    /// <see cref="DispatcherQueue"/> callback is a stowed exception that tears down the whole
    /// process, not just this window, so every callback that can run off a background Task must
    /// go through here rather than touching UI elements directly.
    /// </summary>
    private void RunOnUi(Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        try { action(); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.RunOnUi", ex); }
    });

    /// <summary>
    /// Runs <paramref name="apply"/> (a batch of programmatic control assignments) with the
    /// <see cref="_updating"/> re-entrancy guard raised, always lowering it in a <c>finally</c>.
    /// Every LoadXxx() must go through here: a bare <c>_updating = true; …; _updating = false;</c>
    /// pair leaves the flag stuck true if any assignment throws, silently disabling every future
    /// edit commit in the window.
    /// </summary>
    private void WithUpdatingSuppressed(Action apply)
    {
        _updating = true;
        try { apply(); }
        finally { _updating = false; }
    }

    /// <summary>
    /// Stops and forgets every outstanding preset-row debounce timer — called both when the rows
    /// are discarded (<see cref="RebuildPresetRows"/>) and when the window closes
    /// (<see cref="OnClosed"/>), so a still-armed timer can't fire ~700 ms later against a
    /// detached row or a torn-down window (see the <see cref="_presetDebounceTimers"/> comment).
    /// </summary>
    private void StopAllPresetDebounceTimers()
    {
        foreach (var t in _presetDebounceTimers) t.Stop();
        _presetDebounceTimers.Clear();
    }

    // ── Navigation ────────────────────────────────────────────────────────────────

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        GeneralPanel.Visibility       = tag == "General"       ? Visibility.Visible : Visibility.Collapsed;
        SmartChargePanel.Visibility   = tag == "SmartCharge"    ? Visibility.Visible : Visibility.Collapsed;
        KeepAwakePanel.Visibility     = tag == "KeepAwake"      ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPanel.Visibility = tag == "Notifications"  ? Visibility.Visible : Visibility.Collapsed;
        HomeAssistantPanel.Visibility = tag == "HomeAssistant"  ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility         = tag == "About"          ? Visibility.Visible : Visibility.Collapsed;

        // Cheap to refresh every time the tab is opened rather than on a timer — reflects a
        // network change made while the window was on a different tab. The network profiles
        // now live on the Smart Charge page.
        if (tag == "SmartCharge")
        {
            ApplyThresholdCapabilityToSmartChargePage();
            RefreshCurrentNetworkText();
        }

        // Same reasoning as the network line above: refresh the two status lines on every open rather
        // than run a clock, so a publish or command that landed while the window sat on another tab
        // shows up.
        if (tag == "HomeAssistant") RefreshHaActivityTexts();

        // The remaining-time line counts down, so it needs a tick — but only while it is on screen.
        if (tag == "KeepAwake")
        {
            RefreshKeepAwakeState();
            RefreshKeepAwakeCurrentNetworkText();
            _keepAwakeTicker.Start();
        }
        else _keepAwakeTicker.Stop();
    }

    /// <summary>
    /// Shows the preset/network-profile machinery, the vendor's fixed modes, or a plain
    /// explanation — whichever <see cref="ThresholdCapabilityPolicy.Classify"/> says this
    /// hardware warrants.
    ///
    /// Presets and network profiles are both expressed ONLY as start/stop percentages. On HP
    /// there is no numeric threshold at all — every preset snaps to the same on/off — so leaving
    /// the editors visible invites the user to build profiles that cannot differ from each other.
    /// That is worse than hiding them: it looks like a bug rather than a hardware limit.
    /// </summary>
    private void ApplyThresholdCapabilityToSmartChargePage()
    {
        // Read the state once: it decides the surface AND supplies the cap figure below.
        var state   = ChargeThresholdService.Read();
        var surface = ThresholdCapabilityPolicy.Classify(state, ChargeThresholdService.SupportsNumericThresholds);

        NumericThresholdSettings.Visibility = surface == SmartChargeSurface.Numeric    ? Visibility.Visible : Visibility.Collapsed;
        FixedModeSettings.Visibility        = surface == SmartChargeSurface.FixedModes ? Visibility.Visible : Visibility.Collapsed;
        NoThresholdInterfaceText.Visibility = surface == SmartChargeSurface.Hidden     ? Visibility.Visible : Visibility.Collapsed;

        if (surface != SmartChargeSurface.FixedModes) return;

        BuildChargeModeRadios();

        // A read-only BIOS setting is readable but refuses writes, so the radios would fail
        // silently on click.
        ChargeModeRadios.IsEnabled = state!.Capable;

        // Read the cap back from the device rather than hardcoding it, so the figure shown here
        // always matches what the dashboard and the hardware report.
        string cap = state is { Enabled: true, Stop: > 0 } ? $"about {state.Stop} %" : "a fixed level";

        FixedModeText.Text =
            $"This laptop's firmware offers fixed modes ({cap} of design capacity when limited) rather "
            + "than an adjustable range, so presets and network profiles do not apply and are hidden.\n\n"
            + "Windows will still report 100 % while a limit is active — this hardware lowers the "
            + "battery's reported full-charge capacity instead of stopping the charge early. "
            + "Changes take effect after a restart."
            + (state.Capable
                ? string.Empty
                : "\n\nThis setting is locked by the BIOS on this machine, so ChargeKeeper can show "
                  + "the current mode but not change it.");
    }

    /// <summary>
    /// Populates the mode radio group from the active vendor and selects whatever the firmware
    /// currently reports.
    /// </summary>
    private void BuildChargeModeRadios()
    {
        var modes = ChargeThresholdService.AvailableModes;

        _suppressChargeModeEvent = true;
        try
        {
            ChargeModeRadios.Items.Clear();

            foreach (var mode in modes)
            {
                var label = new TextBlock { Text = mode.Label };
                var description = new TextBlock
                {
                    Text         = mode.Description,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth     = 400,
                    FontSize     = 12,
                    Opacity      = 0.7,
                };

                ChargeModeRadios.Items.Add(new RadioButton
                {
                    // Tag carries the firmware id; the display text is never parsed back.
                    Tag     = mode.Id,
                    Content = new StackPanel { Children = { label, description } },
                });
            }

            // A null id — firmware reporting a mode this build doesn't list — deliberately leaves
            // every button unselected rather than highlighting a wrong one.
            string? current = ChargeThresholdService.ReadMode();
            ChargeModeRadios.SelectedIndex = current is null
                ? -1
                : IndexOfMode(modes, current);
        }
        finally { _suppressChargeModeEvent = false; }
    }

    private static int IndexOfMode(IReadOnlyList<ChargeMode> modes, string id)
    {
        for (int i = 0; i < modes.Count; i++)
            if (string.Equals(modes[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Guards the SelectionChanged handler against the programmatic selection made while
    /// populating the list, which would otherwise write the mode straight back to the firmware
    /// every time the Settings window opened.
    /// </summary>
    private bool _suppressChargeModeEvent;

    private void OnChargeModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChargeModeEvent) return;
        if (ChargeModeRadios.SelectedItem is not RadioButton { Tag: string id }) return;

        if (!ChargeThresholdService.SetMode(id))
        {
            // Write refused (firmware rejected it, or the setting is read-only). Snap the UI back
            // to what the device actually reports rather than leaving a selection that lies.
            AppLog.Info($"Charge mode write refused by firmware: {id}");
            BuildChargeModeRadios();
            return;
        }

        // Re-read rather than trusting the write: the dashboard's cap figure is derived from the
        // mode, and on this hardware a successful write can still be overridden by the firmware's
        // own adaptive logic.
        BuildChargeModeRadios();
    }

    // ── Preset-picker plumbing (issue #22) ─────────────────────────────────────────
    // Discrete settings are dropdowns, not spin controls (a NumberBox spinner is impractical for
    // picking a fixed value). Presets are (label, value) pairs; the underlying int is stored in the
    // ComboBoxItem's Tag so the display string is never parsed back.

    private static readonly (string Label, int Value)[] StartupDelayPresets =
        [("None", 0), ("2 s", 2), ("5 s", 5), ("10 s", 10), ("20 s", 20), ("30 s", 30), ("60 s", 60)];
    private static readonly (string Label, int Value)[] DowntimeGapPresets =
        [("None", 0), ("1 min", 1), ("2 min", 2), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("60 min", 60)];
    private static readonly (string Label, int Value)[] LowBattPctPresets =
        [("5 %", 5), ("10 %", 10), ("15 %", 15), ("20 %", 20), ("25 %", 25), ("30 %", 30), ("40 %", 40), ("50 %", 50)];
    private static readonly (string Label, int Value)[] DrainPctPresets =
        [("1 %/h", 1), ("2 %/h", 2), ("3 %/h", 3), ("5 %/h", 5), ("10 %/h", 10)];

    /// <summary>
    /// Populates a preset-picker <see cref="ComboBox"/> with its (label, value) items (each item's
    /// <see cref="FrameworkElement.Tag"/> holds the int) and selects the one matching
    /// <paramref name="current"/>. If the stored value is NOT one of the presets (a hand-edited
    /// settings.json, or a value from an earlier build), it's inserted as a custom entry and
    /// selected — so a user's stored value is shown, never silently overwritten. Call inside
    /// <see cref="WithUpdatingSuppressed"/> so populating it doesn't fire the change-commit.
    /// </summary>
    private static void LoadPresetCombo(ComboBox combo, (string Label, int Value)[] presets,
        int current, Func<int, string> formatCustom)
    {
        combo.Items.Clear();
        foreach (var (label, value) in presets)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        if (!presets.Any(p => p.Value == current))
            combo.Items.Insert(0, new ComboBoxItem { Content = formatCustom(current), Tag = current });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag! == current);
    }

    /// <summary>Commit half of the preset-picker: read the selected item's int Tag and save it.</summary>
    private void CommitPresetCombo(ComboBox combo, Action<AppSettings, int> save)
    {
        if (_updating || combo.SelectedItem is not ComboBoxItem { Tag: int value }) return;
        SettingsService.Update(s => save(s, value));
    }

    // ── General ───────────────────────────────────────────────────────────────────

    private void LoadGeneral()
    {
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            LoadPresetCombo(StartupDelayCombo, StartupDelayPresets, s.StartupDelaySeconds, v => $"{v} s");
            IconModeCombo.SelectedIndex   = (int)s.IconMode;
            GraphScaleCombo.SelectedIndex = (int)s.GraphTimeScale;
            LoadPresetCombo(DowntimeGapCombo, DowntimeGapPresets, s.DowntimeGapMinutes, v => $"{v} min");
        });
    }

    private void OnStartupDelayChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(StartupDelayCombo, (s, v) => s.StartupDelaySeconds = v);

    private void OnDowntimeGapChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(DowntimeGapCombo, (s, v) => s.DowntimeGapMinutes = v);

    private void OnIconModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || IconModeCombo.SelectedIndex < 0) return;
        var mode = (TrayIconMode)IconModeCombo.SelectedIndex;
        SettingsService.Update(s => s.IconMode = mode);
        _menu.ReconcileFromExternalChange();   // repaints the tray icon via the icon-mode callback
    }

    private void OnGraphScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || GraphScaleCombo.SelectedIndex < 0) return;
        var scale = (GraphTimeScale)GraphScaleCombo.SelectedIndex;
        SettingsService.Update(s => s.GraphTimeScale = scale);

        // Persisting alone never took effect: the in-memory window is only (re)loaded when a graph
        // host finds it EMPTY, so every open graph kept drawing the old span. Reload it exactly as the
        // graph's own scale buttons do (BatteryHistoryGraphControl.OnTimeScaleButtonClick) — a full CSV
        // scan, so off the UI thread. Open graphs repaint from the new window on their refresh tick.
        Task.Run(() =>
        {
            BatteryHistoryService.LoadWindow(scale.ToTimeSpan());
            AppLog.Info($"Time-scale changed to {scale}.");
        });
    }

    // ── Advanced (settings file) ─────────────────────────────────────────────────
    // The two settings-file actions relocated from the tray menu (TODO #28).

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Reveal(SettingsService.FilePath);

    /// <summary>
    /// Re-reads settings.json from disk (a manual edit, or a file synced in from another machine),
    /// then — since this window is the one showing those values — resyncs its own sections and the
    /// tray toggles. Was the tray's "Reload settings from file" command; moved here (TODO #28) so the
    /// entry point sits next to the settings it affects, and can call <see cref="RefreshAllSections"/>
    /// directly rather than through the old <c>OnExternalReload</c> hook. A toast confirms either
    /// outcome, matching the previous tray behaviour.
    /// </summary>
    private void OnReloadSettings(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Reload())
        {
            RefreshAllSections();                 // reflect the reloaded values in this open window
            _menu.ReconcileFromExternalChange();  // resync the tray toggles + icon
            NativeMethods.Info("Settings reloaded from disk.", AppName);
        }
        else
        {
            NativeMethods.Warn("Could not reload settings — the file is missing or invalid.", AppName);
        }
    }

    // ── Notifications ─────────────────────────────────────────────────────────────

    private void LoadNotifications()
    {
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            LowBattEnabledToggle.IsOn      = s.LowBatteryWarningEnabled;
            LoadPresetCombo(LowBattPctCombo, LowBattPctPresets, s.LowBatteryWarningPct, v => $"{v} %");
            LowBattPctCombo.IsEnabled      = s.LowBatteryWarningEnabled;
            DrainEnabledToggle.IsOn        = s.DrainAnomalyWarningEnabled;
            LoadPresetCombo(DrainPctPerHourCombo, DrainPctPresets, s.DrainAnomalyPercentPerHour, v => $"{v} %/h");
            DrainPctPerHourCombo.IsEnabled = s.DrainAnomalyWarningEnabled;
        });
    }

    private void OnLowBattEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = LowBattEnabledToggle.IsOn;
        LowBattPctCombo.IsEnabled = on;
        SettingsService.Update(s => s.LowBatteryWarningEnabled = on);
    }

    private void OnLowBattPctChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(LowBattPctCombo, (s, v) => s.LowBatteryWarningPct = v);

    private void OnDrainEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = DrainEnabledToggle.IsOn;
        DrainPctPerHourCombo.IsEnabled = on;
        SettingsService.Update(s => s.DrainAnomalyWarningEnabled = on);
    }

    private void OnDrainPctPerHourChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(DrainPctPerHourCombo, (s, v) => s.DrainAnomalyPercentPerHour = v);

    // ── Smart Charge (presets) ───────────────────────────────────────────────────

    private void LoadSmartCharge() => RebuildPresetRows();   // also (re)populates UnknownPresetCombo

    /// <summary>
    /// The Fluent "critical" system brush for the preset editor's inline validation error text.
    /// Looked up by key via <see cref="ResourceDictionary.TryGetValue"/> rather than the plain
    /// indexer (which throws on a missing key) — this app's own palette (<c>AppColors</c>)
    /// deliberately has no red, so this is the one place that needs a genuine error/critical
    /// colour, and it's safer to degrade to the default text colour than to risk a
    /// KeyNotFoundException while building a settings row.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.Brush? CriticalBrush() =>
        Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush)
            ? brush as Microsoft.UI.Xaml.Media.Brush
            : null;

    /// <summary>
    /// The ordinary secondary-text brush — needed only to put a TextBlock BACK after
    /// <see cref="CriticalBrush"/> has been assigned to it (an inline result line that alternates
    /// between error and plain status). Same defensive lookup as above.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.Brush? SecondaryBrush() =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush)
            ? brush as Microsoft.UI.Xaml.Media.Brush
            : null;

    private void RebuildPresetRows()
    {
        // Every existing row is about to be discarded — stop its debounce timer first so a drag
        // that's still settling on one row can't fire after this method returns and commit a
        // stale value on top of (or, if renamed away, silently fail to find) whatever the fresh
        // rebuild shows.
        StopAllPresetDebounceTimers();

        PresetsListPanel.Children.Clear();
        var presets = SettingsService.Current.Presets;

        if (presets.Count == 0)
        {
            PresetsListPanel.Children.Add(new TextBlock
            {
                Text = "No presets yet. Add one below.",
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 4),
            });
        }
        else
        {
            foreach (var preset in presets)
                PresetsListPanel.Children.Add(BuildPresetRow(preset));
        }

        RefreshUnknownPresetCombo();
    }

    /// <summary>
    /// Builds one preset's editor row: a collapsible <see cref="SettingsExpander"/> with a Name
    /// <see cref="TextBox"/> and a <see cref="RangeSelector"/> inside, plus a Delete button in the
    /// footer. Built entirely in code (not an ItemsRepeater/DataTemplate) so the RangeSelector's
    /// Minimum/Maximum can be set imperatively right after construction — required on this WinUI
    /// build regardless of XAML vs. code (see the RangeSelector remarks below); building the whole
    /// row this way just avoids a second, DataTemplate-specific place to remember that rule.
    /// The row's commit closures key off the preset's NAME (captured as a string), not the passed
    /// <see cref="ThresholdPreset"/> reference — the object supplies only the initial display values,
    /// while a concurrent <see cref="SettingsService.Reload"/> swapping
    /// <see cref="SettingsService.Current"/> out from under an open row can't leave a closure pointing
    /// at an orphaned object, because every commit re-looks-up the live preset by name at commit time.
    /// </summary>
    private SettingsExpander BuildPresetRow(ThresholdPreset preset)
    {
        string presetName = preset.Name;
        var expander = new SettingsExpander { Header = presetName };

        var nameBox = new TextBox { Text = preset.Name, MinWidth = 220 };

        // RangeSelector Minimum/Maximum MUST be set in code (not XAML markup) on this WinUI SDK
        // build — assigning them via the XAML type-converter throws a XamlParseException (see
        // DashboardWindow.ConfigureThresholdRange). Maximum before Minimum, same reasoning: it
        // never lets Minimum transiently exceed Maximum during assignment.
        // Stretch so the slider fills the full card width (issue #31) — the card itself uses
        // ContentAlignment.Vertical below so its content region spans edge-to-edge rather than being
        // squeezed into the right-hand column; without both, the RangeSelector renders too small to
        // operate comfortably.
        var range = new RangeSelector
        {
            Height              = 32,
            Margin              = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        range.Maximum       = PresetEditValidator.MaxThreshold;
        range.Minimum       = PresetEditValidator.MinThreshold;
        range.StepFrequency = 5;
        range.RangeStart    = preset.Start;
        range.RangeEnd      = preset.Stop;

        var startText = new TextBlock { Text = $"{preset.Start}%", FontSize = 12, Width = 36, VerticalAlignment = VerticalAlignment.Center };
        var stopText  = new TextBlock { Text = $"{preset.Stop}%",  FontSize = 12, Width = 36, VerticalAlignment = VerticalAlignment.Center, TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right };

        var rangeRow = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        rangeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rangeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rangeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(startText, 0);
        Grid.SetColumn(range, 1);
        Grid.SetColumn(stopText, 2);
        rangeRow.Children.Add(startText);
        rangeRow.Children.Add(range);
        rangeRow.Children.Add(stopText);

        var errorText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = CriticalBrush(),
        };
        var deleteBtn = new Button { Content = "Delete preset" };
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };
        footer.Children.Add(errorText);
        footer.Children.Add(deleteBtn);

        expander.Header = ThresholdPreset.FormatLabel(preset.Name, preset.Start, preset.Stop);
        expander.ItemsSource = new List<SettingsCard>
        {
            new SettingsCard { Header = "Name",                              Content = nameBox },
            // ContentAlignment.Vertical drops the slider onto its own row beneath the header instead
            // of the default right-aligned content column. That alone is NOT enough (issue #31): a
            // SettingsCard's HorizontalContentAlignment defaults to Right, so even in Vertical mode
            // the content presenter gives the Grid only its natural width and right-aligns it — the
            // star column collapses and the RangeSelector shrinks to minimum, crammed to the right
            // edge (~250px). HorizontalContentAlignment=Stretch is the actual root-cause fix: it lets
            // the [Auto,*,Auto] Grid span the full card width so the slider fills it. (DashboardWindow's
            // identical threshold Grid renders fine only because it sits directly in a full-width
            // StackPanel with nothing constraining its width.)
            new SettingsCard
            {
                Header                     = "Range (5-point minimum gap)",
                ContentAlignment           = ContentAlignment.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content                    = rangeRow,
            },
        };
        expander.ItemsFooter = footer;

        // Ordinary text field: commit on focus-loss or Enter (tier 2 of the save model).
        nameBox.LostFocus += (_, _) => CommitPresetRow(presetName, nameBox, range, errorText, expander);
        nameBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) CommitPresetRow(presetName, nameBox, range, errorText, expander); };

        // RangeSelector: debounced auto-commit, same 700 ms figure and "settle before committing"
        // reasoning as DashboardWindow's own threshold sliders — a drag fires ValueChanged many
        // times per second, and validating/saving on every tick would be wasteful and could reject
        // (and flash an error for) every INTERMEDIATE sub-5-point-gap position on the way to a
        // valid final one.
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _presetDebounceTimers.Add(debounce);
        // Stays in _presetDebounceTimers for the row's whole life — un-tracking it here would let the
        // NEXT ValueChanged re-Start an untracked timer that StopAllPresetDebounceTimers can no longer
        // stop. The list is cleared when the rows are rebuilt or the window closes.
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            CommitPresetRow(presetName, nameBox, range, errorText, expander);
        };
        range.ValueChanged += (_, _) =>
        {
            startText.Text = $"{(int)range.RangeStart}%";
            stopText.Text  = $"{(int)range.RangeEnd}%";
            debounce.Stop();
            debounce.Start();
        };

        deleteBtn.Click += (_, _) => DeletePreset(presetName);

        return expander;
    }

    /// <summary>
    /// Validates and, if valid, saves a preset row's current name/thresholds — the "reject-on-
    /// save" path (see <see cref="PresetEditValidator"/>): an invalid edit shows an inline error
    /// and is NOT written, leaving the row exactly as the user left it rather than silently
    /// correcting or discarding anything.
    /// </summary>
    private void CommitPresetRow(string originalName, TextBox nameBox, RangeSelector range,
        TextBlock errorText, SettingsExpander expander)
    {
        string newName = nameBox.Text?.Trim() ?? "";
        int start = (int)range.RangeStart;
        int stop  = (int)range.RangeEnd;

        var cur = SettingsService.Current;
        var otherNames = cur.Presets.Select(p => p.Name);
        string? error = PresetEditValidator.Validate(newName, start, stop, otherNames, originalName);
        if (error is not null)
        {
            errorText.Text = error;
            errorText.Visibility = Visibility.Visible;
            return;
        }
        errorText.Visibility = Visibility.Collapsed;

        bool renamed   = newName != originalName;
        bool wasActive = cur.ActivePreset == originalName;

        SettingsService.Update(s =>
        {
            // Always look up by originalName — the preset object still carries its old name at
            // this point, so looking it up by newName (before anything renames it) would find
            // nothing and silently drop both the rename AND the Start/Stop edit.
            var preset = s.Presets.FirstOrDefault(p => p.Name == originalName);
            if (preset is null) return;
            if (renamed)
            {
                PresetCascade.Rename(s, originalName, newName);
                preset.Name = newName;
            }
            preset.Start = start;
            preset.Stop  = stop;
        });

        // Push to the device immediately only when this preset is (still) the active one — editing
        // a preset that ISN'T active must not touch the device (reconcile contract, section C).
        if (wasActive)
            PushThresholdsToDevice(start, stop);

        if (renamed)
        {
            // This row's identity (the name every closure above keys off) is now stale — rebuild
            // the whole list rather than trying to re-key a live row in place. Network rule rows
            // show preset NAMES too (dropdown + summary text) — refresh them so a rule referencing
            // the old name doesn't keep offering a now-dangling option.
            RebuildPresetRows();
            RebuildNetworkRuleRows();
            _onPresetsChanged();   // HA's preset select carries the old name until discovery is republished
        }
        else
        {
            expander.Header = ThresholdPreset.FormatLabel(newName, start, stop);
        }

        _menu.ReconcileFromExternalChange();
    }

    /// <summary>
    /// Pushes thresholds to the device off the UI thread via the shared
    /// <see cref="ChargeControlService.SetExplicitThresholds"/> composition (which funnels
    /// <see cref="TravelOverrideService.ApplyExplicitThresholds"/> — deactivating any in-flight
    /// "charge to 100% once" override first — and fires StateChanged so the tray/tooltip/MQTT
    /// reconcile immediately, issue #40). <c>clearActivePreset</c> is left at its default (false):
    /// the ActivePreset here is managed by the callers (an edited preset stays active; the
    /// delete-fallback path already promoted the fallback via PresetCascade), so this write must NOT
    /// touch it. A write failure is surfaced with a TOAST, not the preset row's inline error text: by
    /// the time this async write completes the row may already be gone (a rename triggers a full
    /// rebuild, and the delete-fallback path has no row at all), so a row-bound error could silently
    /// vanish exactly when it matters — the whole point of reporting the failure. Silently discarding
    /// it would leave settings.json/tray/window all claiming a value the device never accepted.
    /// </summary>
    private void PushThresholdsToDevice(int start, int stop) => Task.Run(() =>
    {
        try
        {
            if (!ChargeControlService.SetExplicitThresholds(start, stop))
                RunOnUi(() => NativeMethods.Warn(
                    "Saved, but the device didn't accept these thresholds — check the Lenovo driver.",
                    AppName));
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.PushThresholdsToDevice", ex); }
    });

    private void DeletePreset(string name)
    {
        var s0 = SettingsService.Current;
        bool wasActive = s0.ActivePreset == name;
        var fallbackPreset = s0.Presets.FirstOrDefault(p => p.Name != name);
        string? fallback = fallbackPreset?.Name;

        SettingsService.Update(s => PresetCascade.Delete(s, name, fallback));

        // Every UI surface will show the fallback selected (via ReconcileFromExternalChange
        // below) the moment this returns — push its thresholds to the device too, or the physical
        // battery keeps running the just-deleted preset's values while every UI surface claims the
        // fallback is active. Same primitive (and same toast-on-failure) as an ordinary edit.
        if (wasActive && fallbackPreset is not null)
            PushThresholdsToDevice(fallbackPreset.Start, fallbackPreset.Stop);

        RebuildPresetRows();
        RebuildNetworkRuleRows();
        _onPresetsChanged();   // the deleted name must stop being offered in HA's preset select
        _menu.ReconcileFromExternalChange();
    }

    private void OnAddPreset(object sender, RoutedEventArgs e)
    {
        var existing = SettingsService.Current.Presets.Select(p => p.Name).ToList();
        string name = "New preset";
        for (int n = 2; existing.Contains(name, StringComparer.OrdinalIgnoreCase); n++)
            name = $"New preset {n}";

        SettingsService.Update(s => s.Presets.Add(new ThresholdPreset(name, 60, 80)));

        RebuildPresetRows();
        RebuildNetworkRuleRows();   // the new preset should be selectable from Network rows immediately
        _onPresetsChanged();        // …and from HA's preset select, which needs the option list republished
        _menu.ReconcileFromExternalChange();
    }

    private void RefreshUnknownPresetCombo()
    {
        const string doNothing = PresetEditValidator.UnknownNetworkSentinel;
        var s = SettingsService.Current;

        WithUpdatingSuppressed(() =>
        {
            UnknownPresetCombo.Items.Clear();
            UnknownPresetCombo.Items.Add(doNothing);
            foreach (var p in s.Presets) UnknownPresetCombo.Items.Add(p.Name);

            UnknownPresetCombo.SelectedItem =
                s.UnknownNetworkPresetName is { } name && s.Presets.Any(p => p.Name == name)
                    ? name
                    : doNothing;
        });
    }

    private void OnUnknownPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        string? selected = UnknownPresetCombo.SelectedItem as string;
        string? presetName = selected is null || selected == PresetEditValidator.UnknownNetworkSentinel ? null : selected;
        SettingsService.Update(s => s.UnknownNetworkPresetName = presetName);
    }

    // ── Network ───────────────────────────────────────────────────────────────────

    private void LoadNetwork()
    {
        WithUpdatingSuppressed(() => NetworkEnabledToggle.IsOn = SettingsService.Current.NetworkProfilesEnabled);
        RefreshCurrentNetworkText();
        RebuildNetworkRuleRows();
    }

    private void OnNetworkEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = NetworkEnabledToggle.IsOn;
        SettingsService.Update(s => s.NetworkProfilesEnabled = on);
    }

    private void RefreshCurrentNetworkText() =>
        CurrentNetworkText.Text = NetworkLocationService.DescribeCurrentLocation();

    /// <summary>
    /// Rebuilds the Smart Charge page's rule rows AND the Keep Awake page's — two renderings of the
    /// one <see cref="AppSettings.NetworkLocationRules"/> list, so they are always rebuilt together
    /// rather than leaving one page showing a rule the other has just deleted or renamed.
    /// </summary>
    private void RebuildNetworkRuleRows()
    {
        RebuildKeepAwakeNetworkRows();

        NetworkRulesListPanel.Children.Clear();
        var rules = SettingsService.Current.NetworkLocationRules;

        if (rules.Count == 0)
        {
            NetworkRulesListPanel.Children.Add(new TextBlock
            {
                Text = "No network profiles yet. Use “Add profile for this network…” below while connected to the network you want to configure.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 4),
            });
            return;
        }

        var presetNames = SettingsService.Current.Presets.Select(p => p.Name).ToList();
        for (int i = 0; i < rules.Count; i++)
            NetworkRulesListPanel.Children.Add(BuildNetworkRuleRow(i, rules[i], presetNames));
    }

    private static string DescribeMatchKey(NetworkLocationRule rule)
    {
        var parts = new List<string>();
        if (rule.AdapterMac is { } mac)  parts.Add($"MAC {mac}");
        if (rule.IpCidr    is { } cidr) parts.Add($"Subnet {cidr}");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No match key — this profile will never apply.";
    }

    private static string DescribeRulePresetSummary(NetworkLocationRule rule) =>
        string.IsNullOrEmpty(rule.PresetName) ? "No preset assigned" : $"Applies “{rule.PresetName}”";

    /// <summary>
    /// Builds one network profile's editor row. Keyed by LIST INDEX rather than name/reference:
    /// unlike presets, <see cref="NetworkLocationRule"/> has no unique identity of its own and two
    /// rules could in principle share a name — index is unambiguous as long as every mutation
    /// rebuilds the whole list afterwards (which every commit path below does).
    /// </summary>
    private SettingsExpander BuildNetworkRuleRow(int index, NetworkLocationRule rule, List<string> presetNames)
    {
        var expander = new SettingsExpander();

        var nameBox = new TextBox { Text = rule.Name, MinWidth = 220 };

        var presetCombo = new ComboBox { MinWidth = 220, PlaceholderText = "Choose a preset" };
        foreach (var n in presetNames) presetCombo.Items.Add(n);
        presetCombo.SelectedItem = presetNames.Contains(rule.PresetName) ? rule.PresetName : null;

        var deleteBtn = new Button { Content = "Delete profile" };
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };
        footer.Children.Add(deleteBtn);

        expander.Header      = rule.Name;
        expander.Description = DescribeRulePresetSummary(rule);
        expander.ItemsSource = new List<SettingsCard>
        {
            new SettingsCard { Header = "Name",    Content = nameBox },
            new SettingsCard { Header = "Matches", Description = DescribeMatchKey(rule) },
            new SettingsCard { Header = "Preset",  Content = presetCombo },
        };
        expander.ItemsFooter = footer;

        nameBox.LostFocus += (_, _) => CommitNetworkRuleName(index, nameBox.Text, expander);
        nameBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) CommitNetworkRuleName(index, nameBox.Text, expander); };
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (presetCombo.SelectedItem is string preset)
                CommitNetworkRulePreset(index, preset, expander);
        };
        deleteBtn.Click += (_, _) => DeleteNetworkRule(index);

        return expander;
    }

    private void CommitNetworkRuleName(int index, string? newNameRaw, SettingsExpander expander)
    {
        var rules = SettingsService.Current.NetworkLocationRules;
        if (index < 0 || index >= rules.Count) return;
        string newName = string.IsNullOrWhiteSpace(newNameRaw) ? rules[index].Name : newNameRaw!.Trim();

        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules[index].Name = newName;
        });
        expander.Header = newName;
        RebuildKeepAwakeNetworkRows();   // that page shows the rule NAME as its card header
    }

    private void CommitNetworkRulePreset(int index, string presetName, SettingsExpander expander)
    {
        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules[index].PresetName = presetName;
        });
        var rules = SettingsService.Current.NetworkLocationRules;
        if (index >= rules.Count) return;
        expander.Description = DescribeRulePresetSummary(rules[index]);

        // Apply the profile that now wins for the network we're currently on so the edit to the
        // active network's rule takes effect immediately (decided #19 follow-up). No-op if this
        // rule is shadowed by an earlier one, or matches no current network.
        ApplyWinningProfile(CurrentLocation());
    }

    // Current network location for the immediate-apply checks — LastKnown is the cheap cached
    // value; fall back to a live read only when it hasn't resolved yet.
    private static NetworkLocation CurrentLocation()
    {
        var loc = NetworkLocationService.LastKnown;
        return loc.IsEmpty ? NetworkLocationService.DetectCurrent() : loc;
    }

    /// <summary>
    /// Applies the preset of whatever rule currently WINS for <paramref name="location"/> —
    /// resolved via <see cref="AppSettings.FindNetworkRule"/> (FIRST match), exactly as the tray's
    /// own network-profile auto-apply does. Using the same resolution as the reconcile is what keeps
    /// an immediate apply from disagreeing with — and being reverted by — the next NetworkChange
    /// (the bug an earlier "any rule that Matches" check would have caused with overlapping rules).
    /// No-op when profiles are off or no rule matches.
    /// </summary>
    private void ApplyWinningProfile(NetworkLocation location)
    {
        var s = SettingsService.Current;
        if (!s.NetworkProfilesEnabled) return;
        if (s.FindNetworkRule(location) is { } rule) _menu.ApplyPresetByName(rule.PresetName);
    }

    private void DeleteNetworkRule(int index)
    {
        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules.RemoveAt(index);
        });
        RebuildNetworkRuleRows();
        RefreshCurrentNetworkText();   // the deleted rule may have been the one naming this network

        // Deleting the winning rule hands the current network to a later (or no) rule — apply whatever
        // wins now, same as editing a rule's preset does, so the device doesn't keep running the
        // deleted rule's preset while the UI says otherwise.
        ApplyWinningProfile(CurrentLocation());
    }

    /// <summary>
    /// "Add profile for this network…": fingerprints the CURRENT network, prompts for a friendly
    /// name via the existing <see cref="NameLocationWindow"/> (reused rather than rebuilt — see
    /// the issue's acceptance criteria), and appends a new rule defaulting to the currently active
    /// preset (or the first preset, or none), and — since the rule is for the network you're on
    /// right now — applies that preset to the device immediately, matching the old tray flow
    /// (decided #19 follow-up).
    /// </summary>
    private async void OnAddNetworkRule(object sender, RoutedEventArgs e)
    {
        // async void: an escaping exception tears the process down rather than surfacing. NameLocationWindow's
        // ctor does the same monitor/placement work this window wraps in SafeInit because it faulted on
        // multi-monitor — so guard the whole path, exactly as App.ShowSettingsWindow / TrayMenu.ShowAbout do.
        try
        {
            var location = NetworkLocationService.DetectCurrent();
            if (location.IsEmpty)
            {
                NativeMethods.Warn("No network detected right now — connect to a network first.", AppName);
                return;
            }

            string suggested = location.DisplayHint ?? (location.IsWired ? "Wired network" : "Wireless network");
            string? name = await new NameLocationWindow(suggested).ShowAsync();
            if (name is null) return;   // cancelled

            var s0 = SettingsService.Current;
            string defaultPreset = s0.ActivePreset ?? s0.Presets.FirstOrDefault()?.Name ?? "";

            SettingsService.Update(s =>
            {
                s.NetworkLocationRules.Add(new NetworkLocationRule
                {
                    Name       = name,
                    AdapterMac = location.AdapterMac,
                    IpCidr     = location.IpCidr,
                    PresetName = defaultPreset,
                });
                s.NetworkProfilesEnabled = true;   // configuring a location implies wanting the feature on
            });

            WithUpdatingSuppressed(() => NetworkEnabledToggle.IsOn = true);

            RebuildNetworkRuleRows();
            RefreshCurrentNetworkText();

            // Apply the profile that now wins for this network — usually the rule just added, unless an
            // earlier rule already shadows it — using the SAME first-match resolution the tray
            // auto-apply uses, so the immediate write agrees with the next reconcile instead of being
            // reverted by it (decided #19 follow-up; matches the old tray "add configuration → preset"
            // flow). Reuses the fresh `location` detected above.
            ApplyWinningProfile(location);
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnAddNetworkRule", ex); }
    }

    // ── Keep Awake (issue #90) ────────────────────────────────────────────────────
    // Every span on this page is TYPED and read by KeepAwakeInputParser — no TimePicker, no
    // NumberBox spinner. That fast entry is the feature; the Windows Settings pickers were
    // rejected as too heavy for "keep this awake till five".

    // Ticks the remaining-time line while the page is on screen. 30 s rather than 1 min: the line
    // is minute-resolution, so a minute-length tick can show a value a whole minute stale.
    private readonly DispatcherTimer _keepAwakeTicker =
        new() { Interval = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Subscribes the page to the things that change keep-awake behind its back — an expiry, the
    /// tray toggle, a network arrival — and starts the countdown ticker's wiring. Unsubscribed in
    /// <see cref="OnClosed"/>.
    /// </summary>
    private void WireKeepAwakeHandlers()
    {
        KeepAwakeService.StateChanged += OnKeepAwakeStateChanged;
        _keepAwakeTicker.Tick += (_, _) => RefreshKeepAwakeState();

        // Echo the parser's reading as the user types, so "1h30" is confirmed as 1 h 30 m BEFORE
        // Start is pressed rather than after the session is already running.
        KeepAwakeCustomBox.TextChanged += (_, _) => RefreshKeepAwakeCustomEcho();
        KeepAwakeCustomBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) StartKeepAwakeFromCustomBox();
        };
    }

    // Raised off the UI thread by KeepAwakeService — marshal before touching anything.
    private void OnKeepAwakeStateChanged() => RunOnUi(RefreshKeepAwakeState);

    private void LoadKeepAwake()
    {
        WithUpdatingSuppressed(() => KeepAwakeDisplayToggle.IsOn = SettingsService.Current.KeepAwakeDisplayOn);
        RefreshKeepAwakeState();
        RefreshKeepAwakeCustomEcho();
        RebuildKeepAwakeChips();
        RebuildKeepAwakePresetRows();
        RefreshKeepAwakeCurrentNetworkText();
        // The rule rows themselves come from LoadNetwork() → RebuildNetworkRuleRows(), which
        // rebuilds both pages' renderings of the shared list.
    }

    private void RefreshKeepAwakeState()
    {
        var session = KeepAwakeService.Current;
        WithUpdatingSuppressed(() => KeepAwakeToggle.IsOn = session is not null);
        KeepAwakeRemainingText.Text = session is null
            ? "Not holding this computer awake."
            : KeepAwakePolicy.DescribeRemaining(DateTimeOffset.Now, session);
    }

    private void OnKeepAwakeToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        // Through KeepAwakeFeature, the same entry point the tray toggle uses, so "on with no span
        // picked" cannot come to mean two different things on the two surfaces.
        new KeepAwakeFeature().SetEnabled(KeepAwakeToggle.IsOn);
        RefreshKeepAwakeState();
    }

    private void OnKeepAwakeDisplayToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = KeepAwakeDisplayToggle.IsOn;
        // Takes effect on the next Activate (KeepAwakeService re-applies the OS flags every time),
        // which is why the card says so rather than silently doing nothing to a running session.
        SettingsService.Update(s => s.KeepAwakeDisplayOn = on);
    }

    // ── Keep Awake: span wording ─────────────────────────────────────────────────
    // Three renderings of the same span, deliberately distinct: DISPLAY (full words, this page has
    // the room the dashboard chips do not), the PARSER ECHO (confirms how the typed text was read),
    // and the EDITABLE form (must round-trip back through KeepAwakeInputParser). A running
    // session's remaining time is not one of these — that is KeepAwakePolicy.DescribeRemaining,
    // the single formatter every surface shares.

    /// <summary>A saved preset as it reads on this page — its name when it has one, else its span.</summary>
    private static string DescribePreset(KeepAwakeRequest r) =>
        string.IsNullOrWhiteSpace(r.Name) ? DescribeSpan(r) : r.Name!;

    /// <summary>The row's subtitle: the span, but only when the header isn't already showing it.</summary>
    private static string DescribePresetSubtitle(KeepAwakeRequest r) =>
        string.IsNullOrWhiteSpace(r.Name) ? "" : DescribeSpan(r);

    /// <summary>The span in full words — "30 minutes", "3 hours", "1 h 30 m", "Until 17:00".</summary>
    private static string DescribeSpan(KeepAwakeRequest r)
    {
        switch (r.Kind)
        {
            case KeepAwakeKind.UntilNetworkChange: return "Until the network changes";
            case KeepAwakeKind.UntilTime when r.Until is { } t:
                return $"Until {t.ToString("HH\\:mm", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        // Indefinite — and any malformed request, which ExpiryFor also reads as "no expiry".
        if (r.Kind != KeepAwakeKind.Duration || r.Duration is not { } d || d <= TimeSpan.Zero)
            return "Until turned off";

        int total = (int)Math.Ceiling(d.TotalMinutes);
        return total switch
        {
            < 60                   => $"{total} minutes",
            _ when total % 60 == 0 => total == 60 ? "1 hour" : $"{total / 60} hours",
            _                      => $"{total / 60} h {total % 60} m",
        };
    }

    /// <summary>How the parser read what was typed, echoed under the box.</summary>
    private static string DescribeParsed(KeepAwakeRequest r) => r switch
    {
        { Kind: KeepAwakeKind.UntilTime, Until: { } t } =>
            $"Clock time: {t.ToString("HH\\:mm", System.Globalization.CultureInfo.InvariantCulture)}",
        { Kind: KeepAwakeKind.Duration, Duration: { } d } =>
            $"Duration: {(int)d.TotalHours} h {d.Minutes} m",
        _ => "",
    };

    /// <summary>
    /// The span as text KeepAwakeInputParser can read back — what an editable "Expires" box is
    /// seeded with. Empty for the two kinds the parser cannot produce, so the box invites a value
    /// instead of showing an uneditable one.
    /// </summary>
    private static string ToEditableSpan(KeepAwakeRequest r)
    {
        if (r.Kind == KeepAwakeKind.UntilTime && r.Until is { } t)
            return t.ToString("HH\\:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (r.Kind != KeepAwakeKind.Duration || r.Duration is not { } d || d <= TimeSpan.Zero) return "";

        int total = (int)Math.Ceiling(d.TotalMinutes);
        return total switch
        {
            < 60                   => $"{total}m",
            _ when total % 60 == 0 => $"{total / 60}h",
            _                      => $"{total / 60}h{total % 60}",
        };
    }

    // ── Keep Awake: quick card ───────────────────────────────────────────────────

    private void RebuildKeepAwakeChips()
    {
        KeepAwakeChipsPanel.Children.Clear();
        foreach (var preset in SettingsService.Current.KeepAwakePresets)
        {
            var captured = preset;
            var chip = new Button { Content = DescribePreset(captured) };
            chip.Click += (_, _) =>
            {
                KeepAwakeService.Activate(captured);
                RefreshKeepAwakeState();
            };
            KeepAwakeChipsPanel.Children.Add(chip);
        }
    }

    private void RefreshKeepAwakeCustomEcho()
    {
        // Typing is not an error — a half-typed "1h3" must not flash red. The inline error is
        // raised only by Start/Enter, which is the point the input has to be usable.
        KeepAwakeCustomErrorText.Visibility = Visibility.Collapsed;
        KeepAwakeCustomEchoText.Text =
            KeepAwakeInputParser.TryParse(KeepAwakeCustomBox.Text, out var request) ? DescribeParsed(request) : "";
    }

    private void OnKeepAwakeCustomStart(object sender, RoutedEventArgs e) => StartKeepAwakeFromCustomBox();

    private void StartKeepAwakeFromCustomBox()
    {
        if (!KeepAwakeInputParser.TryParse(KeepAwakeCustomBox.Text, out var request))
        {
            ShowInlineError(KeepAwakeCustomErrorText,
                "Enter a duration like 3h, 90m or 1h30, or a clock time like 17:00.");
            return;
        }

        KeepAwakeCustomErrorText.Visibility = Visibility.Collapsed;
        KeepAwakeService.Activate(request);
        RefreshKeepAwakeState();
    }

    /// <summary>The inline-validation half of the preset-row pattern, shared by the rows that only
    /// need the error line (no expander header to re-label).</summary>
    private static void ShowInlineError(TextBlock target, string message)
    {
        target.Text       = message;
        target.Foreground = CriticalBrush();
        target.Visibility = Visibility.Visible;
    }

    // ── Keep Awake: presets ──────────────────────────────────────────────────────
    // Keyed by LIST INDEX, same reasoning as the network rule rows: a KeepAwakeRequest is a value
    // with no identity of its own and two presets may legitimately be identical, so index is the
    // only unambiguous key — valid as long as every mutation rebuilds the whole list, which every
    // path below does.

    private void RebuildKeepAwakePresetRows()
    {
        KeepAwakePresetsListPanel.Children.Clear();
        var presets = SettingsService.Current.KeepAwakePresets;

        if (presets.Count == 0)
        {
            KeepAwakePresetsListPanel.Children.Add(new TextBlock
            {
                Text    = "No presets yet. Add one below.",
                Opacity = 0.7,
                Margin  = new Thickness(0, 4, 0, 4),
            });
            return;
        }

        for (int i = 0; i < presets.Count; i++)
            KeepAwakePresetsListPanel.Children.Add(BuildKeepAwakePresetRow(i, presets[i]));
    }

    /// <summary>
    /// One keep-awake preset's editor row — a name and ONE "Expires" box, because typing <c>3h</c>
    /// or <c>17:00</c> defines the kind and the value together and a separate kind picker would only
    /// let the two disagree. Same shape as <see cref="BuildPresetRow"/>: inline error, Delete in the
    /// footer, commit on focus-loss or Enter.
    /// </summary>
    private SettingsExpander BuildKeepAwakePresetRow(int index, KeepAwakeRequest preset)
    {
        var nameBox    = new TextBox { Text = preset.Name ?? "", MinWidth = 220, PlaceholderText = DescribeSpan(preset) };
        var expiresBox = new TextBox { Text = ToEditableSpan(preset), MinWidth = 220, PlaceholderText = "3h, 90m or 17:00" };

        var errorText = new TextBlock
        {
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility   = Visibility.Collapsed,
            Foreground   = CriticalBrush(),
        };
        var deleteBtn = new Button { Content = "Delete preset" };
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };
        footer.Children.Add(errorText);
        footer.Children.Add(deleteBtn);

        var expander = new SettingsExpander
        {
            Header      = DescribePreset(preset),
            Description = DescribePresetSubtitle(preset),
            ItemsSource = new List<SettingsCard>
            {
                new SettingsCard { Header = "Name",    Description = "Optional — the span is shown when this is blank.", Content = nameBox },
                new SettingsCard { Header = "Expires", Description = "A duration (3h, 90m, 1h30) or a clock time (17:00).", Content = expiresBox },
            },
            ItemsFooter = footer,
        };

        void Commit() => CommitKeepAwakePresetRow(index, nameBox, expiresBox, errorText, expander);
        nameBox.LostFocus    += (_, _) => Commit();
        nameBox.KeyDown      += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        expiresBox.LostFocus += (_, _) => Commit();
        expiresBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };

        deleteBtn.Click += (_, _) => DeleteKeepAwakePreset(index);

        return expander;
    }

    /// <summary>
    /// Validates and, if valid, saves one preset row — the same reject-on-save contract the
    /// threshold presets use: an unreadable "Expires" shows an inline error and writes NOTHING,
    /// leaving the row exactly as the user left it. A BLANK box keeps the stored span rather than
    /// erroring, so clearing the field can't destroy a preset by accident.
    /// </summary>
    private void CommitKeepAwakePresetRow(int index, TextBox nameBox, TextBox expiresBox,
        TextBlock errorText, SettingsExpander expander)
    {
        var presets = SettingsService.Current.KeepAwakePresets;
        if (index < 0 || index >= presets.Count) return;

        string expires = expiresBox.Text?.Trim() ?? "";
        KeepAwakeRequest span = presets[index];
        if (expires.Length > 0)
        {
            if (!KeepAwakeInputParser.TryParse(expires, out var parsed))
            {
                ShowInlineError(errorText, "Enter a duration like 3h, 90m or 1h30, or a clock time like 17:00.");
                return;
            }
            span = parsed;
        }
        errorText.Visibility = Visibility.Collapsed;

        string? name = nameBox.Text?.Trim() is { Length: > 0 } n ? n : null;
        var updated = span with { Name = name };

        SettingsService.Update(s =>
        {
            if (index < s.KeepAwakePresets.Count) s.KeepAwakePresets[index] = updated;
        });

        expander.Header      = DescribePreset(updated);
        expander.Description = DescribePresetSubtitle(updated);
        expiresBox.Text      = ToEditableSpan(updated);   // normalises "1h30m" to "1h30"
        nameBox.PlaceholderText = DescribeSpan(updated);
        RebuildKeepAwakeChips();   // the chip row shows these same presets
    }

    private void DeleteKeepAwakePreset(int index)
    {
        SettingsService.Update(s =>
        {
            if (index < s.KeepAwakePresets.Count) s.KeepAwakePresets.RemoveAt(index);
        });
        RebuildKeepAwakePresetRows();
        RebuildKeepAwakeChips();
    }

    private void OnAddKeepAwakePreset(object sender, RoutedEventArgs e)
    {
        // An hour is the least surprising thing to hand someone a row for; the point is that the row
        // exists and is editable, not the figure.
        SettingsService.Update(s => s.KeepAwakePresets.Add(
            new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromHours(1), null)));
        RebuildKeepAwakePresetRows();
        RebuildKeepAwakeChips();
    }

    // ── Keep Awake: networks ─────────────────────────────────────────────────────
    // The keep-awake FACET of the shared NetworkLocationRules list. The Smart Charge page edits the
    // preset facet of the same rules; neither page owns the list.

    private void RefreshKeepAwakeCurrentNetworkText() =>
        KeepAwakeCurrentNetworkText.Text = NetworkLocationService.DescribeCurrentLocation();

    private void RebuildKeepAwakeNetworkRows()
    {
        KeepAwakeNetworkRulesListPanel.Children.Clear();
        var rules = SettingsService.Current.NetworkLocationRules;

        if (rules.Count == 0)
        {
            KeepAwakeNetworkRulesListPanel.Children.Add(new TextBlock
            {
                Text = "No network rules yet. Use “Add rule for this network…” below while connected to the network you want to configure.",
                TextWrapping = TextWrapping.Wrap,
                Opacity      = 0.7,
                Margin       = new Thickness(0, 4, 0, 4),
            });
            return;
        }

        for (int i = 0; i < rules.Count; i++)
        {
            int index = i;
            var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = rules[i].KeepAwakeHere };
            toggle.Toggled += (_, _) => CommitKeepAwakeHere(index, toggle.IsOn);

            // A plain card, not an expander: there is exactly one field per rule on this page.
            KeepAwakeNetworkRulesListPanel.Children.Add(new SettingsCard
            {
                Header      = rules[i].Name,
                Description = DescribeMatchKey(rules[i]),
                Content     = toggle,
            });
        }
    }

    private void CommitKeepAwakeHere(int index, bool on)
    {
        if (_updating) return;
        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules[index].KeepAwakeHere = on;
        });
        ReconcileKeepAwakeForCurrentNetwork();
    }

    /// <summary>
    /// Applies the keep-awake facet of the rule that wins for the network we are on RIGHT NOW,
    /// mirroring <c>KeepAwakeService.OnLocationChanged</c>'s two rules. Without it, ticking "keep
    /// awake here" for the current network does nothing until you leave and come back — the
    /// service only ever reacts to a location CHANGE. Never overrides a session the user started by
    /// hand: it only starts when nothing is running, and only stops the network-kind session.
    /// </summary>
    private static void ReconcileKeepAwakeForCurrentNetwork()
    {
        var s = SettingsService.Current;
        bool wantsHold = s.NetworkProfilesEnabled &&
                         s.FindNetworkRule(CurrentLocation()) is { KeepAwakeHere: true };

        var current = KeepAwakeService.Current;
        if (wantsHold && current is null)
            KeepAwakeService.Activate(new KeepAwakeRequest(KeepAwakeKind.UntilNetworkChange, null, null));
        else if (!wantsHold && current?.Request.Kind == KeepAwakeKind.UntilNetworkChange)
            KeepAwakeService.Deactivate();
    }

    /// <summary>
    /// "Add rule for this network…" — the same fingerprint-then-name flow (and the same reused
    /// <see cref="NameLocationWindow"/>) as the Smart Charge page's version, differing only in which
    /// facet it fills in: <see cref="NetworkLocationRule.KeepAwakeHere"/> on, and no immediate
    /// threshold write. It deliberately does NOT call <c>ApplyWinningProfile</c>: the charge preset
    /// is the other page's facet, and on fixed-mode hardware there may be no presets to apply at all.
    /// </summary>
    private async void OnAddKeepAwakeNetworkRule(object sender, RoutedEventArgs e)
    {
        // async void — guarded whole, see OnAddNetworkRule.
        try
        {
            var location = NetworkLocationService.DetectCurrent();
            if (location.IsEmpty)
            {
                NativeMethods.Warn("No network detected right now — connect to a network first.", AppName);
                return;
            }

            string suggested = location.DisplayHint ?? (location.IsWired ? "Wired network" : "Wireless network");
            string? name = await new NameLocationWindow(suggested).ShowAsync();
            if (name is null) return;   // cancelled

            var s0 = SettingsService.Current;
            string defaultPreset = s0.ActivePreset ?? s0.Presets.FirstOrDefault()?.Name ?? "";

            SettingsService.Update(s =>
            {
                s.NetworkLocationRules.Add(new NetworkLocationRule
                {
                    Name          = name,
                    AdapterMac    = location.AdapterMac,
                    IpCidr        = location.IpCidr,
                    PresetName    = defaultPreset,
                    KeepAwakeHere = true,
                });
                // The rules are inert with profiles off — KeepAwakeService gates its auto-activate on
                // this flag — so configuring a location implies wanting the feature on, same as the
                // Smart Charge page's add flow.
                s.NetworkProfilesEnabled = true;
            });

            WithUpdatingSuppressed(() => NetworkEnabledToggle.IsOn = true);

            RebuildNetworkRuleRows();          // rebuilds BOTH pages' renderings of the rule list
            RefreshKeepAwakeCurrentNetworkText();
            RefreshCurrentNetworkText();
            ReconcileKeepAwakeForCurrentNetwork();
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnAddKeepAwakeNetworkRule", ex); }
    }

    // ── Home Assistant ────────────────────────────────────────────────────────────

    private void LoadHomeAssistant()
    {
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            HaEnabledToggle.IsOn   = s.HomeAssistantEnabled;
            HaHostBox.Text         = s.MqttBrokerHost;
            HaPortBox.Value        = s.MqttBrokerPort;
            HaUsernameBox.Text     = s.MqttUsername;
            HaPasswordBox.Password = s.MqttPassword;   // PasswordBox.Password has no XAML binding — set directly
            HaTlsToggle.IsOn       = s.MqttUseTls;
            HaPrefixBox.Text       = s.MqttDiscoveryPrefix;
            // Blank means "use the machine-derived default" — show that default as the placeholder
            // rather than pre-filling it, so an untouched field keeps meaning "default", not a
            // literal copy of today's machine name.
            HaDeviceNameBox.PlaceholderText = DefaultDeviceName();
            HaDeviceNameBox.Text            = s.MqttDeviceName;
        });
        RefreshHaNodeIdText();
        // A re-sync (reopen / tray Reload) discards any un-applied broker edit, so a leftover
        // "Applied." from a previous session must not linger asserting stale values are live.
        HaAppliedText.Visibility = Visibility.Collapsed;
        HaTestResultText.Visibility = Visibility.Collapsed;   // the tested values are gone with it
        RefreshHaBrokerStatusText();
        RefreshHaActivityTexts();
    }

    /// <summary>
    /// Hides the "Applied." confirmation the moment any broker field is edited — under the batch
    /// save model those edits are NOT live until the next Apply click, so the label would
    /// otherwise keep (falsely) asserting the shown values are the ones in effect. Wired once from
    /// the constructor; the seven batched controls have no other change handlers by design.
    /// </summary>
    private void WireHaBrokerFieldEditHandlers()
    {
        // The test result names a verdict about one exact set of values, so an edit invalidates it
        // for the same reason it invalidates "Applied." — leaving it up would let a stale "Connected."
        // vouch for a host the user has since retyped.
        void Hide()
        {
            HaAppliedText.Visibility    = Visibility.Collapsed;
            HaTestResultText.Visibility = Visibility.Collapsed;
        }
        HaDeviceNameBox.TextChanged   += (_, _) => Hide();
        HaHostBox.TextChanged         += (_, _) => Hide();
        HaUsernameBox.TextChanged     += (_, _) => Hide();
        HaPrefixBox.TextChanged       += (_, _) => Hide();
        HaPortBox.ValueChanged        += (_, _) => Hide();
        HaPasswordBox.PasswordChanged += (_, _) => Hide();
        HaTlsToggle.Toggled           += (_, _) => Hide();
    }

    // HomeAssistantEnabled is NOT one of the batched broker fields (see the save-model doc comment
    // above) — it's an ordinary toggle and applies immediately, same as every other toggle here.
    private void OnHaEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = HaEnabledToggle.IsOn;
        SettingsService.Update(s => s.HomeAssistantEnabled = on);
        _onHomeAssistantChanged();   // exactly one reconnect attempt for this toggle flip
    }

    /// <summary>
    /// Commits all seven batched fields as one batch — the ONE exception to "commit on change"
    /// in this window's save model, so <c>HomeAssistantService</c> reconnects at most once per
    /// Apply click rather than per keystroke. <see cref="AppSettings.MqttPassword"/> is read here
    /// (not on every keystroke) and is never logged or shown in any toast — see
    /// <c>HomeAssistantService.Sanitize</c>.
    /// <para><see cref="AppSettings.MqttDeviceName"/> (#87) belongs here: it is cosmetic in Home
    /// Assistant but reaches it through the same republish every broker field triggers. The device
    /// ID does NOT — it must be impossible to rename every entity as a side effect of applying a
    /// host edit, so it has its own dialog (<see cref="OnChangeNodeIdClicked"/>).</para>
    /// </summary>
    private void OnHaApplyClicked(object sender, RoutedEventArgs e)
    {
        string device = HaDeviceNameBox.Text?.Trim() ?? "";
        string host   = HaHostBox.Text?.Trim() ?? "";
        int    port   = StagedPort();
        string user   = HaUsernameBox.Text?.Trim() ?? "";
        string pass   = HaPasswordBox.Password ?? "";
        bool   tls    = HaTlsToggle.IsOn;
        string prefix = string.IsNullOrWhiteSpace(HaPrefixBox.Text) ? "homeassistant" : HaPrefixBox.Text.Trim();

        SettingsService.Update(s =>
        {
            s.MqttBrokerHost       = host;
            s.MqttBrokerPort       = port;
            s.MqttUsername         = user;
            s.MqttPassword         = pass;
            s.MqttUseTls           = tls;
            s.MqttDiscoveryPrefix  = prefix;
            s.MqttDeviceName       = device;
        });

        _onHomeAssistantChanged();   // exactly one reconnect attempt for this Apply click
        RefreshHaBrokerStatusText();
        RefreshHaActivityTexts();

        HaAppliedText.Visibility = Visibility.Visible;
    }

    /// <summary>The staged port, defaulted and clamped — read identically by Apply and by the test.</summary>
    private int StagedPort() =>
        double.IsNaN(HaPortBox.Value) ? 1883 : Math.Clamp((int)HaPortBox.Value, 1, 65535);

    private void RefreshHaBrokerStatusText()
    {
        var s = SettingsService.Current;
        HaBrokerStatusText.Text = string.IsNullOrWhiteSpace(s.MqttBrokerHost)
            ? "Broker: not set"
            : $"Broker: {s.MqttBrokerHost}:{s.MqttBrokerPort}";
    }

    // ── Connection check + live status ───────────────────────────────────────────

    /// <summary>
    /// The in-flight connection test, or null when none is running — the re-entrancy guard AND the
    /// handle <see cref="OnClosed"/> uses to cancel a probe that would otherwise resume against a
    /// torn-down window.
    /// </summary>
    private CancellationTokenSource? _haProbeCts;

    /// <summary>
    /// Tests the STAGED broker values — what is in the boxes right now, applied or not. That is the
    /// point of the button: check before committing. On an untouched form the boxes hold the saved
    /// configuration anyway, so the honest description ("the values in the boxes") is also the
    /// complete one, and it is spelled out on the page rather than left to be inferred.
    ///
    /// <para>async void with the whole body guarded — see <see cref="OnChangeNodeIdClicked"/>. The
    /// probe is awaited directly rather than pushed to <c>Task.Run</c>: it is I/O all the way down, so
    /// the UI thread is released at the first await and the continuation comes back on it naturally.</para>
    /// </summary>
    private async void OnHaTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (_haProbeCts is not null) return;   // a second click while one runs is dropped, not queued

        var cts = new CancellationTokenSource();
        _haProbeCts = cts;
        try
        {
            var target = new MqttProbeTarget(
                Host:     HaHostBox.Text?.Trim() ?? "",
                Port:     StagedPort(),
                Username: HaUsernameBox.Text?.Trim() ?? "",
                Password: HaPasswordBox.Password ?? "",
                UseTls:   HaTlsToggle.IsOn,
                ClientId: MqttConnectionProbe.ProbeClientId(EffectiveNodeId()));

            SetHaTestRunning(true);
            var result = await MqttConnectionProbe.RunAsync(target, cts.Token);

            if (cts.IsCancellationRequested) return;   // window closed mid-probe — touch nothing
            HaTestResultText.Text       = MqttConnectionProbe.Describe(result);
            HaTestResultText.Foreground = MqttConnectionProbe.IsFailure(result) ? CriticalBrush() : SecondaryBrush();
            HaTestResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnHaTestConnectionClicked", ex); }
        finally
        {
            _haProbeCts = null;
            cts.Dispose();
            if (!cts.IsCancellationRequested) SetHaTestRunning(false);
        }
    }

    private void SetHaTestRunning(bool running)
    {
        HaTestBtn.IsEnabled          = !running;
        HaTestProgress.IsActive      = running;
        HaTestProgress.Visibility    = running ? Visibility.Visible : Visibility.Collapsed;
        if (!running) return;

        HaTestResultText.Text       = "Testing…";
        HaTestResultText.Foreground = SecondaryBrush();
        HaTestResultText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Re-reads the two live-connection facts. Called when the page is shown and after an Apply, not
    /// on a timer: the only clock in this window is the Keep Awake page's countdown ticker, which is
    /// stopped whenever this page is on screen, and a relative age going a few minutes stale while the
    /// user sits on the page is not worth waking the machine for.
    /// </summary>
    private void RefreshHaActivityTexts()
    {
        var now = DateTime.UtcNow;
        HaLastPublishText.Text = MqttStatusFormatter.DescribeLastPublish(MqttActivity.LastPublishUtc, now);
        HaLastCommandText.Text = MqttStatusFormatter.DescribeLastCommand(MqttActivity.LastCommand, now);
    }

    // ── Device identity (#87) ────────────────────────────────────────────────────

    /// <summary>The device name used when <see cref="AppSettings.MqttDeviceName"/> is blank — the
    /// same expression <c>HomeAssistantService.ApplyAsync</c> falls back to, so the placeholder
    /// cannot promise a name the publisher wouldn't use.</summary>
    private static string DefaultDeviceName() => $"ChargeKeeper ({Environment.MachineName})";

    /// <summary>The id actually published under, override or machine-derived default.</summary>
    private static string EffectiveNodeId() =>
        HaDiscovery.EffectiveNodeId(SettingsService.Current.MqttNodeId, Environment.MachineName);

    private void RefreshHaNodeIdText() => HaNodeIdText.Text = EffectiveNodeId();

    /// <summary>
    /// The device ID's own confirmation dialog (#87). Deliberately outside the Apply batch: the id is
    /// the <c>unique_id</c>/<c>object_id</c> stem, the device identifier AND every topic segment, so
    /// changing it renames every entity in Home Assistant — that must never happen as a side effect
    /// of clicking Apply after editing a broker host.
    ///
    /// <para>Friction is two deliberate interactions (a valid, different id AND the acknowledgement
    /// tick) and no more: type-the-old-id ceremony buys nothing here, because the change is
    /// recoverable by typing the old id back — what is not recoverable is the HA-side history, and no
    /// amount of typing changes that.</para>
    ///
    /// <para>async void with the whole body guarded: an exception escaping an async void handler
    /// tears the process down rather than surfacing — same reasoning as
    /// <see cref="OnAddNetworkRule"/>.</para>
    /// </summary>
    private async void OnChangeNodeIdClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string current = EffectiveNodeId();

            var idBox = new TextBox
            {
                Text            = SettingsService.Current.MqttNodeId,
                PlaceholderText = HaDiscovery.NodeId(Environment.MachineName),
            };
            var errorText = new TextBlock
            {
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Visibility   = Visibility.Collapsed,
                Foreground   = CriticalBrush(),
            };
            // The id is sanitised to [a-z0-9_] before it reaches a topic, so echo what will actually
            // be published — otherwise "Office ThinkPad" silently becomes something else.
            var previewText = new TextBlock
            {
                FontSize     = 11,
                Opacity      = 0.7,
                TextWrapping = TextWrapping.Wrap,
            };
            var ack = new CheckBox { Content = "I understand my automations will break" };

            var body = new StackPanel { Spacing = 8, Width = 420 };
            body.Children.Add(new TextBlock
            {
                Text         = $"Current ID: {current}",
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock { Text = "New ID", Opacity = 0.7, FontSize = 12 });
            body.Children.Add(idBox);
            body.Children.Add(previewText);
            body.Children.Add(errorText);
            body.Children.Add(new TextBlock
            {
                Text = "Changing the ID renames every ChargeKeeper entity in Home Assistant. "
                     + "Automations, dashboards and history that point at the old entities will stop "
                     + "working — they will not report an error, the entities simply will not be there "
                     + "any more.\n\n"
                     + "ChargeKeeper removes the old entities from Home Assistant when you confirm. "
                     + "Their recorded history is not carried over to the new ones.\n\n"
                     + "Leave the box empty to go back to the name derived from this machine.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0),
            });
            body.Children.Add(ack);

            var dialog = new ContentDialog
            {
                XamlRoot          = Content.XamlRoot,
                Title             = "Change device ID",
                Content           = body,
                PrimaryButtonText = "Change ID",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
            };

            // Live validation: the primary button is the only gate, so re-derive it from scratch on
            // every edit rather than tracking a "was valid" flag that can go stale.
            void Revalidate()
            {
                string raw      = idBox.Text ?? "";
                string? error   = HaDiscovery.ValidateNodeId(raw);
                string candidate = HaDiscovery.EffectiveNodeId(raw, Environment.MachineName);

                errorText.Text       = error ?? "";
                errorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
                previewText.Text     = error is null ? $"Publishes as: {candidate}" : "";

                dialog.IsPrimaryButtonEnabled = error is null && candidate != current && ack.IsChecked == true;
            }

            idBox.TextChanged += (_, _) => Revalidate();
            ack.Checked       += (_, _) => Revalidate();
            ack.Unchecked     += (_, _) => Revalidate();
            Revalidate();

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Store the sanitised form, not the raw text: the read-only card above shows the
            // effective id, and storing the raw string would leave the two disagreeing. Blank stays
            // blank — that is the "use the machine default" sentinel, not an id.
            string entered = (idBox.Text ?? "").Trim();
            string newId   = entered.Length == 0 ? "" : HaDiscovery.NormalizeNodeId(entered);

            SettingsService.Update(s => s.MqttNodeId = newId);
            // Same callback the broker Apply uses — HomeAssistantService compares the new effective id
            // against the one it last published under and evicts the old id's retained topics
            // (HaDiscovery.TopicsToClear) before republishing discovery under the new one.
            _onHomeAssistantChanged();
            RefreshHaNodeIdText();
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnChangeNodeIdClicked", ex); }
    }

    // ── Appearance ──────────────────────────────────────────────────────────────
    // The Appearance section (a single dead "Use new styling" toggle that did nothing) was removed;
    // TODO #45 can restore an Appearance nav item + panel here when there's a real styling setting.

    // ── About ───────────────────────────────────────────────────────────────────
    // No handler needed: the About section hosts BrandAboutControl inline (populated in the ctor)
    // instead of a button that opened AboutWindow. The control owns its own link buttons.
}
