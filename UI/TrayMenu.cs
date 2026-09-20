using Microsoft.UI.Xaml.Controls;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ZeroZero.Update.Win32;
using ZeroZero.Win32;

namespace ChargeKeeper.UI;

/// <summary>
/// Owns the tray icon's right-click context menu — a flat list of quick toggles and actions.
/// H.NotifyIcon rebuilds a native Win32 popup from the flyout on every right-click and invokes only
/// each item's <c>Command</c>; the XAML <c>Click</c> and <c>Opening</c> events never fire. Items are
/// built once with command bindings, and every mutation funnels through <see cref="QueueRefresh"/>.
/// </summary>
internal sealed class TrayMenu
{
    private readonly ToggleMenuFlyoutItem _autoStartItem;
    private readonly List<(ToggleMenuFlyoutItem Item, TrayIconMode Mode)> _iconModeItems = [];
    private readonly List<(ToggleMenuFlyoutItem Item, TrayDigitStyle Style)> _digitStyleItems = [];

    private readonly MenuFlyoutSubItem _iconStyleSubmenu;

    // Added to and removed from the item list rather than collapsed: the list is what the native
    // popup is rebuilt from on every right-click.
    private readonly MenuFlyoutSubItem _digitStyleSubmenu;

    // A line of text, never a control. A person at the keyboard cannot end a focus session, and
    // nothing should put a way to back here because the line looked incomplete without one.
    private readonly MenuFlyoutItem _focusSessionItem =
        new() { IsEnabled = false, Text = "Focus session" };

    private readonly MenuFlyoutItem _settingsItem;

    private MenuFlyoutItem? _updateItem;
    private AboutWindow?    _aboutWindow;
    private WhatsNewWindow? _whatsNewWindow;

    /// <summary>The update service every entry point works through.</summary>
    internal AppUpdates Updates { get; }

    /// <summary>The one update check every entry point shares: the tray menu, and the button on the
    /// About window and the Settings About page.</summary>
    internal UpdateCheckCoordinator UpdateChecks { get; }

    private readonly Action _onIconModeChanged;
    private readonly Action _onExit;
    private readonly Action _onOpenSettings;

    // App's window-creation gate. About is the one window this class creates, so it must wait on it.
    private readonly Task _windowsReady;

    /// <summary>The flyout to assign to <c>TaskbarIcon.ContextFlyout</c>.</summary>
    public MenuFlyout Flyout { get; }

    /// <summary>The second, percentage-only icon's right-click menu: the digit style and nothing
    /// else.</summary>
    public MenuFlyout PercentageIconFlyout { get; }

