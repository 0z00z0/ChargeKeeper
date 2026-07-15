using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;
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
/// <item>The Home Assistant/MQTT broker fields (host/port/user/pass/TLS/prefix) are the ONE
/// exception: they stage locally and commit as a batch behind the explicit "Apply" button (see
/// <see cref="OnHaApplyClicked"/>), so <c>HomeAssistantService</c> reconnects at most once per
/// edit session, never per keystroke.</item>
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
    private readonly Action   _onShowAbout;

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

    public SettingsWindow(TrayMenu menu, Action onHomeAssistantChanged, Action onShowAbout)
    {
        _menu = menu;
        _onHomeAssistantChanged = onHomeAssistantChanged;
        _onShowAbout = onShowAbout;

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
        SafeInit("SelectInitialSection", () =>
        {
            Nav.SelectedItem = Nav.MenuItems[0];
            ShowSection("General");
        });

        Closed += OnClosed;
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
    }

    /// <summary>
    /// The window's opening rect (physical px): the saved size+position when one exists (clamped
    /// onto a currently-connected monitor), otherwise a default centred on the monitor under the
    /// cursor and capped to its work area so it is never oversized on a large screen. Deliberately
    /// uses the native MonitorFromPoint / GetCursorMonitorMetrics path, NOT DisplayArea.FindAll —
    /// the latter faulted in the constructor on a multi-monitor setup and, because a throw there
    /// left the window unactivated, made the Settings window never appear (the placement was lost
    /// and the window fell back to its oversized content-default size).
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

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int workW = work.Right  - work.Left;
        int workH = work.Bottom - work.Top;
        int dw = Math.Min((int)(DefaultWidth  * scale), workW);
        int dh = Math.Min((int)(DefaultHeight * scale), workH);
        return new RectInt32(work.Left + (workW - dw) / 2, work.Top + (workH - dh) / 2, dw, dh);
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
        SettingsService.Update(s =>
        {
            s.SettingsWindowX      = pos.X;
            s.SettingsWindowY      = pos.Y;
            s.SettingsWindowWidth  = size.Width;
            s.SettingsWindowHeight = size.Height;
        });

        StopAllPresetDebounceTimers();
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
        NotificationsPanel.Visibility = tag == "Notifications"  ? Visibility.Visible : Visibility.Collapsed;
        NetworkPanel.Visibility       = tag == "Network"        ? Visibility.Visible : Visibility.Collapsed;
        HomeAssistantPanel.Visibility = tag == "HomeAssistant"  ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility         = tag == "About"          ? Visibility.Visible : Visibility.Collapsed;

        // Cheap to refresh every time the tab is opened rather than on a timer — reflects a
        // network change made while the window was on a different tab.
        if (tag == "Network") RefreshCurrentNetworkText();
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
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            _presetDebounceTimers.Remove(debounce);
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

    private void RebuildNetworkRuleRows()
    {
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
        });
        // A re-sync (reopen / tray Reload) discards any un-applied broker edit, so a leftover
        // "Applied." from a previous session must not linger asserting stale values are live.
        HaAppliedText.Visibility = Visibility.Collapsed;
        RefreshHaBrokerStatusText();
    }

    /// <summary>
    /// Hides the "Applied." confirmation the moment any broker field is edited — under the batch
    /// save model those edits are NOT live until the next Apply click, so the label would
    /// otherwise keep (falsely) asserting the shown values are the ones in effect. Wired once from
    /// the constructor; the six broker controls have no other change handlers by design.
    /// </summary>
    private void WireHaBrokerFieldEditHandlers()
    {
        void Hide() => HaAppliedText.Visibility = Visibility.Collapsed;
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
    /// Commits all six broker fields as a single batch — the ONE exception to "commit on change"
    /// in this window's save model, so <c>HomeAssistantService</c> reconnects at most once per
    /// Apply click rather than per keystroke. <see cref="AppSettings.MqttPassword"/> is read here
    /// (not on every keystroke) and is never logged or shown in any toast — see
    /// <c>HomeAssistantService.Sanitize</c>.
    /// </summary>
    private void OnHaApplyClicked(object sender, RoutedEventArgs e)
    {
        string host   = HaHostBox.Text?.Trim() ?? "";
        int    port   = double.IsNaN(HaPortBox.Value) ? 1883 : Math.Clamp((int)HaPortBox.Value, 1, 65535);
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
        });

        _onHomeAssistantChanged();   // exactly one reconnect attempt for this Apply click
        RefreshHaBrokerStatusText();

        HaAppliedText.Visibility = Visibility.Visible;
    }

    private void RefreshHaBrokerStatusText()
    {
        var s = SettingsService.Current;
        HaBrokerStatusText.Text = string.IsNullOrWhiteSpace(s.MqttBrokerHost)
            ? "Broker: not set"
            : $"Broker: {s.MqttBrokerHost}:{s.MqttBrokerPort}";
    }

    // ── Appearance ──────────────────────────────────────────────────────────────
    // The Appearance section (a single dead "Use new styling" toggle that did nothing) was removed;
    // TODO #45 can restore an Appearance nav item + panel here when there's a real styling setting.

    // #59: opens the same single About window the tray "About…" item uses, via the callback App
    // wired up from the tray menu — so there's never more than one About window instance.
    private void OnAboutClicked(object sender, RoutedEventArgs e) => _onShowAbout();
}
