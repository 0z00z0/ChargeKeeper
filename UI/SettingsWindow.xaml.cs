using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;
using ChargeKeeper.Features;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ZeroZero.Mqtt.WinUI;

namespace ChargeKeeper.UI;

/// <summary>The app's Settings window: a NavigationView with one panel per section, built from
/// <see cref="SettingsCard"/>/<see cref="SettingsExpander"/> rows. Every setting on these pages
/// commits as it is edited; the MQTT page is the shared module's own panel, which stages its broker
/// block behind an Apply of its own. Plain WinUI chrome, not
/// <see cref="ChargeKeeper.Helpers.WindowChrome.ApplyPopup"/> — that auto-dismisses on focus loss,
/// which would close the window mid-edit.</summary>
internal sealed partial class SettingsWindow : Window
{
    private const string AppName = AppInfo.Name;
    // First-open default in DIPs; the content otherwise lays out ~2580 px wide.
    private const int DefaultWidth  = 1200;
    private const int DefaultHeight = 750;

    private readonly TrayMenu _menu;

    // The MQTT publisher, or null when startup never reached it. The window supplies it to the shared
    // settings panel and calls it directly for the one thing the panel knows nothing about: a preset
    // renamed here changes the two preset selects' options, which are baked into the retained
    // discovery document.
    private readonly Services.MqttPublisher? _mqtt;

    // Suppresses the change handlers while LoadXxx() writes controls, so a programmatic assignment
    // can't queue a bogus commit. One shared flag is safe: each LoadXxx() runs synchronously.
    private bool _updating;

    // Every preset row's commit-debounce timer, tracked so one left running after its row is
    // discarded can be stopped before it fires against a detached row or a closed window.
    private readonly List<DispatcherTimer> _presetDebounceTimers = [];

    // The six saved-value lists, all on the one shared mechanism. Two of them are the same network
    // profiles rendered on both pages. Assigned in the constructor, before anything loads a page.
    private PresetRows.Section _thresholdList      = null!;
    private PresetRows.Section _networkProfileList = null!;
    private PresetRows.Section _keepAwakeProfileList = null!;
    private PresetRows.Section _keepAwakePresetList  = null!;
    private PresetRows.Section _lidDelayList         = null!;
    private PresetRows.Section _lidDischargeList     = null!;

    // What the threshold rows are marked against: the vendor read blocks, so it is taken once per
    // refresh rather than per row.
    private ChargeThresholdState? _thresholdState;

    // Resolved once per rebuild of the profile rows and read by every row: the adapter enumeration
    // is expensive, and both pages must mark the same profile.
    private NetworkLocation _profileRowLocation;
    private IReadOnlyList<BridgePeer> _profileRowAdapters = [];
    private IReadOnlyList<string> _profileRowPresets = [];
    private int _winningProfileRow = -1;

    public SettingsWindow(TrayMenu menu, Services.MqttPublisher? mqtt)
    {
        _menu = menu;
        _mqtt = mqtt;

        InitializeComponent();
        Title = "ChargeKeeper Settings";

        VersionText.Text = $"v{AppInfo.Version}";

        // Nothing below may throw out of the constructor: App.ShowSettingsWindow only stores the
        // window and calls Activate() once the ctor returns, so a throw here leaves an orphaned,
        // never-shown window and every later "Settings…" click leaks another.
        // Before anything loads a page: every list below is rebuilt through one of these.
        BuildSavedValueLists();

        SafeInit(nameof(ConfigureWindowChrome), ConfigureWindowChrome);
        SafeInit("AttachBrandMark", () => BrandMarkImage.Attach(BrandMark));
        // Before RefreshAllSections, which reloads the panel: an uninitialised one ignores a reload,
        // so the other order works only by accident.
        SafeInit(nameof(InitialiseMqttPanel), InitialiseMqttPanel);
        SafeInit(nameof(RefreshAllSections), RefreshAllSections);
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

        // After Closed, which owns the detach. Without this the "Current network" lines only ever
        // refresh on open, on a section switch and on a rule edit, so a dock, an undock or a carrier
        // change leaves a network on screen that the service moved off long ago.
        NetworkLocationService.LocationChanged += OnNetworkLocationChanged;
    }

