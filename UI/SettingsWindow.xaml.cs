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

/// <summary>The app's Settings window: a NavigationView with one panel per section, built from
/// <see cref="SettingsCard"/>/<see cref="SettingsExpander"/> rows. Every setting commits as it is
/// edited except the Home Assistant broker fields, which stage locally and commit as a batch behind
/// Apply so <c>HomeAssistantService</c> reconnects once per edit session, not per keystroke. Plain
/// WinUI chrome, not <see cref="ChargeKeeper.Helpers.WindowChrome.ApplyPopup"/> — that
/// auto-dismisses on focus loss, which would close the window mid-edit.</summary>
internal sealed partial class SettingsWindow : Window
{
    private const string AppName = AppInfo.Name;
    // First-open default in DIPs; the content otherwise lays out ~2580 px wide.
    private const int DefaultWidth  = 1200;
    private const int DefaultHeight = 750;

    private readonly TrayMenu _menu;
    private readonly Action   _onHomeAssistantChanged;

    // Raised when what is announced changes: a preset renamed, or a publishing group toggled. The
    // select options and the announced entity set are both baked into the retained discovery configs,
    // so without this the broker keeps the set captured at connect time.
    private readonly Action   _onDiscoveryChanged;

    // Suppresses the change handlers while LoadXxx() writes controls, so a programmatic assignment
    // can't queue a bogus commit. One shared flag is safe: each LoadXxx() runs synchronously.
    private bool _updating;

    // Every preset row's commit-debounce timer, tracked so one left running after its row is
    // discarded can be stopped before it fires against a detached row or a closed window.
    private readonly List<DispatcherTimer> _presetDebounceTimers = [];