    public TrayMenu(Action onExit, Action onIconModeChanged, Action onOpenSettings, Task windowsReady)
    {
        _onExit            = onExit;
        _onIconModeChanged = onIconModeChanged;
        _onOpenSettings    = onOpenSettings;
        _windowsReady      = windowsReady;

        // The exit the flow calls once Setup has started: on the UI thread, and as soon as possible,
        // because Setup waits for this process to go before it replaces the files it holds.
        Updates      = new AppUpdates(() => RunOnUiThread(_onExit, "TrayMenu.exit"),
                                      body => RunOnUiThread(body, "TrayMenu.updateProgress"));
        UpdateChecks = new UpdateCheckCoordinator(Updates.CheckAsync);

        Flyout = new MenuFlyout();

        _autoStartItem = new ToggleMenuFlyoutItem { Text = "Launch at startup" };
        // Target state comes from the item the user just clicked, not a fresh OS read (TOCTOU).
        _autoStartItem.Command = new RelayCommand(() => ToggleAutoStart(!_autoStartItem.IsChecked));

        _settingsItem = new MenuFlyoutItem { Text = "Settings…", Command = new RelayCommand(_onOpenSettings) };
        Flyout.Items.Add(_settingsItem);
        _iconStyleSubmenu  = BuildIconStyleSubmenu();
        _digitStyleSubmenu = BuildDigitStyleSubmenu();
        Flyout.Items.Add(_iconStyleSubmenu);

        // Its own submenu instance: one element cannot sit in two flyouts. Both sets of items are in
        // _digitStyleItems, so one refresh checks both menus.
        PercentageIconFlyout = new MenuFlyout();
        PercentageIconFlyout.Items.Add(BuildDigitStyleSubmenu());

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem
        {
            Text    = "Check for updates",
            Command = new RelayCommand(CheckForUpdates),
        });
        Flyout.Items.Add(_autoStartItem);

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "About…", Command = new RelayCommand(() => ShowAbout()) });

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(onExit) });

        // Never unsubscribed — the subscription lives for the whole process.
        NetworkLocationService.LocationChanged += OnNetworkLocationChanged;

        // Off-thread: ReadState blocks on a vendor RPC and this runs on the UI thread before the
        // tray icon exists. Seeds the first snapshot RefreshState re-applies.
        QueueRefresh();
    }

    /// <summary>Builds the "Icon style" submenu: one checked item per <see cref="TrayIconMode"/>,
    /// reading <see cref="TrayIconModeLabels"/> rather than restating the strings.</summary>
    private MenuFlyoutSubItem BuildIconStyleSubmenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Icon style" };
        foreach (var mode in Enum.GetValues<TrayIconMode>())
        {
            var item = new ToggleMenuFlyoutItem { Text = TrayIconModeLabels.For(mode) };
            item.Command = new RelayCommand(() => SelectIconMode(mode));
            _iconModeItems.Add((item, mode));
            sub.Items.Add(item);
        }
        return sub;
    }

    /// <summary>Builds the "Digit style" submenu: one checked item per <see cref="TrayDigitStyle"/>,
    /// reading <see cref="TrayDigitStyleLabels"/>. Shown only while the icon style is Numeric %.</summary>
    private MenuFlyoutSubItem BuildDigitStyleSubmenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Digit style" };
        foreach (var style in Enum.GetValues<TrayDigitStyle>())
        {
            var item = new ToggleMenuFlyoutItem { Text = TrayDigitStyleLabels.For(style) };
            item.Command = new RelayCommand(() => SelectDigitStyle(style));
            _digitStyleItems.Add((item, style));
            sub.Items.Add(item);
        }
        return sub;
    }

    /// <summary>Applies a digit style chosen from the tray menu — the same write
    /// <c>OnDigitStyleChanged</c> makes from Settings. The committed change repaints the tray, since
    /// the icon request carries the style.</summary>
    private void SelectDigitStyle(TrayDigitStyle style) => Task.Run(() =>
    {
        try
        {
            SettingsService.Update(s => s.PercentageDigitStyle = style);
        }
        catch (Exception ex)
        {
            AppLog.Error("TrayMenu.SelectDigitStyle", ex);
        }
        finally
        {
            QueueRefresh();   // updates the check marks, success or not
        }
    });

    /// <summary>Puts the "Digit style" submenu directly under "Icon style", or takes it out.</summary>
    private void ShowDigitStyleSubmenu(bool shown)
    {
        int index = Flyout.Items.IndexOf(_digitStyleSubmenu);
        if (shown && index < 0)
            Flyout.Items.Insert(Flyout.Items.IndexOf(_iconStyleSubmenu) + 1, _digitStyleSubmenu);
        else if (!shown && index >= 0)
            Flyout.Items.RemoveAt(index);
    }

    /// <summary>Applies a style chosen from the tray menu's own submenu — the same write
    /// <c>OnIconModeChanged</c> makes from Settings.</summary>
    private void SelectIconMode(TrayIconMode mode) => Task.Run(() =>
    {
        try
        {
            SettingsService.ApplyIconModeChoice(mode);
            _onIconModeChanged();   // repaints the tray icon, the same callback a Settings change uses
        }
        catch (Exception ex)
        {
            AppLog.Error("TrayMenu.SelectIconMode", ex);
        }
        finally
        {
            QueueRefresh();   // funnel: mutate → refresh, success or not — updates the check marks
        }
    });

    /// <summary>Inserts or updates an "Update available" item at the top of the menu.</summary>
    public void SetUpdateBadge(string version)
    {
        if (_updateItem is not null)
        {
            _updateItem.Text = $"⬆  Update available: v{version}";
            return;
        }

        _updateItem = new MenuFlyoutItem
        {
            Text    = $"⬆  Update available: v{version}",
            Command = new RelayCommand(CheckForUpdates),
        };

        Flyout.Items.Insert(0, _updateItem);
        Flyout.Items.Insert(1, new MenuFlyoutSeparator());
    }

    /// <summary>
    /// Readies the menu for an imminent open, then kicks a refresh for the next one. Applies the last
    /// snapshot and returns — reading inline would block the UI thread on a vendor RPC per right-click.
    /// </summary>
    public void RefreshState()
    {
        if (_lastApplied is { } cached) ApplyState(cached);
        QueueRefresh();
    }

    /// <summary>Silent resync after a settings change made outside the tray menu.</summary>
    public void ReconcileFromExternalChange()
    {
        _onIconModeChanged();
        // Bare QueueRefresh: no menu is about to open, so there is no cached snapshot to re-apply.
        QueueRefresh();
    }

    /// <summary>
    /// The funnel every state mutation ends in: reads a fresh <see cref="MenuState"/> off the UI thread
    /// and marshals one <see cref="ApplyState"/> back. Any thread. An open popup will not repaint.
    /// </summary>
    private void QueueRefresh() => Task.Run(() =>
    {
        try
        {
            var state = ReadState();
            Flyout.DispatcherQueue?.TryEnqueue(() =>
            {
                // A throw in a raw dispatcher callback tears the process down (see App.RunOnUi).
                try { ApplyState(state); }
                catch (Exception ex) { AppLog.Error("TrayMenu.QueueRefresh", ex); }
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("TrayMenu.QueueRefresh", ex);
        }
    });

    /// <summary>
    /// One immutable snapshot of every input the menu reflects. <see cref="ReadState"/> is the only
    /// producer (may perform RPC, any thread); <see cref="ApplyState"/> the only consumer (UI thread).
    /// </summary>
    private sealed record MenuState(
        bool AutoStartEnabled,
        TrayIconMode IconMode,          // aligned with _iconModeItems
        TrayDigitStyle DigitStyle,      // aligned with _digitStyleItems
        FocusSnapshot Focus);

    private MenuState ReadState()
    {
        bool autoStart = SafeCall(TaskSchedulerHelper.IsAutoStartEnabled, fallback: false);
        var (mode, digits) = SettingsService.Read(s => (s.IconMode, s.PercentageDigitStyle));
        return new MenuState(autoStart, mode, digits, FocusSessionService.Current);
    }

    // The most recent snapshot, re-applied by RefreshState. UI thread only, so no synchronisation.
    private MenuState? _lastApplied;

    private void ApplyState(MenuState state)
    {
        _lastApplied = state;
        _autoStartItem.IsChecked = state.AutoStartEnabled;
        foreach (var (item, mode) in _iconModeItems)
            item.IsChecked = mode == state.IconMode;
        foreach (var (item, style) in _digitStyleItems)
            item.IsChecked = style == state.DigitStyle;
        ShowDigitStyleSubmenu(state.IconMode == TrayIconMode.Numeric);
        ShowFocusSession(state.Focus);
    }

    /// <summary>Puts the focus session line at the top of the menu while one runs, and takes it out
    /// when none does.</summary>
    private void ShowFocusSession(FocusSnapshot session)
    {
        int index = Flyout.Items.IndexOf(_focusSessionItem);

        if (!session.IsRunning)
        {
            if (index >= 0) Flyout.Items.RemoveAt(index);
            return;
        }

        _focusSessionItem.Text = FocusSessionStages.Describe(session, DateTimeOffset.Now);
        // Above Settings…, so it reads before anything actionable and never displaces the update
        // badge that inserts itself at the very top.
        if (index < 0) Flyout.Items.Insert(Flyout.Items.IndexOf(_settingsItem), _focusSessionItem);
    }

    private void ApplyPreset(ThresholdPreset preset) => RunApplyPreset(preset.Name);

    /// <summary>Applies the named preset; a no-op when the name is blank or matches no preset.</summary>
    public void ApplyPresetByName(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName)) return;
        // Resolve first, so an unknown name is a no-op without spinning up a Task.
        if (SettingsService.Current.Presets.Any(p => p.Name == presetName))
            RunApplyPreset(presetName);
    }

    /// <summary>
    /// Applies the named preset off the UI thread (the vendor RPC blocks) via the shared
    /// <see cref="ChargeControlService"/>, which fires StateChanged → QueueRefresh itself.
    /// </summary>
    private void RunApplyPreset(string name)
        => Task.Run(() =>
        {
            // A device-rejected preset returns false; without this the apply is completely silent.
            try
            {
                if (!ChargeControlService.ApplyPresetByName(name))
                    AppLog.Info($"Preset '{name}' was not applied — the device rejected the write.");
            }
            catch { QueueRefresh(); }
        });

    /// <summary>
    /// Auto-apply on a detected location change. Runs on the debounce timer's thread, not the UI one —
    /// <see cref="ApplyPreset"/> marshals its own UI work, so nothing is needed here.
    /// </summary>
    private void OnNetworkLocationChanged(NetworkLocation location)
    {
        var s = SettingsService.Current;
        if (s.NetworkProfilesEnabled)
        {
            // One resolution for every surface, so an apply here and an apply from the Settings page
            // cannot pick different presets for one network.
            string? presetName = NetworkProfiles.WinningPresetName(s, location);
            var preset = presetName is not null
                ? s.Presets.FirstOrDefault(p => p.Name == presetName)
                : null;
            if (preset is not null)
            {
                ApplyPreset(preset); // applies + QueueRefresh internally
                return;
            }
        }
        QueueRefresh(); // still resync check marks even when nothing was applied
    }

    private const string AppName = AppInfo.Name;

    /// <summary>Opens, or re-activates, the single About window.</summary>
    internal async void ShowAbout()
    {
        try
        {
            // Normally already complete, so this does not yield — see the _windowsReady field.
            await _windowsReady.ConfigureAwait(true);

            if (_aboutWindow is not null)
            {
                _aboutWindow.Activate();
                _aboutWindow.CheckForUpdatesAutomatically();
                return;
            }

            _aboutWindow = new AboutWindow(this);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Activate();
            _aboutWindow.CheckForUpdatesAutomatically();
        }
        catch (Exception ex)
        {
            // async void: an escaping exception tears the process down. Drop the half-built window.
            AppLog.Error("TrayMenu.ShowAbout", ex);
            _aboutWindow = null;
        }
    }

    /// <summary>Opens, or re-activates, the single "What's new" window. Reachable at any time, not
    /// only in the moment after an update: a report that cannot be reopened is not always
    /// available.</summary>
    internal async void ShowWhatsNew()
    {
        try
        {
            await _windowsReady.ConfigureAwait(true);

            if (_whatsNewWindow is not null)
            {
                _whatsNewWindow.Activate();
                return;
            }

            _whatsNewWindow = new WhatsNewWindow();
            _whatsNewWindow.Closed += (_, _) => _whatsNewWindow = null;
            _whatsNewWindow.Activate();
        }
        catch (Exception ex)
        {
            // async void: an escaping exception tears the process down. Drop the half-built window.
            AppLog.Error("TrayMenu.ShowWhatsNew", ex);
            _whatsNewWindow = null;
        }
    }

    // One dialog per check. A check can take tens of seconds with nothing on screen, so without
    // this a second click queues a second dialog behind the first. UI thread only — every entry
    // point is a click handler, a flyout command or a window being shown.
    private bool _reportPending;

    /// <summary>The tray menu's entry point: every outcome is reported in a dialog.</summary>
    internal void CheckForUpdates() => CheckForUpdates(UpdateCheckTrigger.TrayMenu);

    /// <summary>
    /// Starts the shared update check, or joins the one running, and reports its outcome as the
    /// trigger requires. Returns the check so a button can show it. A second request that could raise
    /// a dialog while one is already waiting joins silently.
    /// </summary>
    internal Task<UpdateFlowRun> CheckForUpdates(UpdateCheckTrigger trigger)
    {
        // Captured while the flyout or the asking window is in front.
        var hwnd  = NativeMethods.CaptureHwnd();
        var check = UpdateChecks.Run();

        bool mayRaiseDialog = trigger != UpdateCheckTrigger.Automatic;
        if (mayRaiseDialog)
        {
            if (_reportPending) return check;
            _reportPending = true;
        }

        _ = ReportAsync(check, trigger, hwnd, mayRaiseDialog);
        return check;
    }

    /// <summary>Reports one outcome. No ConfigureAwait(false): the continuation returns to the UI
    /// thread, where the flag lives and where a task dialog has the manifest's comctl32 v6 context
    /// that pool threads lack.</summary>
    private async Task ReportAsync(Task<UpdateFlowRun> check, UpdateCheckTrigger trigger,
                                   IntPtr hwnd, bool claimedReport)
    {
        try
        {
            var run = await check;

            if (UpdateButtonPolicy.OpensUpdateDialog(run.Result, trigger))
                await OfferUpdate(run, hwnd);
            else if (UpdateButtonPolicy.ShowsNotice(run.Result, trigger))
                ShowNotice(run, hwnd);
            else if (trigger == UpdateCheckTrigger.Automatic &&
                     run.Result is not (UpdateFlowResult.UpToDate or UpdateFlowResult.UpdateAvailable))
                AppLog.Info($"Update check on opening a window ended {run.Result}; " +
                            "the button returns to rest and no dialog is shown.");
        }
        catch (Exception ex)
        {
            AppLog.Error("TrayMenu.CheckForUpdates", ex);
        }
        finally
        {
            if (claimedReport) _reportPending = false;
        }
    }

    /// <summary>Every outcome but an available update is worded by the shared component, which owns
    /// the text for each one.</summary>
    private void ShowNotice(UpdateFlowRun run, IntPtr hwnd)
    {
        var prompts = Updates.PromptsFor(hwnd);
        switch (run.Result)
        {
            case UpdateFlowResult.UpToDate:
                prompts.SayUpToDate(Updates.RunningVersion);
                break;

            case UpdateFlowResult.NothingReleased:
                prompts.SayNothingReleased();
                break;

            case UpdateFlowResult.CheckFailed when run.Check is { } check:
                prompts.SayCheckFailed(check);
                break;

            // A result added later must not inherit a sibling's wording.
            default:
                AppLog.Info($"Update check ended {run.Result}; nothing is shown for it.");
                break;
        }
    }

    /// <summary>The update dialog for an available release, started from the release already found.
    /// An accepted update downloads, verifies and installs itself, and the flow exits the app.</summary>
    internal async Task OfferUpdate(UpdateFlowRun run, IntPtr hwnd = default)
    {
        if (run.Release is not { } release) return;

        try { await Updates.InstallAsync(release, hwnd); }
        catch (Exception ex) { AppLog.Error("TrayMenu.OfferUpdate", ex); }
    }

    /// <summary>Runs on the UI thread from wherever the caller is. Never throws.</summary>
    private void RunOnUiThread(Action body, string source)
    {
        try
        {
            Flyout.DispatcherQueue?.TryEnqueue(() =>
            {
                try { body(); }
                catch (Exception ex) { AppLog.Error(source, ex); }
            });
        }
        catch (Exception ex) { AppLog.Error(source, ex); }
    }

    // Apply target state off the UI thread — the task-scheduler write can block for seconds.
    private void ToggleAutoStart(bool enable)
        => Task.Run(() =>
        {
            // No StateChanged here, so the finally re-reads the OS — an unreported failure would
            // silently un-tick the item.
            try
            {
                TaskSchedulerHelper.SetAutoStart(enable);
            }
            catch (Exception ex)
            {
                // Throws when the exe path cannot be resolved.
                AppLog.Error("TrayMenu.ToggleAutoStart", ex);
                NativeMessageBox.Warning(IntPtr.Zero, AppName, $"Could not change 'Launch at startup'.\n\n{ex.Message}");
            }
            finally
            {
                QueueRefresh();   // funnel: mutate → refresh, success or not
            }
        });

    private static T SafeCall<T>(Func<T> fn, T fallback)
    {
        try { return fn(); }
        catch { return fallback; }
    }
}