    // Raised off the UI thread by NetworkLocationService — marshal before touching anything.
    private void OnNetworkLocationChanged(NetworkLocation location) => RunOnUi(() =>
    {
        RefreshCurrentNetworkText();
        RefreshKeepAwakeCurrentNetworkText();
        // Which profile is in use has just moved, and so has every row's reading of the match key.
        RebuildNetworkRuleRows();
    });

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
        // Fixed, not MaxWidth: the button is far narrower than the card on its own, so only a fixed
        // width forces this row to occupy the same span the card does, for the centring above to land
        // under the card rather than under the button's own small footprint.
        CheckForUpdatesRow.Width = AboutContent.ContentWidthDip;
        // The tray menu owns the "What's new" window, so the card's button and the About window's
        // share one instance rather than each opening a copy.
        AboutInline.SetInfo(AboutContent.Build(_menu.ShowWhatsNew));
        _updateButton = new UpdateCheckButtonController(CheckForUpdatesButton, _menu);
    }

    private UpdateCheckButtonController? _updateButton;

    /// <summary>Runs one constructor step, logging any failure rather than letting it escape the ctor.</summary>
    private static void SafeInit(string step, Action body)
    {
        try { body(); }
        catch (Exception ex) { AppLog.Error($"SettingsWindow ctor step '{step}'", ex); }
    }

    /// <summary>Re-reads every section's controls from live settings. Also called when an
    /// already-open window is re-activated, so a change made behind its back (a settings reload, an
    /// out-of-band edit to settings.json) shows up. The MQTT panel is reloaded rather than reset, so
    /// a half-typed broker field survives a re-activation.</summary>
    internal void RefreshAllSections()
    {
        LoadGeneral();
        LoadSmartCharge();
        LoadNotifications();
        LoadNetwork();
        LoadKeepAwake();
        LoadScripts();
        LoadAppearance();
        LoadAppDiagnostics();
        // Keeps whatever is being typed in the broker block: a re-activation is not a reason to
        // throw away a half-entered host name.
        if (_mqtt is not null) MqttPanel.Reload();

        // Re-read the firmware charge mode on every re-activation, so a mode changed OUTSIDE this
        // app (HP's own utility, another tool, the tray) is reflected rather than showing whatever
        // was read when the window was first built. Issue #84.
        //
        // Only the SELECTION is refreshed, deliberately not the whole capability pass: which Smart
        // Charge layout applies is decided when the window is built and cannot change under the
        // user, and re-running it here would also re-do window-sizing work for no reason.
        // Guarded on the mode list being non-empty so this costs a numeric vendor nothing — on
        // Lenovo it would otherwise be a pointless vendor round-trip on every re-activation.
        if (ChargeThresholdService.AvailableModes.Count > 0) BuildChargeModeRadios();
    }

    private void ConfigureWindowChrome()
    {
        var rect = ComputeInitialRect(out _opensAtStoredRect);
        // Guarded: a placement failure must never stop the window from showing.
        try { AppWindow.MoveAndResize(rect); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.MoveAndResize", ex); }

        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);   // match the Mica BaseAlt backdrop

        // The content cannot be measured yet — SettingsCard is templated, and a control outside a
        // live visual tree reports no useful size. Grow to fit once it has laid out.
        ContentScroller.Loaded += OnContentScrollerLoaded;
    }

    private bool _fittedToContent;

    /// <summary>Whether the window opened at the size stored when it last closed. That size is the
    /// one the person left it at, so it is applied as it stands and never grown to fit a page.</summary>
    private bool _opensAtStoredRect;

    private void OnContentScrollerLoaded(object sender, RoutedEventArgs e)
    {
        ContentScroller.Loaded -= OnContentScrollerLoaded;
        if (_fittedToContent) return;
        _fittedToContent = true;
        try { ApplyMinimumSize(); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.ApplyMinimumSize", ex); }
        if (_opensAtStoredRect) return;
        try { FitWindowToContent(); }
        catch (Exception ex) { AppLog.Error("SettingsWindow.FitWindowToContent", ex); }
    }

    /// <summary>Stops the window being dragged below its navigation pane and its widest fixed
    /// control. Set here rather than in <see cref="ConfigureWindowChrome"/>: the rasterisation scale
    /// the DIP arithmetic has to be converted with is only readable once the tree has laid out.</summary>
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        int width  = WindowFit.MinimumWidthDip(Nav.OpenPaneLength,
                                               ContentScroller.Padding.Left + ContentScroller.Padding.Right);
        int height = WindowFit.MinimumHeightDip(Nav.MenuItems.Count,
                                                ContentScroller.Padding.Top + ContentScroller.Padding.Bottom);

        presenter.PreferredMinimumWidth  = WindowFit.ToPhysicalPixels(width,  scale);
        presenter.PreferredMinimumHeight = WindowFit.ToPhysicalPixels(height, scale);
        AppLog.Info($"SettingsWindow minimum size: {width}x{height} DIP at scale {scale} -> "
                  + $"{presenter.PreferredMinimumWidth}x{presenter.PreferredMinimumHeight} px.");
    }

    /// <summary>Grows a window opened with no stored size so the tallest page fits without a
    /// scrollbar, no taller than <see cref="WindowFit.FirstOpenHeightFraction"/> of the work area,
    /// then re-clamps it to the work area. The extra height comes from the ScrollViewer's own
    /// overflow rather than a sum of padding, header and title bar, so it cannot drift when the
    /// chrome changes.</summary>
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
        required = Math.Min(required, WindowFit.FirstOpenHeightCap(work.H));

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
            [GeneralPanel, AppearancePanel, SmartChargePanel, KeepAwakePanel, LidClosePanel, NotificationsPanel,
             ScriptsPanel, HomeAssistantPanel, AppDiagnosticsPanel, AboutPanel];

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
    private static RectInt32 ComputeInitialRect(out bool isStored)
    {
        var s = SettingsService.Current;
        if (s.SettingsWindowX is { } x && s.SettingsWindowY is { } y &&
            s.SettingsWindowWidth is { } w && s.SettingsWindowHeight is { } h &&
            w > 0 && h > 0)
        {
            isStored = true;
            var (cx, cy, cw, ch) = NativeMethods.ClampRectToNearestMonitor(x, y, w, h);
            return new RectInt32(cx, cy, cw, ch);
        }

        isStored = false;
        return NativeMethods.CentreRectOnCursorMonitor(DefaultWidth, DefaultHeight);
    }

    /// <summary>Persists the window's final rect. WinUIEx's <c>PersistenceId</c> is unusable here
    /// — it stores through <c>Windows.Storage.ApplicationData</c>, which an unpackaged app does
    /// not have.</summary>
    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        _updateButton?.Detach();

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

        // Before the timers stop: a script typed and not yet committed is minutes of work, and the
        // debounce would otherwise be stopped with the edit still in it.
        FlushScriptEdits();
        StopAllPresetDebounceTimers();

        // Static events, instance handlers: without these the closed window stays reachable from
        // the services for the process's life and keeps touching a torn-down UI tree.
        KeepAwakeService.StateChanged           -= OnKeepAwakeStateChanged;
        LidDelayService.StateChanged            -= OnLidDelayStateChanged;
        NetworkLocationService.LocationChanged  -= OnNetworkLocationChanged;
        _keepAwakeTicker.Stop();

        // An in-flight probe outlives the window by up to its budget; cancelling makes the
        // continuation bail before it touches a torn-down control.
        if (_mqtt is not null) MqttPanel.Cancel();
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
        ScriptsPanel.Visibility       = tag == "Scripts"        ? Visibility.Visible : Visibility.Collapsed;
        HomeAssistantPanel.Visibility = tag == "HomeAssistant"  ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility     = tag == "Appearance"     ? Visibility.Visible : Visibility.Collapsed;
        AppDiagnosticsPanel.Visibility = tag == "AppDiagnostics" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility         = tag == "About"          ? Visibility.Visible : Visibility.Collapsed;

        // The graph only paints while its page is on screen. Its own Visibility follows the page
        // because that is what stops its repaint: a collapsed parent panel does not reach it.
        PerformanceGraph.Visibility = tag == "AppDiagnostics" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "AppDiagnostics") PerformanceGraph.ApplySettings();

        // Refreshed on open rather than on a timer: cheap, and it picks up anything that changed
        // while the window sat on a different tab.
        if (tag == "SmartCharge")
        {
            ApplyThresholdCapabilityToSmartChargePage();
            RefreshCurrentNetworkText();
        }

        // Opening the page reads what is already known and probes nothing: a probe follows an
        // explicit action — a Broker setting edited, Test connection, or Apply — and never the mere
        // act of looking at the page.
        if (tag == "HomeAssistant" && _mqtt is not null)
            MqttPanel.Refresh();

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
    /// <summary>The offered ceilings. The band starts well above a machine at rest and stops short
    /// of where firmware protection acts, which is the last resort this exists to stay ahead of.</summary>
    private static readonly (string Label, int Value)[] LidThermalCeilingPresets =
        [("70 °C", 70), ("75 °C", 75), ("80 °C", 80), ("85 °C", 85), ("90 °C", 90)];

    private static readonly (string Label, int Value)[] DowntimeGapPresets =
        [("None", 0), ("1 min", 1), ("2 min", 2), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("60 min", 60)];
    private static readonly (string Label, int Value)[] LowBattPctPresets =
        [("5 %", 5), ("10 %", 10), ("15 %", 15), ("20 %", 20), ("25 %", 25), ("30 %", 30), ("40 %", 40), ("50 %", 50)];
    private static readonly (string Label, int Value)[] HighBattPctPresets =
        [("60 %", 60), ("70 %", 70), ("75 %", 75), ("80 %", 80), ("85 %", 85), ("90 %", 90), ("95 %", 95)];
    private static readonly (string Label, int Value)[] DrainPctPresets =
        [("1 %/h", 1), ("2 %/h", 2), ("3 %/h", 3), ("5 %/h", 5), ("10 %/h", 10)];

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
        });
    }

    /// <summary>Selects the item whose <c>Tag</c> is <paramref name="value"/>, falling back to the
    /// first — a stored value naming no item still has to leave the combo showing a selection, and
    /// the first item is the setting's own default.</summary>
    private static void SelectComboByTag(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem { Tag: string tag } && tag == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        combo.SelectedIndex = 0;
    }

    private void OnStartupDelayChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(StartupDelayCombo, (s, v) => s.StartupDelaySeconds = v);

    private void OnDowntimeGapChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(DowntimeGapCombo, (s, v) => s.DowntimeGapMinutes = v);

    private void OnIconModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || IconModeCombo.SelectedIndex < 0) return;
        var mode = (TrayIconMode)IconModeCombo.SelectedIndex;

        // Numeric % already puts the reading in the tray, so the second icon would be a duplicate.
        // Switched off as well as disabled: leaving it stored as on would bring the duplicate back
        // the moment another style was chosen, which is not what selecting Numeric % asked for.
        bool clearSecond = mode == TrayIconMode.Numeric;
        SettingsService.Update(s =>
        {
            s.IconMode = mode;
            if (clearSecond) s.ShowPercentageIcon = false;
        });

        WithUpdatingSuppressed(() =>
        {
            if (clearSecond) PercentageIconToggle.IsOn = false;
            ApplyPercentageIconAvailability(mode);
            ApplyDigitStyleAvailability(mode, PercentageIconToggle.IsOn);
        });

        _menu.ReconcileFromExternalChange();   // repaints the tray icon via the icon-mode callback
    }

    /// <summary>The check run each time the window is opened; its outcome shows on the About page's
    /// button alone. The tray menu owns the check, so Settings and the About window share it.</summary>
    internal void CheckForUpdatesAutomatically() => _updateButton?.CheckAutomatically();

    private void OnPromoteIconsToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = PromoteIconsToggle.IsOn;
        SettingsService.Update(s => s.PromoteTrayIcons = on);
        // The same path the style change takes; the tray applies or reverses the promotion there.
        _menu.ReconcileFromExternalChange();
    }

    // ── Appearance ──────────────────────────────────────────────────────────────────────────────
    // The page's Tray, Dashboard and Graph headings group controls for the screen only; each keeps
    // its own file section, including the tray style, the main-tray setting and the downtime gap
    // that sit here rather than on General — see SettingsFileShape's own remarks on why file section
    // and UI page are independent.

    private void LoadAppearance()
    {
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            IconModeCombo.SelectedIndex      = (int)s.IconMode;
            PromoteIconsToggle.IsOn          = s.PromoteTrayIcons;
            OneLineUntilItMattersToggle.IsOn = s.OneLineUntilItMatters;
            PercentageIconToggle.IsOn        = s.ShowPercentageIcon;
            HideGraphInDashboardToggle.IsOn  = s.HideGraphInDashboard;
            DigitStyleCombo.SelectedIndex    = (int)s.PercentageDigitStyle;
            ApplyPercentageIconAvailability(s.IconMode);
            ApplyDigitStyleAvailability(s.IconMode, s.ShowPercentageIcon);
            GraphScaleCombo.SelectedIndex    = (int)s.GraphTimeScale;
            SelectComboByTag(GraphLineColouringCombo, s.GraphLineColouring.ToString());
            GraphShadingToggle.IsOn          = s.GraphShadingEnabled;
            LoadPresetCombo(DowntimeGapCombo, DowntimeGapPresets, s.DowntimeGapMinutes, v => $"{v} min");
        });
    }

    private void OnOneLineUntilItMattersToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = OneLineUntilItMattersToggle.IsOn;
        SettingsService.Update(s => s.OneLineUntilItMatters = on);
    }

    private void OnHideGraphInDashboardToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = HideGraphInDashboardToggle.IsOn;
        SettingsService.Update(s => s.HideGraphInDashboard = on);
    }

    private void OnPercentageIconToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = PercentageIconToggle.IsOn;
        SettingsService.Update(s => s.ShowPercentageIcon = on);
        ApplyDigitStyleAvailability(SettingsService.Current.IconMode, on);
        _menu.ReconcileFromExternalChange();   // adds or removes the second icon on the next repaint
    }

    private void OnDigitStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DigitStyleCombo.SelectedIndex < 0) return;
        var style = (TrayDigitStyle)DigitStyleCombo.SelectedIndex;

        // The icon request carries the style, so the repaint the committed change starts draws the
        // new digits. The reconcile moves both tray menus' check marks; the second icon's menu has no
        // right-click refresh of its own.
        SettingsService.Update(s => s.PercentageDigitStyle = style);
        _menu.ReconcileFromExternalChange();
    }

    /// <summary>The digit style is shown whenever a tray icon draws digits: the Numeric % style, or
    /// the second percentage icon. Hidden rather than disabled: with no digits drawn it qualifies
    /// nothing, so there is nothing for a disabled row to explain.</summary>
    private void ApplyDigitStyleAvailability(TrayIconMode mode, bool percentageIconOn) =>
        DigitStyleCard.Visibility = AppSettings.TrayDrawsDigits(mode, percentageIconOn)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The second icon is offered only where it would show something the main icon does
    /// not. Disabling rather than ignoring is what makes the reason visible.</summary>
    private void ApplyPercentageIconAvailability(TrayIconMode mode)
    {
        bool available = mode != TrayIconMode.Numeric;
        PercentageIconToggle.IsEnabled = available;
        PercentageIconCard.Description = available
            ? "Adds a second tray icon showing the charge level as a number."
            : "The Numeric % style already shows the charge level as a number.";
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

    // Both graph hosts repaint on their own 5 s tick and read the settings fresh, so neither of the
    // two below reloads history or forces a render.

    private void OnGraphLineColouringChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || GraphLineColouringCombo.SelectedItem is not ComboBoxItem { Tag: string name }) return;
        // Enum.TryParse also accepts integer text, so IsDefined is what rejects a Tag that names no
        // member rather than saving a setting the graph would have to fall back from.
        if (!Enum.TryParse<GraphLineColouring>(name, out var mode) || !Enum.IsDefined(mode)) return;
        SettingsService.Update(s => s.GraphLineColouring = mode);
    }

    private void OnGraphShadingToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = GraphShadingToggle.IsOn;
        SettingsService.Update(s => s.GraphShadingEnabled = on);
    }

    // ── App diagnostics ─────────────────────────────────────────────────────────────────────────

    private void LoadAppDiagnostics()
    {
        var s = SettingsService.Current;
        WithUpdatingSuppressed(() =>
        {
            // Built from the rate ladder itself rather than listed in markup, so a step added to the
            // enum reaches the dropdown instead of silently going missing from it.
            if (PerformanceRateCombo.Items.Count == 0)
                foreach (var rate in PerformanceSampleRates.All)
                    PerformanceRateCombo.Items.Add(new ComboBoxItem { Content = rate.Label() });

            PerformanceGraphToggle.IsOn      = s.PerformanceGraphEnabled;
            PerformanceRateCombo.SelectedIndex = (int)PerformanceSampleRates.Normalise(s.PerformanceSampleRate);
        });
        PerformanceGraph.ApplySettings();
    }

    private void OnPerformanceGraphToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = PerformanceGraphToggle.IsOn;
        SettingsService.Update(s => s.PerformanceGraphEnabled = on);
        // The sampler itself is re-applied by App, which listens for the settings change; this only
        // brings the plot in line with what is now being collected.
        PerformanceGraph.ApplySettings();
    }

    private void OnPerformanceRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PerformanceRateCombo.SelectedIndex < 0) return;
        var rate = (PerformanceSampleRate)PerformanceRateCombo.SelectedIndex;
        SettingsService.Update(s => s.PerformanceSampleRate = rate);
        PerformanceGraph.ApplySettings();
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Reveal(SettingsService.FilePath);

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Open(SettingsService.FilePath);

    private void OnOpenAppLogClick(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Open(AppPaths.LogFile(AppLog.FileName));

    private void OnOpenPowerLogClick(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Open(AppPaths.LogFile(PowerLog.FileName));

    private void OnOpenPerformanceHistoryLogClick(object sender, RoutedEventArgs e)
        => ExplorerLauncher.Open(PerformanceHistoryService.FilePath);

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

            NotificationSoundCombo.Items.Clear();
            foreach (var (sound, label) in NotificationSounds.Choices)
                NotificationSoundCombo.Items.Add(new ComboBoxItem { Content = label, Tag = sound });
            NotificationSoundCombo.SelectedItem =
                NotificationSoundCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (NotificationSound)i.Tag! == s.NotificationSound)
                ?? NotificationSoundCombo.Items[0];
            NotificationSoundPreviewButton.IsEnabled = NotificationSounds.HasOwnRecording(s.NotificationSound);

            foreach (var toggle in new[] { ChargeCompleteToggle, ChargingStartedToggle, SleptWhileHotToggle,
                                           SettingsNotSavedToggle, ScriptFailedToggle })
                toggle.IsOn = NotificationSwitches.IsOn(s, Enum.Parse<NotificationKind>((string)toggle.Tag));
        });
    }

    private void OnNotificationSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleSwitch { Tag: string tag } toggle) return;
        var  kind = Enum.Parse<NotificationKind>(tag);
        bool on   = toggle.IsOn;
        SettingsService.Update(s => NotificationSwitches.Set(s, kind, on));
    }

    private void OnNotificationSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NotificationSoundCombo.SelectedItem is not ComboBoxItem { Tag: NotificationSound sound }) return;
        NotificationSoundPreviewButton.IsEnabled = NotificationSounds.HasOwnRecording(sound);
        if (_updating) return;
        SettingsService.Update(s => s.NotificationSound = sound);
    }

    private void OnNotificationSoundPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (NotificationSoundCombo.SelectedItem is ComboBoxItem { Tag: NotificationSound sound })
            NotificationSoundPlayer.Preview(sound);
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

    /// <summary>
    /// Every saved-value list this window shows, each describing itself to the one shared mechanism:
    /// where its rows go, what it says when empty, how a row is built, what "in use" means for it,
    /// whether activation can be offered at all, and the feature switch that decides whether the
    /// marking means anything. Built once per window; each list reads its values afresh on every
    /// rebuild.
    /// </summary>
    private void BuildSavedValueLists()
    {
        _thresholdList = new PresetRows.Section
        {
            Panel     = PresetsListPanel,
            EmptyText = "No presets yet. Add one below.",
            Count     = () => SettingsService.Current.Presets.Count,
            BuildRow  = i => BuildPresetRow(SettingsService.Current.Presets[i]),
            // The thresholds the firmware is actually running, not the stored choice.
            ActiveTag = () => ActivePresetPolicy.Match(SettingsService.Current.Presets, _thresholdState)?.Name,
            // Hidden entirely where the vendor refuses threshold writes.
            OffersActivation = _ => _thresholdState is { Capable: true },
            ActiveTip = "These thresholds are the ones in use.",
            IdleTip   = "Applies these thresholds now.",
        };

        _networkProfileList   = NetworkProfileList(NetworkRulesListPanel, BuildSmartChargeNetworkRow);
        _keepAwakeProfileList = NetworkProfileList(KeepAwakeNetworkRulesListPanel, BuildKeepAwakeNetworkRow);

        _keepAwakePresetList = new PresetRows.Section
        {
            Panel     = KeepAwakePresetsListPanel,
            EmptyText = "No presets yet. Add one below.",
            Count     = () => SettingsService.Current.KeepAwakePresets.Count,
            BuildRow  = i => BuildKeepAwakePresetRow(i, SettingsService.Current.KeepAwakePresets[i]),
            // The preset the running session came from. Always offered: no hardware can refuse a
            // keep-awake hold the way fixed-mode firmware refuses thresholds.
            ActiveTag = () => ActiveKeepAwakePresetPolicy.MatchIndex(
                                  SettingsService.Current.KeepAwakePresets, KeepAwakeService.Current) is int i and >= 0
                              ? i : null,
            ActiveTip = "A session from this preset is running.",
            IdleTip   = "Starts a session from this preset now.",
        };

        _lidDelayList = new PresetRows.Section
        {
            Panel     = LidDelayPresetsListPanel,
            EmptyText = "No delays yet. Add one below.",
            Count     = () => SettingsService.Current.LidDelayPresets.Count,
            BuildRow  = i => BuildLidDelayPresetRow(i, SettingsService.Current.LidDelayPresets[i]),
            ActiveTag = DelayPresetInUse,
            // Nothing can act on a delay while the lid-close wait itself is off.
            OffersActivation = _ => SettingsService.Current.LidDelayEnabled,
            FeatureOn = () => SettingsService.Current is { LidDelayEnabled: true, LidDelayTimeEnabled: true },
            ActiveTip = "This is the delay the lid-close wait runs.",
            IdleTip   = "Makes this the delay the lid-close wait runs.",
        };

        _lidDischargeList = new PresetRows.Section
        {
            Panel     = LidDischargeTargetsListPanel,
            EmptyText = "No targets yet. Add one below.",
            Count     = () => SettingsService.Current.LidDischargePresets.Count,
            BuildRow  = i => BuildLidDischargeTargetRow(i, SettingsService.Current.LidDischargePresets[i]),
            ActiveTag = DischargeTargetInUse,
            OffersActivation = _ => SettingsService.Current.LidDelayEnabled,
            FeatureOn = () => SettingsService.Current is { LidDelayEnabled: true, LidDischargeEnabled: true },
            ActiveTip = "This is the level the lid-close wait drains to.",
            IdleTip   = "Makes this the level the lid-close wait drains to.",
        };
    }

    /// <summary>One page's rendering of the shared network-profile list. Both pages mark the same
    /// profile — the one the network we are on matches — and only that row offers activation, since
    /// activating any other would apply the winner rather than itself.</summary>
    private PresetRows.Section NetworkProfileList(StackPanel panel, Func<int, SettingsExpander> buildRow) => new()
    {
        Panel     = panel,
        EmptyText = NoNetworkRulesText,
        Count     = () => SettingsService.Current.NetworkLocationRules.Count,
        BuildRow  = buildRow,
        ActiveTag = () => _winningProfileRow >= 0 ? _winningProfileRow : null,
        OffersActivation = tag => _winningProfileRow >= 0 && Equals(tag, _winningProfileRow),
        FeatureOn = () => SettingsService.Current.NetworkProfilesEnabled,
        ActiveTip = "This profile matches the network this computer is on, and is the one being applied.",
        IdleTip   = "Switches network profiles on and applies this profile now.",
    };

    private void RebuildPresetRows()
    {
        // Every existing row is about to be discarded — stop its debounce timer first, or a drag
        // still settling can fire afterwards and commit a stale value against a detached row.
        StopAllPresetDebounceTimers();

        // Read once here rather than per row: the vendor read blocks.
        _thresholdState = ChargeThresholdService.Read();
        _thresholdList.Rebuild();
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
    /// activation.</summary>
    private void RefreshPresetActivationStates(ChargeThresholdState? state)
    {
        _thresholdState = state;
        _thresholdList.RefreshActivation();
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
            _mqtt?.Republish();   // the preset select carries the old name until the document is rewritten
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
        _mqtt?.Republish();   // the deleted name must stop being offered by the preset select
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
        _mqtt?.Republish();           // …and from the preset select, once the document is rewritten
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
        bool on = SettingsService.Current.NetworkProfilesEnabled;
        WithUpdatingSuppressed(() =>
        {
            NetworkEnabledToggle.IsOn          = on;
            KeepAwakeNetworkEnabledToggle.IsOn = on;
        });
        RefreshCurrentNetworkText();
        RebuildNetworkRuleRows();
    }

    /// <summary>Both pages carry the switch, because the Smart Charge page's copy sits inside the
    /// block that collapses on fixed-mode hardware, where the keep-awake side still has to be
    /// reachable. Either copy drives the one setting.</summary>
    private void OnNetworkEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        SetNetworkProfilesEnabled(((ToggleSwitch)sender).IsOn);
    }

    private void SetNetworkProfilesEnabled(bool on)
    {
        // Through the service, which applies the profile that wins here when it goes on and releases
        // the hold a profile took when it goes off. A plain settings write leaves both behind.
        NetworkProfiles.SetEnabled(on, "the Settings page");

        WithUpdatingSuppressed(() =>
        {
            NetworkEnabledToggle.IsOn          = on;
            KeepAwakeNetworkEnabledToggle.IsOn = on;
        });
        // A rebuild rather than a refresh: the rows say whether a keep-awake tick acts, which has
        // just moved, as well as which profile is in use.
        RebuildNetworkRuleRows();
        RefreshKeepAwakeState();
    }

    private void RefreshCurrentNetworkText() =>
        SmartChargeCurrentNetwork.Value = NetworkLocationService.DescribeCurrentLocation();

    /// <summary>Rebuilds both pages' renderings of the one
    /// <see cref="AppSettings.NetworkLocationRules"/> list, always together, so neither is left
    /// showing a rule the other just deleted or renamed.</summary>
    private void RebuildNetworkRuleRows()
    {
        // All three resolved ONCE per rebuild, not per row: the adapter enumeration is the expensive
        // one, and the winning profile must be the same answer on both pages.
        _profileRowLocation = NetworkProfiles.CurrentLocation();
        _profileRowAdapters = NetworkLocationService.EnumerateAdapters();
        _profileRowPresets  = SettingsService.Current.Presets.Select(p => p.Name).ToList();
        _winningProfileRow  = WinningProfileRow(_profileRowLocation);

        _networkProfileList.Rebuild();
        _keepAwakeProfileList.Rebuild();
    }

    /// <summary>The position of the profile that matches <paramref name="location"/>, or -1 when
    /// none does. First match wins, the one resolution the whole feature uses.</summary>
    private static int WinningProfileRow(NetworkLocation location) =>
        SettingsService.Current.NetworkLocationRules.FindIndex(r => r.Matches(location));

    /// <summary>One wording for both pages, so an empty list reads the same wherever it is met.</summary>
    private const string NoNetworkRulesText =
        "No network profiles yet. “Add profile for this network…” below adds one for the network currently connected.";

    /// <summary>One profile's row on the Smart Charge page, where its facet is the charge preset.</summary>
    private SettingsExpander BuildSmartChargeNetworkRow(int index)
    {
        var rule = SettingsService.Current.NetworkLocationRules[index];

        var presetCombo = new ComboBox { MinWidth = 220, PlaceholderText = "Choose a preset" };
        foreach (var n in _profileRowPresets) presetCombo.Items.Add(n);
        presetCombo.SelectedItem = _profileRowPresets.Contains(rule.PresetName) ? rule.PresetName : null;

        var row = BuildNetworkRuleRow(
            index, rule, DescribeRulePresetSummary(rule),
            new SettingsCard { Header = "Preset", Content = presetCombo },
            () => _keepAwakeProfileList.Rebuild());

        presetCombo.SelectionChanged += (_, _) =>
        {
            if (presetCombo.SelectedItem is string preset) CommitNetworkRulePreset(index, preset, row.Expander);
        };
        return row.Expander;
    }

    /// <summary>The rule's match key, plus a hint when the key no longer fits: its MAC belongs to a
    /// virtual adapter, or its subnet is the one we are on while its MAC is not. A stored subnet that
    /// mobile ignores is shown as ignored, not as a subnet. Stated only: nothing here rewrites a
    /// stored key.</summary>
    private static string DescribeMatchKey(
        NetworkLocationRule rule, NetworkLocation current, IReadOnlyList<BridgePeer> adapters)
    {
        string key = NetworkLocationService.DescribeMatchKey(
            rule.AdapterMac, rule.IpCidr, rule.SubnetIgnoredOn(current));
        return NetworkLocationService.DescribeStaleKey(rule, current, adapters) is { } hint
            ? $"{key}\n{hint}"
            : key;
    }

    private static string DescribeRulePresetSummary(NetworkLocationRule rule) =>
        string.IsNullOrEmpty(rule.PresetName) ? "No preset assigned" : $"Applies “{rule.PresetName}”";

    /// <summary>The keep-awake facet as the row reads it. A tick nothing acts on says so: the switch
    /// that decides whether it acts is on the page too, and a stored tick with no visible effect is
    /// the one state that reads as broken.</summary>
    private static string DescribeRuleKeepAwakeSummary(bool keepAwakeHere) =>
        !keepAwakeHere                                     ? "No keep-awake here"
        : SettingsService.Current.NetworkProfilesEnabled    ? "Keeps this computer awake"
                                                            : "Keeps this computer awake — nothing acts on it while network profiles are off";

    /// <summary>Builds one network rule's editor row for either page: both carry the name, the match
    /// key and Delete, and differ only in <paramref name="pageCard"/> and the summary line, so a rule
    /// reads the same on both. Keyed by list index — a rule has no identity of its own and two may
    /// share a name, which is unambiguous only because every commit path below rebuilds the lists.
    /// </summary>
    /// <param name="rebuildOtherPage">The other page's rebuild, run after a rename: both pages show
    /// the rule's name, and the row being edited keeps its focus rather than being rebuilt under it.</param>
    private PresetRows.Parts BuildNetworkRuleRow(
        int index, NetworkLocationRule rule, string summary, SettingsCard pageCard, Action rebuildOtherPage)
    {
        var nameBox = new TextBox { Text = rule.Name, MinWidth = 220 };

        // Deleting is offered on both pages because there is one rule, not one per page — its
        // keep-awake side and its preset side go together.
        var row = PresetRows.Build(
            rule.Name, summary, index,
            [
                new SettingsCard { Header = "Name",    Content = nameBox },
                new SettingsCard { Header = "Matches", Description = DescribeMatchKey(rule, _profileRowLocation, _profileRowAdapters) },
                pageCard,
            ],
            CriticalBrush(), deleteLabel: "Delete profile");

        row.Activate.Click += (_, _) => ActivateNetworkProfile();

        void CommitName() => CommitNetworkRuleName(index, nameBox.Text, row.Header, rebuildOtherPage);
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) CommitName(); };
        row.Delete.Click  += (_, _) => DeleteNetworkRule(index);

        return row;
    }

    /// <summary>Puts the profile for the network we are on into effect: the feature goes on if it was
    /// off, and the profile that wins here is applied at once. Only the winning row offers this, so
    /// there is exactly one profile it can mean.</summary>
    private void ActivateNetworkProfile() => SetNetworkProfilesEnabled(true);

    private void CommitNetworkRuleName(int index, string? newNameRaw, TextBlock header,
        Action rebuildOtherPage)
    {
        var rules = SettingsService.Current.NetworkLocationRules;
        if (index < 0 || index >= rules.Count) return;
        string newName = string.IsNullOrWhiteSpace(newNameRaw) ? rules[index].Name : newNameRaw!.Trim();

        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules[index].Name = newName;
        });
        header.Text = newName;
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
        NetworkProfiles.ApplyWinner(NetworkProfiles.CurrentLocation());
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
        var location = NetworkProfiles.CurrentLocation();
        NetworkProfiles.ApplyWinner(location);
        KeepAwakeService.ReconcileNetworkHold(location, "a network profile was deleted");
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

        string suggested = location.DisplayHint
            ?? (location.IsMobile ? NetworkLocationService.MobileLabel
                : location.IsWired ? NetworkLocationService.WiredLabel
                                   : NetworkLocationService.WirelessLabel);
        string? name = await new NameLocationWindow(
            suggested,
            NetworkLocationService.DescribeMatchKey(location.AdapterMac, location.IpCidr, location.IsMobile)).ShowAsync();
        if (name is null) return null;   // cancelled

        var s0 = SettingsService.Current;
        // The preset in use is the obvious default for a new rule; the first one when none is.
        string defaultPreset = ActivePresetInUse() ?? s0.Presets.FirstOrDefault()?.Name ?? "";

        SettingsService.Update(s =>
        {
            s.NetworkLocationRules.Add(new NetworkLocationRule
            {
                Id            = StableId.New(),
                Name          = name,
                AdapterMac    = location.AdapterMac,
                IpCidr        = location.IpCidr,
                PresetName    = defaultPreset,
                KeepAwakeHere = keepAwakeHere,
            });
            // Rules are inert with profiles off, so configuring a location implies wanting them on.
            s.NetworkProfilesEnabled = true;
        });

        WithUpdatingSuppressed(() =>
        {
            NetworkEnabledToggle.IsOn          = true;
            KeepAwakeNetworkEnabledToggle.IsOn = true;
        });
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
            NetworkProfiles.ApplyWinner(location);
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
        KeepAwakeService.StateChanged           += OnKeepAwakeStateChanged;
        LidDelayService.StateChanged            += OnLidDelayStateChanged;
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
            LidLockToggle.IsOn          = s.LidDelayLockOnClose;
            LidDelayTimeToggle.IsOn     = s.LidDelayTimeEnabled;
            LidDischargeToggle.IsOn     = s.LidDischargeEnabled;
            LidThermalToggle.IsOn       = s.LidThermalCeilingEnabled;
            LoadPresetCombo(LidThermalCeilingCombo, LidThermalCeilingPresets,
                            LidThermalWatch.Clamp(s.LidThermalCeilingCelsius), v => $"{v} °C");
            LidOffAfterSleepToggle.IsOn = s.LidDelayOffAfterSleep;
        });
        RefreshLidDelayState();
        RefreshKeepAwakeState();
        RefreshKeepAwakeCustomEcho();
        RebuildKeepAwakeChips();
        RebuildKeepAwakePresetRows();
        RebuildLidDelayPresetRows();
        RebuildLidDischargeTargetRows();
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
        RefreshKeepAwakeAfterText(session is not null);

        // Covers every way a session can start or end, including expiry and the dashboard's chips.
        RefreshKeepAwakeActivationStates();
    }

    /// <summary>
    /// The line under the toggle: what a running session does to a lid close, and what Windows does
    /// once the hold is let go. Both are read afresh on every refresh rather than cached — the power
    /// plan and the power source can both change under an open page, and the mains and battery sleep
    /// delays differ by a large factor, so a cached figure would be a wrong number rather than a
    /// stale one. Nothing is shown when no session is running: the page already says so.
    /// </summary>
    private void RefreshKeepAwakeAfterText(bool sessionRunning)
    {
        var lines = new List<string>(2);

        if (KeepAwakePolicy.DescribeLidEffect(sessionRunning, SettingsService.Current.LidDelayEnabled)
            is { } lidEffect)
            lines.Add(lidEffect);

        if (sessionRunning)
        {
            // Both names are qualified: Windows.System carries a PowerState of its own, and this is
            // the app's.
            bool onBattery = PowerStates.From(Windows.System.Power.PowerManager.BatteryStatus)
                             == Helpers.PowerState.Discharging;
            if (SleepDelayPolicy.Describe(
                    SleepDelayPolicy.ForPowerSource(NativeMethods.ReadSleepDelay(), onBattery),
                    onBattery) is { } sleepRule)
                lines.Add(sleepRule);
        }

        KeepAwakeAfterText.Text       = string.Join(" ", lines);
        KeepAwakeAfterText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    // Raised off the UI thread by LidDelayService, including when the feature stands itself down
    // after a lid close reached sleep — marshal before touching anything.
    private void OnLidDelayStateChanged() => RunOnUi(RefreshLidDelayState);

    /// <summary>Puts the master switch and everything that depends on it where the setting is. Driven
    /// by the page load, by the switch itself, and by <see cref="LidDelayService.StateChanged"/>, so a
    /// feature that switches itself off shows here without the page being reopened.</summary>
    private void RefreshLidDelayState()
    {
        // The setting rather than the toggle: an enable the power scheme refused never reached it,
        // and the dependants follow what the feature actually is.
        bool on = SettingsService.Current.LidDelayEnabled;
        WithUpdatingSuppressed(() => LidDelayToggle.IsOn = on);
        LidLockToggle.IsEnabled          = on;
        LidOffAfterSleepToggle.IsEnabled = on;
        LidDelayTimeToggle.IsEnabled     = on;
        LidDischargeToggle.IsEnabled     = on;

        // Read fresh on every call rather than kept live: this only re-runs when the page is shown
        // (construction, re-activation, a lid-delay state change), which is early enough for a
        // reading that can otherwise take a short while to arrive after start.
        bool hasReading = ThermalStatusService.PublishableCelsius is not null;
        LidThermalToggle.IsEnabled       = on && hasReading;
        LidThermalCeilingCombo.IsEnabled = on && hasReading;
        LidThermalCard.Description = hasReading
            ? "Ends the wait early and sleeps the computer, ahead of the delay and the battery target."
            : "No temperature reading is available on this computer.";

        // Read fresh rather than cached, like every other reading on this page — and shown whether
        // the feature is on or off, because it is what someone deciding whether to switch it on
        // needs to know.
        string? caveat = StandbyCapability.LidWaitCaveat(StandbyCapability.Read());
        LidStandbyCaveatText.Text       = caveat ?? string.Empty;
        LidStandbyCaveatText.Visibility = caveat is null ? Visibility.Collapsed : Visibility.Visible;

        // Both lists are marked against conditions this switch gates, so their marking moves with it.
        RefreshLidDelayPresetActivationStates();
        RefreshLidDischargeActivationStates();
    }

    private void OnLidDelayToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        // LidDelayService owns the setting and the power-scheme write together, so the two cannot
        // drift. Only enabling can fail visibly; RefreshLidDelayState then puts the toggle back
        // rather than showing an on state the machine will not honour.
        LidDelayService.SetEnabled(LidDelayToggle.IsOn);
        RefreshLidDelayState();
    }

    private void OnLidOffAfterSleepToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = LidOffAfterSleepToggle.IsOn;
        SettingsService.Update(s => s.LidDelayOffAfterSleep = on);
    }

    private void OnLidDelayTimeToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        LidDelayService.SetTimeEnabled(LidDelayTimeToggle.IsOn);
        RefreshLidDelayPresetActivationStates();
    }

    private void OnLidLockToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = LidLockToggle.IsOn;
        SettingsService.Update(s => s.LidDelayLockOnClose = on);
    }

    // The lid-close delay: how long the machine stays awake with the lid shut before sleep is
    // allowed. Same row shell as every other preset list on this page, and the delay in use is the
    // one marked active.

    /// <summary>A saved delay as its row header reads — its name when it has one, else the span.</summary>
    private static string DescribeDelayPreset(LidDelayPreset p) =>
        string.IsNullOrWhiteSpace(p.Name) ? DescribeDelaySpan(p) : p.Name!;

    /// <summary>The row's subtitle: the span, but only when the header isn't already showing it.</summary>
    private static string DescribeDelaySubtitle(LidDelayPreset p) =>
        string.IsNullOrWhiteSpace(p.Name) ? "" : DescribeDelaySpan(p);

    private static string DescribeDelaySpan(LidDelayPreset p) =>
        $"After {LidDelayPolicy.DelayFor(p.Minutes).TotalMinutes:0} min";

    private void RebuildLidDelayPresetRows() => _lidDelayList.Rebuild();

    /// <summary>Marks the row whose span is the configured delay.</summary>
    private void RefreshLidDelayPresetActivationStates() => _lidDelayList.RefreshActivation();

    /// <summary>The saved delay the lid-close wait runs, by position, or null when none matches.
    /// Matched on the clamped span rather than by position, so two rows at the same span both read
    /// as in use rather than one of them arbitrarily.</summary>
    private static object? DelayPresetInUse()
    {
        var s = SettingsService.Current;
        int minutes = (int)LidDelayPolicy.DelayFor(s.LidDelayMinutes).TotalMinutes;
        int active = s.LidDelayPresets.FindIndex(p => (int)LidDelayPolicy.DelayFor(p.Minutes).TotalMinutes == minutes);
        return active >= 0 ? active : null;
    }

    /// <summary>One delay's editor row — a name and the span, entered as whole minutes.</summary>
    private SettingsExpander BuildLidDelayPresetRow(int index, LidDelayPreset preset)
    {
        var nameBox = new TextBox { Text = preset.Name ?? "", MinWidth = 220, PlaceholderText = DescribeDelaySpan(preset) };
        // CurrentCulture on both sides, so what is shown is what parses back.
        var minutesBox = new TextBox
        {
            Text            = preset.Minutes.ToString(System.Globalization.CultureInfo.CurrentCulture),
            MinWidth        = 220,
            PlaceholderText = $"{LidDelayPolicy.MinMinutes}–{LidDelayPolicy.MaxMinutes}",
        };

        // Tagged by position, not by name: a delay need not have one.
        var row = PresetRows.Build(
            DescribeDelayPreset(preset), DescribeDelaySubtitle(preset), index,
            [
                new SettingsCard { Header = "Name",    Description = "Optional — the delay is shown when this is blank.", Content = nameBox },
                new SettingsCard { Header = "Minutes", Description = "How long to stay awake with the lid closed.",       Content = minutesBox },
            ],
            CriticalBrush());

        row.Activate.Click += (_, _) => ActivateLidDelayPresetAt(index);

        void Commit() => CommitLidDelayPresetRow(index, nameBox, minutesBox, row);
        nameBox.LostFocus    += (_, _) => Commit();
        nameBox.KeyDown      += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        minutesBox.LostFocus += (_, _) => Commit();
        minutesBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };

        row.Delete.Click += (_, _) => DeleteLidDelayPreset(index);

        return row.Expander;
    }

    /// <summary>Validates and saves one delay row, same reject-on-save contract as the lists around
    /// it. A blank span keeps the stored one, so clearing the field cannot destroy a delay.</summary>
    private void CommitLidDelayPresetRow(int index, TextBox nameBox, TextBox minutesBox,
        PresetRows.Parts row)
    {
        var presets = SettingsService.Current.LidDelayPresets;
        if (index < 0 || index >= presets.Count) return;

        int minutes = presets[index].Minutes;
        string typed = minutesBox.Text?.Trim() ?? "";
        if (typed.Length > 0)
        {
            if (!int.TryParse(typed, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.CurrentCulture, out int parsed))
            {
                ShowInlineError(row.Error, "Enter the delay as a whole number of minutes, like 30.");
                return;
            }
            // The same bounds a remote write is held to, rather than a second, looser set here.
            if (SettingRanges.Validate(parsed, LidDelayPolicy.MinMinutes, LidDelayPolicy.MaxMinutes,
                                       "The delay") is { } error)
            {
                ShowInlineError(row.Error, error);
                return;
            }
            minutes = parsed;
        }
        row.Error.Visibility = Visibility.Collapsed;

        string? name = nameBox.Text?.Trim() is { Length: > 0 } n ? n : null;
        var updated = new LidDelayPreset(minutes, name);

        SettingsService.Update(s =>
        {
            if (index < s.LidDelayPresets.Count) s.LidDelayPresets[index] = updated;
        });

        row.Header.Text          = DescribeDelayPreset(updated);
        row.Expander.Description = DescribeDelaySubtitle(updated);
        minutesBox.Text          = updated.Minutes.ToString(System.Globalization.CultureInfo.CurrentCulture);
        nameBox.PlaceholderText  = DescribeDelaySpan(updated);

        // An edited span may now be, or may no longer be, the one in use.
        RefreshLidDelayPresetActivationStates();
    }

    /// <summary>Makes a saved delay the one the lid-close wait runs, re-read by position so an edit
    /// committed since the row was built is the span that lands.</summary>
    private void ActivateLidDelayPresetAt(int index)
    {
        var presets = SettingsService.Current.LidDelayPresets;
        if (index < 0 || index >= presets.Count) return;
        int minutes = (int)LidDelayPolicy.DelayFor(presets[index].Minutes).TotalMinutes;
        LidDelayService.SetDelayMinutes(minutes, "the Settings page");
        // Choosing a delay is asking for the condition it belongs to: a delay that is stored and not
        // run is the state the marking would otherwise claim was in use.
        if (!SettingsService.Current.LidDelayTimeEnabled) LidDelayService.SetTimeEnabled(true);
        WithUpdatingSuppressed(() => LidDelayTimeToggle.IsOn = true);
        RefreshLidDelayPresetActivationStates();
    }

    private void DeleteLidDelayPreset(int index)
    {
        SettingsService.Update(s =>
        {
            if (index < s.LidDelayPresets.Count) s.LidDelayPresets.RemoveAt(index);
        });
        RebuildLidDelayPresetRows();
    }

    private void OnAddLidDelayPreset(object sender, RoutedEventArgs e)
    {
        // Ten minutes is the shipped default; the point is that the row exists and is editable.
        SettingsService.Update(s => s.LidDelayPresets.Add(new LidDelayPreset(10)));
        RebuildLidDelayPresetRows();
    }

    // The lid-close discharge target: a level the battery drains to with the lid shut before sleep is
    // allowed. The rows are the same shell the keep-awake presets use, and the target in use is the
    // one marked active.

    private void OnLidThermalToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = LidThermalToggle.IsOn;
        SettingsService.Update(s => s.LidThermalCeilingEnabled = on);
    }

    private void OnLidThermalCeilingChanged(object sender, SelectionChangedEventArgs e)
        => CommitPresetCombo(LidThermalCeilingCombo, (s, v) => s.LidThermalCeilingCelsius = v);

    private void OnLidDischargeToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        // Through the service, which also drops an outstanding target when this goes off — a plain
        // settings write would leave the machine held awake for a target no longer configured.
        LidDelayService.SetDischargeEnabled(LidDischargeToggle.IsOn);
        RefreshLidDischargeActivationStates();
    }

    /// <summary>A saved target as its row header reads — its name when it has one, else the level.</summary>
    private static string DescribeDischargeTarget(LidDischargeTarget t) =>
        string.IsNullOrWhiteSpace(t.Name) ? DescribeDischargeLevel(t) : t.Name!;

    /// <summary>The row's subtitle: the level, but only when the header isn't already showing it.</summary>
    private static string DescribeDischargeSubtitle(LidDischargeTarget t) =>
        string.IsNullOrWhiteSpace(t.Name) ? "" : DescribeDischargeLevel(t);

    private static string DescribeDischargeLevel(LidDischargeTarget t) =>
        $"Down to {LidDischargeWatch.Clamp(t.Percent)} %";

    private void RebuildLidDischargeTargetRows() => _lidDischargeList.Rebuild();

    /// <summary>Marks the row whose level is the configured target.</summary>
    private void RefreshLidDischargeActivationStates() => _lidDischargeList.RefreshActivation();

    /// <summary>The saved target the lid-close wait drains to, by position, or null when none
    /// matches. Matched on the clamped level rather than by position, so two rows at the same level
    /// both read as in use rather than one of them arbitrarily.</summary>
    private static object? DischargeTargetInUse()
    {
        var s = SettingsService.Current;
        int target = LidDischargeWatch.Clamp(s.LidDischargeTargetPercent);
        int active = s.LidDischargePresets.FindIndex(t => LidDischargeWatch.Clamp(t.Percent) == target);
        return active >= 0 ? active : null;
    }

    /// <summary>One target's editor row — a name and the level, entered as a plain percentage.</summary>
    private SettingsExpander BuildLidDischargeTargetRow(int index, LidDischargeTarget target)
    {
        var nameBox    = new TextBox { Text = target.Name ?? "", MinWidth = 220, PlaceholderText = DescribeDischargeLevel(target) };
        // CurrentCulture on both sides, so what is shown is what parses back.
        var percentBox = new TextBox
        {
            Text            = target.Percent.ToString(System.Globalization.CultureInfo.CurrentCulture),
            MinWidth        = 220,
            PlaceholderText = $"{LidDischargeWatch.MinPercent}–{LidDischargeWatch.MaxPercent}",
        };

        // Tagged by position, not by name: a target need not have one.
        var row = PresetRows.Build(
            DescribeDischargeTarget(target), DescribeDischargeSubtitle(target), index,
            [
                new SettingsCard { Header = "Name",  Description = "Optional — the level is shown when this is blank.", Content = nameBox },
                new SettingsCard { Header = "Level", Description = "The charge level to drain to, as a percentage.",     Content = percentBox },
            ],
            CriticalBrush());

        row.Activate.Click += (_, _) => ActivateLidDischargeTargetAt(index);

        void Commit() => CommitLidDischargeTargetRow(index, nameBox, percentBox, row);
        nameBox.LostFocus    += (_, _) => Commit();
        nameBox.KeyDown      += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        percentBox.LostFocus += (_, _) => Commit();
        percentBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };

        row.Delete.Click += (_, _) => DeleteLidDischargeTarget(index);

        return row.Expander;
    }

    /// <summary>Validates and saves one target row, same reject-on-save contract as the preset lists
    /// above it. A blank level keeps the stored one, so clearing the field cannot destroy a target.</summary>
    private void CommitLidDischargeTargetRow(int index, TextBox nameBox, TextBox percentBox,
        PresetRows.Parts row)
    {
        var targets = SettingsService.Current.LidDischargePresets;
        if (index < 0 || index >= targets.Count) return;

        int percent = targets[index].Percent;
        string typed = percentBox.Text?.Trim() ?? "";
        if (typed.Length > 0)
        {
            if (!int.TryParse(typed, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.CurrentCulture, out int parsed))
            {
                ShowInlineError(row.Error, "Enter the level as a whole number, like 50.");
                return;
            }
            // The same bounds a remote write is held to, rather than a second, looser set here.
            if (SettingRanges.Validate(parsed, LidDischargeWatch.MinPercent, LidDischargeWatch.MaxPercent,
                                       "The level") is { } error)
            {
                ShowInlineError(row.Error, error);
                return;
            }
            percent = parsed;
        }
        row.Error.Visibility = Visibility.Collapsed;

        string? name = nameBox.Text?.Trim() is { Length: > 0 } n ? n : null;
        var updated = new LidDischargeTarget(percent, name);

        SettingsService.Update(s =>
        {
            if (index < s.LidDischargePresets.Count) s.LidDischargePresets[index] = updated;
        });

        row.Header.Text          = DescribeDischargeTarget(updated);
        row.Expander.Description = DescribeDischargeSubtitle(updated);
        percentBox.Text          = updated.Percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
        nameBox.PlaceholderText  = DescribeDischargeLevel(updated);

        // An edited level may now be, or may no longer be, the one in use.
        RefreshLidDischargeActivationStates();
    }

    /// <summary>Makes a saved target the one the lid-close wait drains to, re-read by position so an
    /// edit committed since the row was built is the level that lands.</summary>
    private void ActivateLidDischargeTargetAt(int index)
    {
        var targets = SettingsService.Current.LidDischargePresets;
        if (index < 0 || index >= targets.Count) return;
        int percent = LidDischargeWatch.Clamp(targets[index].Percent);
        SettingsService.Update(s => s.LidDischargeTargetPercent = percent);
        // Choosing a target is asking for the condition it belongs to; through the service, which
        // pairs the setting with the wait in flight.
        if (!SettingsService.Current.LidDischargeEnabled) LidDelayService.SetDischargeEnabled(true);
        WithUpdatingSuppressed(() => LidDischargeToggle.IsOn = true);
        RefreshLidDischargeActivationStates();
    }

    private void DeleteLidDischargeTarget(int index)
    {
        SettingsService.Update(s =>
        {
            if (index < s.LidDischargePresets.Count) s.LidDischargePresets.RemoveAt(index);
        });
        RebuildLidDischargeTargetRows();
    }

    private void OnAddLidDischargeTarget(object sender, RoutedEventArgs e)
    {
        // Half charge is the least surprising starting figure; the point is that the row exists and
        // is editable.
        SettingsService.Update(s => s.LidDischargePresets.Add(new LidDischargeTarget(50)));
        RebuildLidDischargeTargetRows();
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
        { Kind: KeepAwakeKind.UntilTime, Until: not null }                       => KeepAwakePolicy.SpanLabel(r),
        { Kind: KeepAwakeKind.Duration, Duration: { } d } when d > TimeSpan.Zero => KeepAwakePolicy.SpanLabel(r),
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

    private void RebuildKeepAwakePresetRows() => _keepAwakePresetList.Rebuild();

    /// <summary>Marks the row whose preset started the running session.</summary>
    private void RefreshKeepAwakeActivationStates() => _keepAwakePresetList.RefreshActivation();

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
        KeepAwakeCurrentNetwork.Value = NetworkLocationService.DescribeCurrentLocation();

    /// <summary>One profile's row on the Keep Awake page, where its facet is the hold.</summary>
    private SettingsExpander BuildKeepAwakeNetworkRow(int index)
    {
        var rule   = SettingsService.Current.NetworkLocationRules[index];
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsOn = rule.KeepAwakeHere };

        var row = BuildNetworkRuleRow(
            index, rule, DescribeRuleKeepAwakeSummary(rule.KeepAwakeHere),
            new SettingsCard { Header = "Keep awake here", Content = toggle },
            () => _networkProfileList.Rebuild());

        // Attached after the initial IsOn, so seeding the switch cannot commit anything.
        toggle.Toggled += (_, _) =>
        {
            if (_updating) return;
            CommitKeepAwakeHere(index, toggle.IsOn);
            row.Expander.Description = DescribeRuleKeepAwakeSummary(toggle.IsOn);
        };
        return row.Expander;
    }

    private void CommitKeepAwakeHere(int index, bool on)
    {
        if (_updating) return;
        SettingsService.Update(s =>
        {
            if (index < s.NetworkLocationRules.Count) s.NetworkLocationRules[index].KeepAwakeHere = on;
        });
        // Without this, ticking "keep awake here" does nothing until you leave and come back, since
        // the service only reacts to a location change.
        KeepAwakeService.ReconcileNetworkHold(NetworkProfiles.CurrentLocation(),
                                              "a network profile's keep-awake was changed");
        RefreshKeepAwakeState();
    }

    /// <summary>The Smart Charge page's add flow with the keep-awake facet filled in instead. No
    /// charge apply: the charge preset is the other page's facet, and fixed-mode
    /// hardware may have no presets to apply at all.</summary>
    private async void OnAddKeepAwakeNetworkRule(object sender, RoutedEventArgs e)
    {
        // async void — guarded whole, see OnAddNetworkRule.
        try
        {
            if (await AddNetworkRuleAsync(keepAwakeHere: true) is null) return;
            RefreshKeepAwakeCurrentNetworkText();
            RefreshCurrentNetworkText();
            KeepAwakeService.ReconcileNetworkHold(NetworkProfiles.CurrentLocation(),
                                                  "a network profile was added");
            RefreshKeepAwakeState();
        }
        catch (Exception ex) { AppLog.Error("SettingsWindow.OnAddKeepAwakeNetworkRule", ex); }
    }

    /// <summary>Hands the shared MQTT panel everything it needs from this application, once, on the
    /// UI thread. Everything protocol-shaped — what a transport is, what the discovery prefix
    /// controls, what Automatic does about encryption — is the module's own copy and is deliberately
    /// absent here; what ChargeKeeper publishes is the module's blind spot and is all that is
    /// supplied.</summary>
    private void InitialiseMqttPanel()
    {
        if (_mqtt is not { } mqtt)
        {
            // No publisher means startup never got that far. Leaving the page blank is honest; an
            // uninitialised panel would offer controls that commit into nothing.
            HomeAssistantPanel.Visibility = Visibility.Collapsed;
            return;
        }

        MqttPanel.Initialise(new MqttPanelSetup
        {
            Settings          = mqtt.Settings,
            Groups            = mqtt.Groups,
            TopicRoot         = MqttPublisher.TopicRoot,
            Activity          = mqtt.Activity,
            ConnectionState   = () => mqtt.State,
            PublishNow        = mqtt.PublishNowAsync,
            // Both must run the real paths: the panel's device-id dialogue has already promised the
            // old entities are removed, which only the connection's apply keeps, and the announced
            // entity set is baked into the retained document, which only a republish rewrites.
            ConnectionChanged = mqtt.ApplyConnection,
            PublishSetChanged = mqtt.Republish,
            RecallEndpoint    = mqtt.RecallEndpoint,
            DefaultDeviceName = mqtt.DefaultDeviceName,
            PublishTitle      = "Publish to MQTT",
            PublishDescription = "Publishes battery and charge state to an MQTT broker.",
            PublishGroupsInfo =
                "Each group covers the entities from the Settings page it is named after. Switching "
                + "one off stops it publishing and marks its entities unavailable; nothing is deleted, "
                + "and switching it back on restores everything you set on them. Entities the hardware "
                + "cannot honour are never announced, whatever these are set to.",
            DeviceIdConsequence =
                "Every ChargeKeeper automation, dashboard card and history graph pointing at the old "
                + "entities has to be repointed by hand.",
            CommandLabel      = mqtt.Entities.NameOf,
            Log               = new AppMqttLog(),
        });
    }

    // The About section hosts BrandAboutControl inline, populated in the ctor, so it needs no
    // handler of its own.

    // Script rows are keyed by list index, like the keep-awake and lid rows: the row's position is
    // what the page has, and the script's own identifier is what the runner uses.

    /// <summary>An edit typed into a script box and not yet saved, one per row. Run on close and
    /// before a rebuild, because a script is minutes of typing rather than a preset's two fields, and
    /// losing it to a re-activation would be the page's worst behaviour.</summary>
    private readonly List<Action> _pendingScriptEdits = [];

    private void LoadScripts() => RebuildScriptRows();

    /// <summary>Saves anything typed into a script box and not yet committed. Safe to call twice: a
    /// row with nothing outstanding does nothing.</summary>
    internal void FlushScriptEdits()
    {
        foreach (var flush in _pendingScriptEdits.ToList()) flush();
    }

    private void RebuildScriptRows()
    {
        FlushScriptEdits();
        _pendingScriptEdits.Clear();

        ScriptsListPanel.Children.Clear();
        var scripts = SettingsService.Current.Scripts;

        if (scripts.Count == 0)
        {
            ScriptsListPanel.Children.Add(PresetRows.EmptyListText("No scripts yet. Add one below."));
            return;
        }

        for (int i = 0; i < scripts.Count; i++)
            ScriptsListPanel.Children.Add(BuildScriptRow(i, scripts[i]));
    }

    /// <summary>The row's subtitle: the event that runs it, and — for a lid script while Lid delay
    /// is off — that it will not run. The page says the rule once; the row says whether it currently
    /// bites.</summary>
    private static string DescribeScript(ScriptDefinition script)
    {
        string when = ScriptTriggerLabels.For(script.Trigger);

        if (ScriptTriggerLabels.IsLid(script.Trigger) && !SettingsService.Current.LidDelayEnabled)
            return $"{when} — does not run while Lid delay is off";

        if (!ScriptTriggerPolicy.NamesANetworkProfile(script.Trigger)) return when;

        if (ProfileOf(script) is { } profile)                     return $"{when} — {profile.Name}";
        if (string.IsNullOrEmpty(script.NetworkProfileId))         return $"{when} — no network profile chosen, so it does not run";
        return $"{when} — the network profile it was bound to has been deleted, so it does not run";
    }

    /// <summary>The profile a script is bound to, or null when it names none or names one that has
    /// been deleted.</summary>
    private static NetworkLocationRule? ProfileOf(ScriptDefinition script) =>
        script.NetworkProfileId is { Length: > 0 } bound
            ? SettingsService.Current.NetworkLocationRules.FirstOrDefault(r => r.Id == bound)
            : null;

    /// <summary>One script's editor row: its name and its event as cards, then the script itself and
    /// Delete.</summary>
    private SettingsExpander BuildScriptRow(int index, ScriptDefinition script)
    {
        var nameBox = new TextBox
        {
            Text            = script.Name,
            MinWidth        = 260,
            PlaceholderText = ScriptTriggerLabels.For(script.Trigger),
        };

        var triggerCombo = new ComboBox { MinWidth = 260 };
        foreach (string label in ScriptTriggerLabels.All) triggerCombo.Items.Add(label);
        triggerCombo.SelectedIndex = (int)script.Trigger;

        // Offered only for the triggers that name a profile; the row is rebuilt when the trigger
        // crosses that line, so the card is either there or not rather than there and inert.
        bool namesProfile = ScriptTriggerPolicy.NamesANetworkProfile(script.Trigger);
        var profiles      = SettingsService.Current.NetworkLocationRules;
        var profileCombo  = new ComboBox { MinWidth = 260, PlaceholderText = "Choose a network profile" };
        if (namesProfile)
        {
            foreach (var profile in profiles) profileCombo.Items.Add(profile.Name);
            profileCombo.SelectedIndex = profiles.FindIndex(p => p.Id == script.NetworkProfileId);
            if (profileCombo.SelectedIndex < 0 && script.NetworkProfileId is { Length: > 0 })
                profileCombo.PlaceholderText = "The profile this script was bound to has been deleted.";
        }

        // AcceptsReturn before Text: a TextBox that is still single-line when Text is assigned keeps
        // only the first line, and turning it multi-line afterwards does not bring the rest back.
        // microsoft-ui-xaml#10956.
        var scriptBox = new TextBox
        {
            AcceptsReturn       = true,
            Text                = script.Body,
            TextWrapping        = TextWrapping.NoWrap,
            Height              = 200,
            IsSpellCheckEnabled = false,
            PlaceholderText     = "The PowerShell to run. Nothing is passed in.",
        };
        // A script is wider and taller than the box: without both bars, a long line is unreachable
        // and there is nothing to say so.
        ScrollViewer.SetVerticalScrollBarVisibility(scriptBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(scriptBox, ScrollBarVisibility.Auto);

        var headerText = new TextBlock { Text = script.DisplayName };
        var runNow     = new Button { Content = "Run now", MinWidth = 88 };
        ToolTipService.SetToolTip(runNow, "Runs the script now, without waiting for its event.");

        var error = new TextBlock
        {
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility   = Visibility.Collapsed,
            Foreground   = CriticalBrush(),
        };
        var delete = new Button { Content = "Delete script" };

        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };
        footer.Children.Add(SmallLabel("Script"));
        footer.Children.Add(scriptBox);
        footer.Children.Add(error);
        footer.Children.Add(delete);

        var cards = new List<SettingsCard>
        {
            new() { Header = "Name",     Description = "Optional — the event is shown when this is blank.", Content = nameBox },
            new() { Header = "Runs when", Description = "One event and one direction. A pair of scripts covers both directions.", Content = triggerCombo },
        };
        if (namesProfile)
            cards.Add(new SettingsCard
            {
                Header      = "Network profile",
                Description = "Joining or leaving this profile's network runs the script. Profiles are added on the Smart Charge page.",
                Content     = profileCombo,
            });

        var expander = new SettingsExpander
        {
            Header      = headerText,
            Description = DescribeScript(script),
            Content     = runNow,
            ItemsSource = cards,
            ItemsFooter = footer,
        };

        void Commit() =>
            CommitScriptRow(index, nameBox, triggerCombo, profileCombo, scriptBox, headerText, expander, error);

        nameBox.LostFocus += (_, _) => Commit();
        nameBox.KeyDown   += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        triggerCombo.SelectionChanged += (_, _) => Commit();
        profileCombo.SelectionChanged += (_, _) => Commit();

        // Debounced, same 700 ms as the preset rows: committing on every keystroke would write the
        // settings document per character, and re-colouring on every one would fight the caret.
        bool typed    = false;
        var  debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        void Flush()
        {
            debounce.Stop();
            if (!typed) return;
            typed = false;
            Commit();
        }
        debounce.Tick     += (_, _) => Flush();
        scriptBox.TextChanged += (_, _) => { typed = true; debounce.Stop(); debounce.Start(); };
        scriptBox.LostFocus   += (_, _) => Flush();
        _pendingScriptEdits.Add(Flush);

        runNow.Click += (_, _) => { Flush(); RunScriptNow(index, error); };
        delete.Click += (_, _) => DeleteScript(index);

        return expander;
    }

    /// <summary>The small caption above an editor block inside a row's footer.</summary>
    private static TextBlock SmallLabel(string text) => new()
    {
        Text       = text,
        FontSize   = 11,
        Foreground = SecondaryBrush(),
    };

    /// <summary>Saves one script row. Nothing is rejected: an empty script is a script not finished
    /// yet, and the runner leaves it alone rather than the page refusing to store it.</summary>
    private void CommitScriptRow(int index, TextBox nameBox, ComboBox triggerCombo, ComboBox profileCombo,
        TextBox scriptBox, TextBlock header, SettingsExpander expander, TextBlock error)
    {
        var scripts = SettingsService.Current.Scripts;
        if (index < 0 || index >= scripts.Count) return;

        // A stored script written before identifiers existed gets one here rather than at load, so
        // nothing rewrites the settings document just for having been read.
        string id = scripts[index].Id is { Length: > 0 } existing ? existing : ScriptDefinition.NewId();

        var trigger  = (ScriptTrigger)Math.Max(triggerCombo.SelectedIndex, 0);
        bool wasNamingAProfile = ScriptTriggerPolicy.NamesANetworkProfile(scripts[index].Trigger);

        var updated = new ScriptDefinition(
            id,
            nameBox.Text?.Trim() ?? "",
            trigger,
            scriptBox.Text ?? "",
            ChosenProfileId(trigger, profileCombo, scripts[index].NetworkProfileId));

        SettingsService.Update(s => { if (index < s.Scripts.Count) s.Scripts[index] = updated; });

        // The row carries the profile picker or it does not, so a trigger that crossed that line
        // needs its row built again rather than edited in place.
        if (ScriptTriggerPolicy.NamesANetworkProfile(trigger) != wasNamingAProfile)
        {
            RebuildScriptRows();
            return;
        }

        header.Text             = updated.DisplayName;
        expander.Description    = DescribeScript(updated);
        nameBox.PlaceholderText = ScriptTriggerLabels.For(updated.Trigger);
        error.Visibility        = Visibility.Collapsed;
    }

    /// <summary>The profile the row's picker names, keeping the stored binding where nothing is
    /// picked — a profile deleted behind a script is reported on the row rather than cleared, which
    /// would leave a script bound to nothing with no sign of what it once ran for.</summary>
    private static string? ChosenProfileId(ScriptTrigger trigger, ComboBox profileCombo, string? stored)
    {
        if (!ScriptTriggerPolicy.NamesANetworkProfile(trigger)) return null;

        var profiles = SettingsService.Current.NetworkLocationRules;
        return profileCombo.SelectedIndex >= 0 && profileCombo.SelectedIndex < profiles.Count
            ? profiles[profileCombo.SelectedIndex].Id
            : stored;
    }

    /// <summary>Runs one script from its row, re-read by position so an edit committed since the row
    /// was built is what runs. The two reasons a run does not start are said on the row: nothing else
    /// would show that the button did nothing.</summary>
    private void RunScriptNow(int index, TextBlock error)
    {
        var scripts = SettingsService.Current.Scripts;
        if (index < 0 || index >= scripts.Count) return;

        var script = scripts[index];
        if (script.IsEmpty)
        {
            ShowInlineError(error, "There is nothing in this script to run.");
            return;
        }

        if (!ScriptRunner.Instance.Start(script, "the Run now button", ScriptRunner.TimeLimit))
        {
            ShowInlineError(error, "This script is already running. The application log says when it started.");
            return;
        }

        error.Visibility = Visibility.Collapsed;
    }

    private void DeleteScript(int index)
    {
        SettingsService.Update(s => { if (index < s.Scripts.Count) s.Scripts.RemoveAt(index); });
        RebuildScriptRows();
    }

    private void OnAddScript(object sender, RoutedEventArgs e)
    {
        SettingsService.Update(s => s.Scripts.Add(
            new ScriptDefinition(ScriptDefinition.NewId(), "", ScriptTrigger.MainsDisconnected, "")));
        RebuildScriptRows();
    }
}