    public SettingsWindow(TrayMenu menu, Action onHomeAssistantChanged, Action onDiscoveryChanged)
    {
        _menu = menu;
        _onHomeAssistantChanged = onHomeAssistantChanged;
        _onDiscoveryChanged     = onDiscoveryChanged;

        InitializeComponent();
        Title = "ChargeKeeper Settings";

        VersionText.Text = $"v{AppInfo.Version}";

        // Nothing below may throw out of the constructor: App.ShowSettingsWindow only stores the
        // window and calls Activate() once the ctor returns, so a throw here leaves an orphaned,
        // never-shown window and every later "Settings…" click leaks another.
        SafeInit(nameof(ConfigureWindowChrome), ConfigureWindowChrome);
        SafeInit(nameof(RefreshAllSections), RefreshAllSections);
        SafeInit(nameof(WireHaBrokerFieldEditHandlers), WireHaBrokerFieldEditHandlers);
        SafeInit(nameof(LoadAboutOnce), LoadAboutOnce);
        // Before the first layout pass: MeasureTallestPageExtent sizes the window to the tallest
        // page, and Smart Charge is much shorter once its sections are hidden on fixed-mode hardware.
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

    /// <summary>Populates the embedded About panel, at most once per window:
    /// <c>BrandAboutControl.SetInfo</c> appends credit rows and adds a repo-button handler with no
    /// clear or unsubscribe, so a second call duplicates the credits and makes one "GitHub" click
    /// open two tabs.</summary>
    private void LoadAboutOnce()
    {
        if (_aboutLoaded) return;
        _aboutLoaded = true;   // before the call: a SetInfo that threw part-way has already appended

        AboutCard.MaxWidth = AboutContent.ContentWidthDip;
        AboutInline.SetInfo(AboutContent.Build());
    }

    /// <summary>Runs one constructor step, logging any failure rather than letting it escape the ctor.</summary>
    private static void SafeInit(string step, Action body)
    {
        try { body(); }
        catch (Exception ex) { AppLog.Error($"SettingsWindow ctor step '{step}'", ex); }
    }

    /// <summary>Re-reads every section's controls from live settings. Also called when an
    /// already-open window is re-activated, so a change made behind its back (a settings reload, an
    /// out-of-band edit to settings.json) shows up. Any un-applied Home Assistant broker edit is
    /// discarded by the re-sync.</summary>
    internal void RefreshAllSections()
    {
        LoadGeneral();
        LoadSmartCharge();
        LoadNotifications();
        LoadNetwork();
        LoadKeepAwake();
        LoadHomeAssistant();
    }

    private void ConfigureWindowChrome()
    {
        var rect = ComputeInitialRect();
        // Guarded: a placement failure must never stop the window from showing.
        try { AppWindow.MoveAndResize(rect); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.MoveAndResize", ex); }

        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);   // match the Mica BaseAlt backdrop

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

    /// <summary>Grows the window so the tallest page fits without a scrollbar, then re-clamps it to
    /// the work area. The extra height comes from the ScrollViewer's own overflow rather than a sum
    /// of padding, header and title bar, so it cannot drift when the chrome changes.</summary>
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

    /// <summary>Height in DIPs the content would take on its longest page. The panels are
    /// overlapping siblings with the inactive ones Collapsed, so measuring as-is only sizes
    /// whichever page is open — making them all visible makes the Grid report their max. Visibility
    /// is then restored.</summary>
    private double MeasureTallestPageExtent()
    {
        FrameworkElement[] panels =
            [GeneralPanel, SmartChargePanel, KeepAwakePanel, LidClosePanel, NotificationsPanel, HomeAssistantPanel, AboutPanel];

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

    /// <summary>The opening rect in physical pixels: the saved size and position clamped onto a
    /// connected monitor, else a default centred on the monitor under the cursor. Both paths use
    /// the native MonitorFromPoint route, not DisplayArea.FindAll, which faults on some
    /// multi-monitor setups.</summary>
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

        return NativeMethods.CentreRectOnCursorMonitor(DefaultWidth, DefaultHeight);
    }

    /// <summary>Persists the window's final rect. WinUIEx's <c>PersistenceId</c> is unusable here
    /// — it stores through <c>Windows.Storage.ApplicationData</c>, which an unpackaged app does
    /// not have.</summary>
    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;

        var pos  = AppWindow.Position;
        var size = AppWindow.Size;

        // Clamp before storing, never after reading: a rect saved on a monitor that is later gone
        // is what puts the window off-screen on the next open.
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

    // Set in OnClosed. Background callbacks started before the close still marshal back, and
    // touching a destroyed window's XAML members throws, so RunOnUi drops them instead.
    private bool _closed;

    /// <summary>Marshals <paramref name="action"/> onto this window's UI thread. An unhandled
    /// exception inside a raw <see cref="DispatcherQueue"/> callback is a stowed exception that
    /// tears the whole process down, so every callback that can run off a background task goes
    /// through here.</summary>
    private void RunOnUi(Action action)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;   // window already destroyed — a stale callback has nothing to update
                try { action(); }
                catch (Exception ex) { AppLog.Error("SettingsWindow.RunOnUi", ex); }
            });
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.RunOnUi enqueue", ex); }
    }

    /// <summary>Raises the <see cref="_updating"/> guard around a batch of programmatic control
    /// assignments, lowering it in a <c>finally</c> — a hand-written pair leaves the flag stuck
    /// true if an assignment throws, silently disabling every later commit in the window.</summary>
    private void WithUpdatingSuppressed(Action apply)
    {
        _updating = true;
        try { apply(); }
        finally { _updating = false; }
    }

    private void StopAllPresetDebounceTimers()
    {
        foreach (var t in _presetDebounceTimers) t.Stop();
        _presetDebounceTimers.Clear();
    }

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
        LidClosePanel.Visibility      = tag == "LidClose"       ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPanel.Visibility = tag == "Notifications"  ? Visibility.Visible : Visibility.Collapsed;
        HomeAssistantPanel.Visibility = tag == "HomeAssistant"  ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility         = tag == "About"          ? Visibility.Visible : Visibility.Collapsed;

        // Refreshed on open rather than on a timer: cheap, and it picks up anything that changed
        // while the window sat on a different tab.
        if (tag == "SmartCharge")
        {
            ApplyThresholdCapabilityToSmartChargePage();
            RefreshCurrentNetworkText();
        }

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

    /// <summary>Shows the preset machinery, the vendor's fixed modes, or a plain explanation —
    /// whichever <see cref="ThresholdCapabilityPolicy.Classify"/> says this hardware warrants.
    /// Presets are only start/stop percentages, so hardware with no numeric threshold hides the
    /// editors rather than offering profiles that cannot differ from each other.</summary>
    private void ApplyThresholdCapabilityToSmartChargePage()
    {
        // Read once: the state decides the surface and supplies the cap figure below.
        var state   = ChargeThresholdService.Read();
        var surface = ThresholdCapabilityPolicy.Classify(state, ChargeThresholdService.SupportsNumericThresholds);

        NumericThresholdSettings.Visibility = surface == SmartChargeSurface.Numeric    ? Visibility.Visible : Visibility.Collapsed;
        FixedModeSettings.Visibility        = surface == SmartChargeSurface.FixedModes ? Visibility.Visible : Visibility.Collapsed;
        NoThresholdInterfaceText.Visibility = surface == SmartChargeSurface.Hidden     ? Visibility.Visible : Visibility.Collapsed;

        // Reuses the read above rather than costing a second vendor RPC.
        RefreshPresetActivationStates(state);

        if (surface != SmartChargeSurface.FixedModes) return;

        BuildChargeModeRadios();

        // A read-only BIOS setting is readable but refuses writes, so the radios would fail
        // silently on click.
        ChargeModeRadios.IsEnabled = state!.Capable;

        // Read the cap back from the device rather than hardcoding it, so this figure matches what
        // the dashboard and the hardware report.
        string cap = state is { Enabled: true, Stop: > 0 } ? $"about {state.Stop} %" : "a fixed level";

        FixedModeText.Text =
            $"This laptop's firmware offers fixed modes ({cap} of design capacity when limited) rather "
            + "than an adjustable range, so presets and network profiles do not apply and are hidden.\n\n"
            + "Windows still reports 100 % while a limit is active — this hardware lowers the "
            + "battery's reported full-charge capacity instead of stopping the charge early. "
            + "Changes take effect after a restart."
            + (state.Capable
                ? string.Empty
                : "\n\nThis setting is locked by the BIOS on this machine, so ChargeKeeper can show "
                  + "the current mode but not change it.");
    }

    /// <summary>Populates the mode radios from the active vendor and selects what the firmware reports.</summary>
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

            // A null id — firmware reporting a mode this build doesn't list — leaves every button
            // unselected rather than highlighting a wrong one.
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

    // Guards SelectionChanged against the programmatic selection made while populating the list,
    // which would otherwise write the mode back to the firmware every time the window opened.
    private bool _suppressChargeModeEvent;

    private void OnChargeModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChargeModeEvent) return;
        if (ChargeModeRadios.SelectedItem is not RadioButton { Tag: string id }) return;

        if (!ChargeThresholdService.SetMode(id))
        {
            // Write refused. Snap the UI back to what the device reports rather than leaving a
            // selection that lies.
            AppLog.Info($"Charge mode write refused by firmware: {id}");
            BuildChargeModeRadios();
            return;
        }

        // Re-read rather than trusting the write: a successful write can still be overridden by the
        // firmware's own adaptive logic.
        BuildChargeModeRadios();
    }

    // Discrete settings are dropdowns of (label, value) pairs; the int lives in the ComboBoxItem's
    // Tag so the display string is never parsed back.

    private static readonly (string Label, int Value)[] StartupDelayPresets =
        [("None", 0), ("2 s", 2), ("5 s", 5), ("10 s", 10), ("20 s", 20), ("30 s", 30), ("60 s", 60)];
    private static readonly (string Label, int Value)[] DowntimeGapPresets =
        [("None", 0), ("1 min", 1), ("2 min", 2), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("60 min", 60)];
    private static readonly (string Label, int Value)[] LowBattPctPresets =
        [("5 %", 5), ("10 %", 10), ("15 %", 15), ("20 %", 20), ("25 %", 25), ("30 %", 30), ("40 %", 40), ("50 %", 50)];
    private static readonly (string Label, int Value)[] HighBattPctPresets =
        [("60 %", 60), ("70 %", 70), ("75 %", 75), ("80 %", 80), ("85 %", 85), ("90 %", 90), ("95 %", 95)];
    private static readonly (string Label, int Value)[] DrainPctPresets =
        [("1 %/h", 1), ("2 %/h", 2), ("3 %/h", 3), ("5 %/h", 5), ("10 %/h", 10)];
    // No "None": a zero lid delay would sleep the machine instantly through the very feature meant
    // to delay that. LidDelayPolicy clamps a hand-edited file the same way.
    private static readonly (string Label, int Value)[] LidDelayPresets =
        [("1 min", 1), ("2 min", 2), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("60 min", 60), ("120 min", 120)];

    /// <summary>Populates a preset-picker and selects the item matching <paramref name="current"/>.
    /// A stored value that is not one of the presets becomes a custom entry rather than being
    /// overwritten. Call inside <see cref="WithUpdatingSuppressed"/> so populating it doesn't fire
    /// a commit.</summary>
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

        // Persisting alone is not enough: the in-memory window is only reloaded when a graph host
        // finds it empty, so open graphs would keep drawing the old span. A full CSV scan, hence
        // off the UI thread.
        Task.Run(() =>
        {
            BatteryHistoryService.LoadWindow(scale.ToTimeSpan());
            AppLog.Info($"Time-scale changed to {scale}.");
        });
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Reveal(SettingsService.FilePath);

    /// <summary>Re-reads settings.json — a manual edit, or a file synced in from another machine.</summary>
    private void OnReloadSettings(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Reload())
        {
            RefreshAllSections();
            _menu.ReconcileFromExternalChange();  // resync the tray toggles + icon
            NativeMethods.Info("Settings reloaded from disk.", AppName);
        }
        else
        {
            NativeMethods.Warn("Could not reload settings — the file is missing or invalid.", AppName);
        }
    }

    private void LoadNotifications()
    {
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            LowBattEnabledToggle.IsOn      = s.LowBatteryWarningEnabled;
            LoadPresetCombo(LowBattPctCombo, LowBattPctPresets, s.LowBatteryWarningPct, v => $"{v} %");
            LowBattPctCombo.IsEnabled      = s.LowBatteryWarningEnabled;
            HighBattEnabledToggle.IsOn     = s.HighBatteryWarningEnabled;
            LoadPresetCombo(HighBattPctCombo, HighBattPctPresets, s.HighBatteryWarningPct, v => $"{v} %");
            HighBattPctCombo.IsEnabled     = s.HighBatteryWarningEnabled;
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

    private void OnHighBattEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = HighBattEnabledToggle.IsOn;
        HighBattPctCombo.IsEnabled = on;
        SettingsService.Update(s => s.HighBatteryWarningEnabled = on);
    }

    private void OnHighBattPctChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(HighBattPctCombo, (s, v) => s.HighBatteryWarningPct = v);

    private void OnDrainEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = DrainEnabledToggle.IsOn;
        DrainPctPerHourCombo.IsEnabled = on;
        SettingsService.Update(s => s.DrainAnomalyWarningEnabled = on);
    }

    private void OnDrainPctPerHourChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(DrainPctPerHourCombo, (s, v) => s.DrainAnomalyPercentPerHour = v);

    private void LoadSmartCharge() => RebuildPresetRows();   // also (re)populates UnknownPresetCombo

    /// <summary>The Fluent "critical" brush for inline validation errors. Looked up via TryGetValue
    /// rather than the throwing indexer: degrading to the default colour beats a
    /// KeyNotFoundException.</summary>
    private static Microsoft.UI.Xaml.Media.Brush? CriticalBrush() =>
        Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush)
            ? brush as Microsoft.UI.Xaml.Media.Brush
            : null;

    /// <summary>Puts a TextBlock back after <see cref="CriticalBrush"/> was assigned to it.</summary>
    private static Microsoft.UI.Xaml.Media.Brush? SecondaryBrush() =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush)
            ? brush as Microsoft.UI.Xaml.Media.Brush
            : null;

    /// <summary>The placeholder for an empty list. One builder, so the four cannot drift apart.</summary>
    private static TextBlock EmptyListText(string text) => new()
    {
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity      = 0.7,
        Margin       = new Thickness(0, 4, 0, 4),
    };

    private void RebuildPresetRows()
    {
        // Every existing row is about to be discarded — stop its debounce timer first, or a drag
        // still settling can fire afterwards and commit a stale value against a detached row.
        StopAllPresetDebounceTimers();

        PresetsListPanel.Children.Clear();
        var presets = SettingsService.Current.Presets;

        PresetRows.ApplyActiveResources(PresetsListPanel);

        if (presets.Count == 0)
        {
            PresetsListPanel.Children.Add(EmptyListText("No presets yet. Add one below."));
        }
        else
        {
            foreach (var preset in presets)
                PresetsListPanel.Children.Add(BuildPresetRow(preset));
        }

        RefreshPresetActivationStates(ChargeThresholdService.Read());
        RefreshUnknownPresetCombo();
    }

    /// <summary>The preset the firmware's thresholds are, or null when none matches. The vendor read
    /// is taken first: it blocks, and the settings lock must not be held across it.</summary>
    private static string? ActivePresetInUse()
    {
        var state = ChargeThresholdService.Read();
        return SettingsService.Read(s => ActivePresetPolicy.Match(s.Presets, state))?.Name;
    }

    /// <summary>Marks the row whose thresholds the firmware is running and leaves the rest offering
    /// activation. Hidden entirely where the vendor refuses threshold writes — an affordance that
    /// cannot work is worse than none.</summary>
    private void RefreshPresetActivationStates(ChargeThresholdState? state)
    {
        string? activeName = SettingsService.Read(s => ActivePresetPolicy.Match(s.Presets, state))?.Name;

        PresetRows.RefreshActivation(
            PresetsListPanel, activeName, state is { Capable: true } ? Visibility.Visible : Visibility.Collapsed,
            "These thresholds are the ones in use.",
            "Applies these thresholds now.");
    }

    /// <summary>Applies a preset from its row, through the same composition every other trigger
    /// uses. Runs off the UI thread — the vendor write blocks.</summary>
    private void ActivatePreset(string name) => Task.Run(() =>
    {
        bool ok = false;
        try { ok = ChargeControlService.ApplyPresetByName(name); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.ActivatePreset", ex); }

        RunOnUi(() =>
        {
            if (!ok)
                NativeMethods.Warn("The device didn't accept this preset's thresholds.", AppName);

            RefreshPresetActivationStates(ChargeThresholdService.Read());
            _menu.ReconcileFromExternalChange();
        });
    });

    /// <summary>Builds one preset's editor row. The commit closures key off the preset's name
    /// rather than the passed object, so a concurrent <see cref="SettingsService.Reload"/> cannot
    /// leave them pointing at an orphaned instance — every commit re-looks-up by name.</summary>
    private SettingsExpander BuildPresetRow(ThresholdPreset preset)
    {
        string presetName = preset.Name;

        var nameBox = new TextBox { Text = preset.Name, MinWidth = 220 };

        // RangeSelector Minimum/Maximum must be set in code on this WinUI SDK build — the XAML
        // type-converter throws a XamlParseException. Maximum first, so Minimum never transiently
        // exceeds it. Stretch pairs with the card's ContentAlignment.Vertical below.
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

        var row = PresetRows.Build(
            ThresholdPreset.FormatLabel(preset.Name, preset.Start, preset.Stop), "", presetName,
            [
                new SettingsCard { Header = "Name",                              Content = nameBox },
                // ContentAlignment.Vertical drops the slider onto its own row, but a SettingsCard's
                // HorizontalContentAlignment still defaults to Right — the star column then collapses
                // and the slider shrinks to ~250 px. Stretch is what makes the Grid span the card.
                new SettingsCard
                {
                    Header                     = "Range (5-point minimum gap)",
                    ContentAlignment           = ContentAlignment.Vertical,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content                    = rangeRow,
                },
            ],
            CriticalBrush());

        row.Activate.Click += (_, _) => ActivatePreset(presetName);

        nameBox.LostFocus += (_, _) => CommitPresetRow(presetName, nameBox, range, row);
        nameBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) CommitPresetRow(presetName, nameBox, range, row); };

        // Debounced commit, same 700 ms as DashboardWindow's threshold sliders: validating on every
        // ValueChanged would flash an error for each intermediate sub-gap position during a drag.
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        // Stays tracked for the row's whole life: un-tracking here would let the next ValueChanged
        // re-start a timer StopAllPresetDebounceTimers can no longer reach.
        _presetDebounceTimers.Add(debounce);
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            CommitPresetRow(presetName, nameBox, range, row);
        };
        range.ValueChanged += (_, _) =>
        {
            startText.Text = $"{(int)range.RangeStart}%";
            stopText.Text  = $"{(int)range.RangeEnd}%";
            debounce.Stop();
            debounce.Start();
        };

        row.Delete.Click += (_, _) => DeletePreset(presetName);

        return row.Expander;
    }

    /// <summary>Validates and, if valid, saves a preset row's name and thresholds. Reject-on-save:
    /// an invalid edit shows an inline error and writes nothing, leaving the row as the user left
    /// it.</summary>
    private void CommitPresetRow(string originalName, TextBox nameBox, RangeSelector range,
        PresetRows.Parts row)
    {
        var errorText = row.Error;
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
        // Matched before the edit lands, while the stored preset still carries the values the device
        // would be running if it were the active one.
        bool wasActive = ActivePresetInUse() == originalName;

        SettingsService.Update(s =>
        {
            // Look up by originalName: the stored preset still carries its old name here, so
            // matching on newName would find nothing and drop the rename and the range edit both.
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

        // Only the active preset may touch the device — editing an inactive one must not
        // (reconcile contract, section C).
        if (wasActive)
            PushThresholdsToDevice(start, stop);

        if (renamed)
        {
            // The name every closure on this row keys off is now stale, and the network rows show
            // preset names too — rebuild both rather than re-keying a live row in place.
            RebuildPresetRows();
            RebuildNetworkRuleRows();
            _onDiscoveryChanged();   // HA's preset select carries the old name until discovery is republished
        }
        else
        {
            row.Header.Text = ThresholdPreset.FormatLabel(newName, start, stop);
        }

        _menu.ReconcileFromExternalChange();
    }

    /// <summary>Pushes thresholds to the device off the UI thread. A failure is reported by toast rather
    /// than a row's inline error, because by the time the write completes the row may be gone.</summary>
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
        bool wasActive = ActivePresetInUse() == name;
        var fallbackPreset = s0.Presets.FirstOrDefault(p => p.Name != name);
        string? fallback = fallbackPreset?.Name;

        SettingsService.Update(s => PresetCascade.Delete(s, name, fallback));

        // Every surface shows the fallback selected the moment this returns, so push its thresholds
        // too — otherwise the battery keeps running the deleted preset's values.
        if (wasActive && fallbackPreset is not null)
            PushThresholdsToDevice(fallbackPreset.Start, fallbackPreset.Stop);

        RebuildPresetRows();
        RebuildNetworkRuleRows();
        _onDiscoveryChanged();   // the deleted name must stop being offered in HA's preset select
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
        RebuildNetworkRuleRows();   // the new preset should be selectable from Network rows at once
        _onDiscoveryChanged();        // …and from HA's preset select, once discovery is republished
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

    /// <summary>Rebuilds both pages' renderings of the one
    /// <see cref="AppSettings.NetworkLocationRules"/> list, always together, so neither is left
    /// showing a rule the other just deleted or renamed.</summary>
    private void RebuildNetworkRuleRows()
    {
        RebuildSmartChargeNetworkRows();
        RebuildKeepAwakeNetworkRows();
    }

    /// <summary>One wording for both pages, so an empty list reads the same wherever it is met.</summary>
    private const string NoNetworkRulesText =
        "No network profiles yet. “Add profile for this network…” below adds one for the network currently connected.";

    private void RebuildSmartChargeNetworkRows()
    {
        NetworkRulesListPanel.Children.Clear();
        var rules = SettingsService.Current.NetworkLocationRules;

        if (rules.Count == 0)
        {
            NetworkRulesListPanel.Children.Add(EmptyListText(NoNetworkRulesText));
            return;
        }

        var presetNames = SettingsService.Current.Presets.Select(p => p.Name).ToList();
        // Both resolved ONCE per rebuild, not per row.
        var current  = CurrentLocation();
        var adapters = NetworkLocationService.EnumerateAdapters();
        for (int i = 0; i < rules.Count; i++)
        {
            int index = i;
            var presetCombo = new ComboBox { MinWidth = 220, PlaceholderText = "Choose a preset" };
            foreach (var n in presetNames) presetCombo.Items.Add(n);
            presetCombo.SelectedItem = presetNames.Contains(rules[i].PresetName) ? rules[i].PresetName : null;

            var expander = BuildNetworkRuleRow(
                index, rules[i], current, adapters, DescribeRulePresetSummary(rules[i]),
                new SettingsCard { Header = "Preset", Content = presetCombo },
                RebuildKeepAwakeNetworkRows);

            presetCombo.SelectionChanged += (_, _) =>
            {
                if (presetCombo.SelectedItem is string preset) CommitNetworkRulePreset(index, preset, expander);
            };
            NetworkRulesListPanel.Children.Add(expander);
        }
    }

    /// <summary>The rule's match key, plus a hint when the key no longer fits: its MAC belongs to a
    /// virtual adapter, or its subnet is the one we are on while its MAC is not. Stated only: nothing
    /// here rewrites a stored key.</summary>
    private static string DescribeMatchKey(
        NetworkLocationRule rule, NetworkLocation current, IReadOnlyList<BridgePeer> adapters)
    {
        string key = NetworkLocationService.DescribeMatchKey(rule.AdapterMac, rule.IpCidr);
        return NetworkLocationService.DescribeStaleKey(rule, current, adapters) is { } hint
            ? $"{key}\n{hint}"
            : key;
    }

    private static string DescribeRulePresetSummary(NetworkLocationRule rule) =>
        string.IsNullOrEmpty(rule.PresetName) ? "No preset assigned" : $"Applies “{rule.PresetName}”";

    private static string DescribeRuleKeepAwakeSummary(bool keepAwakeHere) =>
        keepAwakeHere ? "Keeps this computer awake" : "No keep-awake here";

    /// <summary>Builds one network rule's editor row for either page: both carry the name, the match
    /// key and Delete, and differ only in <paramref name="pageCard"/> and the summary line, so a rule
    /// reads the same on both. Keyed by list index — a rule has no identity of its own and two may
    /// share a name, which is unambiguous only because every commit path below rebuilds the lists.
    /// </summary>
    /// <param name="rebuildOtherPage">The other page's rebuild, run after a rename: both pages show
    /// the rule's name, and the row being edited keeps its focus rather than being rebuilt under it.</param>
    private SettingsExpander BuildNetworkRuleRow(
        int index, NetworkLocationRule rule, NetworkLocation current, IReadOnlyList<BridgePeer> adapters,
        string summary, SettingsCard pageCard, Action rebuildOtherPage)
    {
        var nameBox = new TextBox { Text = rule.Name, MinWidth = 220 };

        // Deleting is offered on both pages because there is one rule, not one per page — its
        // keep-awake side and its preset side go together.
        var deleteBtn = new Button { Content = "Delete profile" };
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };
        footer.Children.Add(deleteBtn);

        var expander = new SettingsExpander
        {
            Header      = rule.Name,
            Description = summary,
            ItemsSource = new List<SettingsCard>
            {
                new SettingsCard { Header = "Name",    Content = nameBox },
                new SettingsCard { Header = "Matches", Description = DescribeMatchKey(rule, current, adapters) },
                pageCard,
            },
            ItemsFooter = footer,
        };

        void CommitName() => CommitNetworkRuleName(index, nameBox.Text, expander, rebuildOtherPage);
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) CommitName(); };
        deleteBtn.Click   += (_, _) => DeleteNetworkRule(index);

        return expander;
    }

    private void CommitNetworkRuleName(int index, string? newNameRaw, SettingsExpander expander,
        Action rebuildOtherPage)
    {
        var rules = SettingsService.Current.NetworkLocationRules;
        if (index < 0 || index >= rules.Count) return;
        string newName = string.IsNullOrWhiteSpace(newNameRaw) ? rules[index].Name : newNameRaw!.Trim();

        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules[index].Name = newName;
        });
        expander.Header = newName;
        rebuildOtherPage();
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

        // Apply whatever profile now wins for the network we are on, so an edit to the active
        // network's rule takes effect immediately.
        ApplyWinningProfile(CurrentLocation());
    }

    // LastKnown is the cheap cached value; fall back to a live read only before it has resolved.
    private static NetworkLocation CurrentLocation()
    {
        var loc = NetworkLocationService.LastKnown;
        return loc.IsEmpty ? NetworkLocationService.DetectCurrent() : loc;
    }

    /// <summary>Applies the preset of whatever rule wins for <paramref name="location"/>, resolved
    /// through <see cref="AppSettings.FindNetworkRule"/> exactly as the tray's auto-apply does —
    /// the same resolution is what stops an immediate apply from being reverted by the next network
    /// change.</summary>
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
        // Both pages name the current network from the rule that matches it.
        RefreshCurrentNetworkText();
        RefreshKeepAwakeCurrentNetworkText();

        // Deleting the winning rule hands the current network to a later (or no) rule — apply
        // whatever wins now, so the device stops running the deleted rule's preset and any hold the
        // rule was keeping is released.
        ApplyWinningProfile(CurrentLocation());
        ReconcileKeepAwakeForCurrentNetwork();
    }

    /// <summary>Fingerprints the current network, asks for a name and appends a rule for it.
    /// Returns the detected location, or null when nothing was detected or the user cancelled.
    /// Shared by both pages' "Add … for this network…" buttons, which differ only in their tail.</summary>
    private async Task<NetworkLocation?> AddNetworkRuleAsync(bool keepAwakeHere)
    {
        var location = NetworkLocationService.DetectCurrent();
        if (location.IsEmpty)
        {
            NativeMethods.Warn("No network detected right now — connect to a network first.", AppName);
            return null;
        }

        string suggested = location.DisplayHint ?? (location.IsWired ? "Wired network" : "Wireless network");
        string? name = await new NameLocationWindow(
            suggested, NetworkLocationService.DescribeMatchKey(location.AdapterMac, location.IpCidr)).ShowAsync();
        if (name is null) return null;   // cancelled

        var s0 = SettingsService.Current;
        // The preset in use is the obvious default for a new rule; the first one when none is.
        string defaultPreset = ActivePresetInUse() ?? s0.Presets.FirstOrDefault()?.Name ?? "";

        SettingsService.Update(s =>
        {
            s.NetworkLocationRules.Add(new NetworkLocationRule
            {
                Name          = name,
                AdapterMac    = location.AdapterMac,
                IpCidr        = location.IpCidr,
                PresetName    = defaultPreset,
                KeepAwakeHere = keepAwakeHere,
            });
            // Rules are inert with profiles off, so configuring a location implies wanting them on.
            s.NetworkProfilesEnabled = true;
        });

        WithUpdatingSuppressed(() => NetworkEnabledToggle.IsOn = true);
        RebuildNetworkRuleRows();   // rebuilds both pages' renderings of the rule list
        return location;
    }

    private async void OnAddNetworkRule(object sender, RoutedEventArgs e)
    {
        // async void: an escaping exception tears the process down rather than surfacing, and
        // NameLocationWindow's ctor does monitor work that faults on multi-monitor.
        try
        {
            if (await AddNetworkRuleAsync(keepAwakeHere: false) is not { } location) return;
            RefreshCurrentNetworkText();

            // Usually the rule just added, unless an earlier one shadows it. Same first-match
            // resolution the tray uses, so this write agrees with the next reconcile.
            ApplyWinningProfile(location);
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnAddNetworkRule", ex); }
    }

    // Every span on the Keep Awake page is typed and read by KeepAwakeInputParser — no TimePicker,
    // no spinner. Fast entry is the feature.

    // Ticks the remaining-time line while the page is on screen. 30 s rather than 1 min: the line
    // is minute-resolution, so a minute-length tick can show a value a whole minute stale.
    private readonly DispatcherTimer _keepAwakeTicker =
        new() { Interval = TimeSpan.FromSeconds(30) };

    /// <summary>Subscribes the page to what changes keep-awake behind its back — an expiry, the
    /// tray toggle, a network arrival. Unsubscribed in <see cref="OnClosed"/>.</summary>
    private void WireKeepAwakeHandlers()
    {
        KeepAwakeService.StateChanged += OnKeepAwakeStateChanged;
        _keepAwakeTicker.Tick += (_, _) => RefreshKeepAwakeState();

        // Echo the parser's reading as the user types, so "1h30" is confirmed as 1 h 30 m before
        // Start is pressed rather than after the session is running.
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
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            KeepAwakeDisplayToggle.IsOn = s.KeepAwakeDisplayOn;
            LidDelayToggle.IsOn         = s.LidDelayEnabled;
            LoadPresetCombo(LidDelayMinutesCombo, LidDelayPresets, s.LidDelayMinutes, v => $"{v} min");
            LidLockToggle.IsOn          = s.LidDelayLockOnClose;
            LidLockToggle.IsEnabled     = s.LidDelayEnabled;
        });
        RefreshKeepAwakeState();
        RefreshKeepAwakeCustomEcho();
        RebuildKeepAwakeChips();
        RebuildKeepAwakePresetRows();
        RefreshKeepAwakeCurrentNetworkText();
        // The rule rows come from LoadNetwork() → RebuildNetworkRuleRows(), which rebuilds both
        // pages' renderings of the shared list.
    }

    private void RefreshKeepAwakeState()
    {
        var session = KeepAwakeService.Current;
        WithUpdatingSuppressed(() => KeepAwakeToggle.IsOn = session is not null);
        KeepAwakeRemainingText.Text = session is null
            ? "Not holding this computer awake."
            : KeepAwakePolicy.DescribeRemaining(DateTimeOffset.Now, session);

        // Covers every way a session can start or end, including expiry and the dashboard's chips.
        RefreshKeepAwakeActivationStates();
    }

    private void OnKeepAwakeToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        // Through KeepAwakeFeature, the same entry point the tray toggle uses, so "on with no span
        // picked" cannot mean two different things on the two surfaces.
        new KeepAwakeFeature().SetEnabled(KeepAwakeToggle.IsOn);
        RefreshKeepAwakeState();
    }

    private void OnKeepAwakeDisplayToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = KeepAwakeDisplayToggle.IsOn;
        // Takes effect on the next Activate — KeepAwakeService re-applies the OS flags each time —
        // which is why the card says so rather than silently not touching a running session.
        SettingsService.Update(s => s.KeepAwakeDisplayOn = on);
    }

    private void OnLidDelayToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        // LidDelayService owns the setting and the power-scheme write together, so the two cannot
        // drift. Only enabling can fail visibly; the toggle then goes back rather than showing an
        // on state the machine will not honour.
        bool wanted = LidDelayToggle.IsOn;
        if (!LidDelayService.SetEnabled(wanted) && wanted)
            WithUpdatingSuppressed(() => LidDelayToggle.IsOn = false);

        // Read back from the toggle rather than from "wanted": an enable that was refused above has
        // already put it back off, and the lock switch follows what the feature actually is.
        LidLockToggle.IsEnabled = LidDelayToggle.IsOn;
    }

    private void OnLidDelayMinutesChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(LidDelayMinutesCombo, (s, v) => s.LidDelayMinutes = v);

    private void OnLidLockToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = LidLockToggle.IsOn;
        SettingsService.Update(s => s.LidDelayLockOnClose = on);
    }

    // Three renderings of the same span, deliberately distinct: full words for display, the parser
    // echo that confirms how typed text was read, and the editable form that must round-trip back
    // through KeepAwakeInputParser. Remaining time is none of these — see DescribeRemaining.

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

    /// <summary>The span as text KeepAwakeInputParser can read back — what an editable "Expires"
    /// box is seeded with. The other kinds return empty, so the box invites a value rather than
    /// showing an uneditable one.</summary>
    private static string ToEditableSpan(KeepAwakeRequest r) => r switch
    {
        { Kind: KeepAwakeKind.UntilTime, Until: not null }                       => KeepAwakePolicy.ShortLabel(r),
        { Kind: KeepAwakeKind.Duration, Duration: { } d } when d > TimeSpan.Zero => KeepAwakePolicy.ShortLabel(r),
        _                                                                       => "",
    };

    private void RebuildKeepAwakeChips()
    {
        KeepAwakeChipsPanel.Children.Clear();
        foreach (var preset in SettingsService.Current.KeepAwakePresets)
        {
            var captured = preset;
            var chip = new Button { Content = DescribePreset(captured) };
            chip.Click += (_, _) => ActivateKeepAwakePreset(captured);
            KeepAwakeChipsPanel.Children.Add(chip);
        }
    }

    private void RefreshKeepAwakeCustomEcho()
    {
        // Typing is not an error — a half-typed "1h3" must not flash red. Only Start/Enter raises
        // the inline error, which is the point the input has to be usable.
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

    private static void ShowInlineError(TextBlock target, string message)
    {
        target.Text       = message;
        target.Foreground = CriticalBrush();
        target.Visibility = Visibility.Visible;
    }

    // Keep-awake preset rows are keyed by list index, same reasoning as the network rule rows: a
    // KeepAwakeRequest is a value with no identity of its own and two presets may be identical.

    private void RebuildKeepAwakePresetRows()
    {
        KeepAwakePresetsListPanel.Children.Clear();
        var presets = SettingsService.Current.KeepAwakePresets;

        PresetRows.ApplyActiveResources(KeepAwakePresetsListPanel);

        if (presets.Count == 0)
        {
            KeepAwakePresetsListPanel.Children.Add(EmptyListText("No presets yet. Add one below."));
            return;
        }

        for (int i = 0; i < presets.Count; i++)
            KeepAwakePresetsListPanel.Children.Add(BuildKeepAwakePresetRow(i, presets[i]));

        RefreshKeepAwakeActivationStates();
    }

    /// <summary>Marks the row whose preset started the running session. Always visible: no hardware
    /// can refuse a keep-awake hold the way fixed-mode firmware refuses thresholds.</summary>
    private void RefreshKeepAwakeActivationStates()
    {
        int active = ActiveKeepAwakePresetPolicy.MatchIndex(
            SettingsService.Current.KeepAwakePresets, KeepAwakeService.Current);

        PresetRows.RefreshActivation(
            KeepAwakePresetsListPanel, active >= 0 ? active : null, Visibility.Visible,
            "A session from this preset is running.",
            "Starts a session from this preset now.");
    }

    /// <summary>Starts a session from a saved preset, re-read by position so an edit committed since
    /// the row was built is the span that starts. Goes through
    /// <see cref="KeepAwakeService.Activate"/>, the one start path every surface uses.</summary>
    private void ActivateKeepAwakePresetAt(int index)
    {
        var presets = SettingsService.Current.KeepAwakePresets;
        if (index < 0 || index >= presets.Count) return;
        ActivateKeepAwakePreset(presets[index]);
    }

    private void ActivateKeepAwakePreset(KeepAwakeRequest preset)
    {
        KeepAwakeService.Activate(preset);
        RefreshKeepAwakeState();
    }

    /// <summary>One keep-awake preset's editor row — a name and a single "Expires" box, because
    /// typing <c>3h</c> or <c>17:00</c> defines the kind and the value together, and a separate
    /// kind picker would only let the two disagree.</summary>
    private SettingsExpander BuildKeepAwakePresetRow(int index, KeepAwakeRequest preset)
    {
        var nameBox    = new TextBox { Text = preset.Name ?? "", MinWidth = 220, PlaceholderText = DescribeSpan(preset) };
        var expiresBox = new TextBox { Text = ToEditableSpan(preset), MinWidth = 220, PlaceholderText = "3h, 90m or 17:00" };

        // Tagged by position, not by name: a keep-awake preset need not have one.
        var row = PresetRows.Build(
            DescribePreset(preset), DescribePresetSubtitle(preset), index,
            [
                new SettingsCard { Header = "Name",    Description = "Optional — the span is shown when this is blank.", Content = nameBox },
                new SettingsCard { Header = "Expires", Description = "A duration (3h, 90m, 1h30) or a clock time (17:00).", Content = expiresBox },
            ],
            CriticalBrush());

        row.Activate.Click += (_, _) => ActivateKeepAwakePresetAt(index);

        void Commit() => CommitKeepAwakePresetRow(index, nameBox, expiresBox, row);
        nameBox.LostFocus    += (_, _) => Commit();
        nameBox.KeyDown      += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        expiresBox.LostFocus += (_, _) => Commit();
        expiresBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };

        row.Delete.Click += (_, _) => DeleteKeepAwakePreset(index);

        return row.Expander;
    }

    /// <summary>Validates and saves one preset row, same reject-on-save contract as the threshold
    /// presets. A blank "Expires" keeps the stored span, so clearing the field cannot destroy a
    /// preset.</summary>
    private void CommitKeepAwakePresetRow(int index, TextBox nameBox, TextBox expiresBox,
        PresetRows.Parts row)
    {
        var presets = SettingsService.Current.KeepAwakePresets;
        if (index < 0 || index >= presets.Count) return;

        string expires = expiresBox.Text?.Trim() ?? "";
        KeepAwakeRequest span = presets[index];
        if (expires.Length > 0)
        {
            if (!KeepAwakeInputParser.TryParse(expires, out var parsed))
            {
                ShowInlineError(row.Error, "Enter a duration like 3h, 90m or 1h30, or a clock time like 17:00.");
                return;
            }
            span = parsed;
        }
        row.Error.Visibility = Visibility.Collapsed;

        string? name = nameBox.Text?.Trim() is { Length: > 0 } n ? n : null;
        var updated = span with { Name = name };

        SettingsService.Update(s =>
        {
            if (index < s.KeepAwakePresets.Count) s.KeepAwakePresets[index] = updated;
        });

        row.Header.Text          = DescribePreset(updated);
        row.Expander.Description = DescribePresetSubtitle(updated);
        expiresBox.Text          = ToEditableSpan(updated);   // normalises "1h30m" to "1h30"
        nameBox.PlaceholderText  = DescribeSpan(updated);
        RebuildKeepAwakeChips();   // the chip row shows these same presets

        // An edited preset no longer matches the session it started, so the marker moves off it.
        RefreshKeepAwakeActivationStates();
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
        // An hour is the least surprising starting figure; the point is that the row exists and is
        // editable.
        SettingsService.Update(s => s.KeepAwakePresets.Add(
            new KeepAwakeRequest(KeepAwakeKind.Duration, TimeSpan.FromHours(1), null)));
        RebuildKeepAwakePresetRows();
        RebuildKeepAwakeChips();
    }

    // The keep-awake facet of the shared NetworkLocationRules list. The Smart Charge page edits the
    // preset facet of the same rules; neither page owns the list.

    private void RefreshKeepAwakeCurrentNetworkText() =>
        KeepAwakeCurrentNetworkText.Text = NetworkLocationService.DescribeCurrentLocation();

    private void RebuildKeepAwakeNetworkRows()
    {
        KeepAwakeNetworkRulesListPanel.Children.Clear();
        var rules = SettingsService.Current.NetworkLocationRules;

        if (rules.Count == 0)
        {
            KeepAwakeNetworkRulesListPanel.Children.Add(EmptyListText(NoNetworkRulesText));
            return;
        }

        // Both resolved ONCE per rebuild, not per row.
        var current  = CurrentLocation();
        var adapters = NetworkLocationService.EnumerateAdapters();
        for (int i = 0; i < rules.Count; i++)
        {
            int index = i;
            var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = rules[i].KeepAwakeHere };

            var expander = BuildNetworkRuleRow(
                index, rules[i], current, adapters, DescribeRuleKeepAwakeSummary(rules[i].KeepAwakeHere),
                new SettingsCard { Header = "Keep awake here", Content = toggle },
                RebuildSmartChargeNetworkRows);

            // Attached after the initial IsOn, so seeding the switch cannot commit anything.
            toggle.Toggled += (_, _) =>
            {
                if (_updating) return;
                CommitKeepAwakeHere(index, toggle.IsOn);
                expander.Description = DescribeRuleKeepAwakeSummary(toggle.IsOn);
            };
            KeepAwakeNetworkRulesListPanel.Children.Add(expander);
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

    /// <summary>Applies the keep-awake facet of the rule that wins for the network we are on now.
    /// Without it, ticking "keep awake here" does nothing until you leave and come back, since the
    /// service only reacts to a location change. Never overrides a hand-started session.</summary>
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

    /// <summary>The Smart Charge page's add flow with the keep-awake facet filled in instead. No
    /// <c>ApplyWinningProfile</c>: the charge preset is the other page's facet, and fixed-mode
    /// hardware may have no presets to apply at all.</summary>
    private async void OnAddKeepAwakeNetworkRule(object sender, RoutedEventArgs e)
    {
        // async void — guarded whole, see OnAddNetworkRule.
        try
        {
            if (await AddNetworkRuleAsync(keepAwakeHere: true) is null) return;
            RefreshKeepAwakeCurrentNetworkText();
            RefreshCurrentNetworkText();
            ReconcileKeepAwakeForCurrentNetwork();
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnAddKeepAwakeNetworkRule", ex); }
    }

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
            // Blank means "use the machine-derived default", so show it as a placeholder rather
            // than pre-filling it — an untouched field must keep meaning "default".
            HaDeviceNameBox.PlaceholderText = DefaultDeviceName();
            HaDeviceNameBox.Text            = s.MqttDeviceName;

            MqttBatteryStatusToggle.IsOn  = s.MqttPublishBatteryStatus;
            MqttSmartChargeToggle.IsOn    = s.MqttPublishSmartCharge;
            MqttKeepAwakeToggle.IsOn      = s.MqttPublishKeepAwake;
            MqttLidCloseToggle.IsOn       = s.MqttPublishLidClose;
            MqttNotificationsToggle.IsOn  = s.MqttPublishNotifications;
            MqttNetworkToggle.IsOn        = s.MqttPublishNetwork;
            MqttAppDiagnosticsToggle.IsOn = s.MqttPublishAppDiagnostics;
        });
        RefreshHaNodeIdText();
        // This re-sync discards any un-applied broker edit, so a leftover "Applied." must not
        // linger claiming stale values are live.
        HaAppliedText.Visibility = Visibility.Collapsed;
        HaTestResultText.Visibility = Visibility.Collapsed;   // the tested values are gone with it
        RefreshHaBrokerStatusText();
        RefreshHaActivityTexts();
    }

    /// <summary>Commits every group toggle at once and re-announces. Unlike the broker fields these
    /// apply immediately: the change is a publish, not a reconnect, so there is nothing to batch.</summary>
    private void OnMqttCategoryToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        SettingsService.Update(s =>
        {
            s.MqttPublishBatteryStatus  = MqttBatteryStatusToggle.IsOn;
            s.MqttPublishSmartCharge    = MqttSmartChargeToggle.IsOn;
            s.MqttPublishKeepAwake      = MqttKeepAwakeToggle.IsOn;
            s.MqttPublishLidClose       = MqttLidCloseToggle.IsOn;
            s.MqttPublishNotifications  = MqttNotificationsToggle.IsOn;
            s.MqttPublishNetwork        = MqttNetworkToggle.IsOn;
            s.MqttPublishAppDiagnostics = MqttAppDiagnosticsToggle.IsOn;
        });
        _onDiscoveryChanged();
    }

    /// <summary>Hides "Applied." and the test result the moment any broker field is edited — those
    /// edits are not live until the next Apply, so either label would vouch for values since
    /// retyped.</summary>
    private void WireHaBrokerFieldEditHandlers()
    {
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

    // HomeAssistantEnabled is not one of the batched broker fields — it applies immediately, same
    // as every other toggle here.
    private void OnHaEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = HaEnabledToggle.IsOn;
        SettingsService.Update(s => s.HomeAssistantEnabled = on);
        _onHomeAssistantChanged();   // exactly one reconnect attempt for this toggle flip
    }

    /// <summary>Commits all seven batched broker fields at once, so <c>HomeAssistantService</c>
    /// reconnects per Apply click rather than per keystroke. The device ID is not in the batch —
    /// renaming every entity must not be a side effect of editing a host
    /// (<see cref="OnChangeNodeIdClicked"/>).</summary>
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

    // The in-flight connection test, or null when none is running — both the re-entrancy guard and
    // the handle OnClosed cancels through.
    private CancellationTokenSource? _haProbeCts;

    /// <summary>Tests the staged broker values — whatever is in the boxes, applied or not, since
    /// the point of the button is to check before committing. Awaited directly rather than pushed
    /// to <c>Task.Run</c>: the probe is I/O all the way down.</summary>
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
            bool cancelled = cts.IsCancellationRequested;   // read BEFORE disposing the source
            _haProbeCts = null;
            cts.Dispose();
            if (!cancelled) SetHaTestRunning(false);
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

    /// <summary>Re-reads the two live-connection facts on page-show and after an Apply, not on a timer.</summary>
    private void RefreshHaActivityTexts()
    {
        var now = DateTime.UtcNow;
        HaLastPublishText.Text = MqttStatusFormatter.DescribeLastPublish(MqttActivity.LastPublishUtc, now);
        HaLastCommandText.Text = MqttStatusFormatter.DescribeLastCommand(MqttActivity.LastCommand, now);
    }

    /// <summary>The device name used when <see cref="AppSettings.MqttDeviceName"/> is blank — the same
    /// expression <c>HomeAssistantService.ApplyAsync</c> falls back to.</summary>
    private static string DefaultDeviceName() => $"ChargeKeeper ({Environment.MachineName})";

    /// <summary>The id actually published under, override or machine-derived default.</summary>
    private static string EffectiveNodeId() =>
        HaDiscovery.EffectiveNodeId(SettingsService.Current.MqttNodeId, Environment.MachineName);

    private void RefreshHaNodeIdText() => HaNodeIdText.Text = EffectiveNodeId();

    /// <summary>The device ID's own confirmation dialog. The id is the <c>unique_id</c> stem, the
    /// device identifier and every topic segment, so changing it renames every entity in Home
    /// Assistant — hence the separate dialog, gated on a valid different id and an acknowledgement
    /// tick.</summary>
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
            // The id is sanitised to [a-z0-9_] before it reaches a topic, so echo what will be
            // published — otherwise "Office ThinkPad" silently becomes something else.
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
                Text = "Changing the ID renames every ChargeKeeper entity on the broker. "
                     + "Automations, dashboards and history that point at the old entities will stop "
                     + "working — they will not report an error, the entities simply will not be there "
                     + "any more.\n\n"
                     + "ChargeKeeper removes the old entities from the broker on confirmation. "
                     + "Their recorded history is not carried over to the new ones.\n\n"
                     + "An empty box restores the name derived from this machine.",
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

            // The primary button is the only gate, so re-derive it from scratch on every edit
            // rather than tracking a "was valid" flag that can go stale.
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

            // Store the sanitised form: the card above shows the effective id and the two must not
            // disagree. Blank stays blank — that is the "use the machine default" sentinel.
            string entered = (idBox.Text ?? "").Trim();
            string newId   = entered.Length == 0 ? "" : HaDiscovery.NormalizeNodeId(entered);

            SettingsService.Update(s => s.MqttNodeId = newId);
            // Same callback the broker Apply uses: HomeAssistantService evicts the old id's retained
            // topics before republishing discovery under the new one.
            _onHomeAssistantChanged();
            RefreshHaNodeIdText();
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnChangeNodeIdClicked", ex); }
    }

    // The About section hosts BrandAboutControl inline, populated in the ctor, so it needs no
    // handler of its own.
}
