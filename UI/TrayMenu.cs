using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using ChargeKeeper.Features;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// Owns the tray icon's right-click context menu.
///
/// H.NotifyIcon builds a native Win32 popup menu from this <see cref="Flyout"/> on every
/// right-click and invokes each item's <c>Command</c> (the XAML <c>Click</c> and
/// <c>Opening</c> events do NOT fire for the native menu).  Items are therefore created once
/// with command bindings; <see cref="RefreshState"/> resyncs the check marks before the menu
/// is shown.  Each toggle is generated from an <see cref="IToggleFeature"/>, so adding a new
/// feature is a one-line change at the call site.
/// </summary>
internal sealed class TrayMenu
{
    private readonly List<(ToggleMenuFlyoutItem Item, IToggleFeature Feature)> _toggles = [];
    private readonly List<(ToggleMenuFlyoutItem Item, ThresholdPreset Preset)> _presetItems = [];

    private MenuFlyoutItem?       _updateItem;
    private MenuFlyoutItem?       _travelItem;
    private ToggleMenuFlyoutItem? _iconModeItem;
    private MenuFlyoutSubItem?    _presetsSubmenu;
    private AboutWindow?          _aboutWindow;

    // Settings submenu state (radio-style items synced in RefreshState).
    private ToggleMenuFlyoutItem? _lowBattEnabledItem;
    private readonly List<(ToggleMenuFlyoutItem Item, int Pct)>     _lowBattPctItems    = [];
    private readonly List<(ToggleMenuFlyoutItem Item, int Seconds)> _startupDelayItems  = [];

    private readonly Action _onIconModeChanged;
    private readonly Action _onExit;

    /// <summary>The flyout to assign to <c>TaskbarIcon.ContextFlyout</c>.</summary>
    public MenuFlyout Flyout { get; }

    public TrayMenu(IReadOnlyList<IToggleFeature> features, Action onExit, Action onIconModeChanged)
    {
        _onExit            = onExit;
        _onIconModeChanged = onIconModeChanged;
        Flyout = new MenuFlyout();

        foreach (var feature in features)
        {
            var item = new ToggleMenuFlyoutItem { Text = feature.Name };
            // Capture current IsChecked at click time to avoid TOCTOU: the target state comes
            // from the item the user just interacted with rather than a fresh OS read.
            item.Command = new RelayCommand(() => Toggle(feature, !item.IsChecked));
            _toggles.Add((item, feature));
            Flyout.Items.Add(item);

            // Append preset submenu + travel override directly under Smart Charge.
            if (feature is SmartChargeFeature)
            {
                _presetsSubmenu = BuildPresetsSubmenu();
                Flyout.Items.Add(_presetsSubmenu);

                _travelItem = new MenuFlyoutItem
                {
                    Text    = TravelOverrideService.ActionLabel,
                    Command = new RelayCommand(OnTravelOverride),
                };
                Flyout.Items.Add(_travelItem);
            }
        }

        // ── Settings submenu ─────────────────────────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());

        var settingsMenu = new MenuFlyoutSubItem { Text = "Settings" };

        _iconModeItem = new ToggleMenuFlyoutItem
        {
            Text      = "Numeric % icon",
            IsChecked = SettingsService.Current.IconMode == TrayIconMode.Numeric,
            Command   = new RelayCommand(ToggleIconMode),
        };
        settingsMenu.Items.Add(_iconModeItem);
        settingsMenu.Items.Add(BuildLowBatteryMenu());
        settingsMenu.Items.Add(BuildStartupDelayMenu());
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Export settings…", Command = new RelayCommand(ExportSettings) });
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Import settings…", Command = new RelayCommand(ImportSettings) });
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Open settings file", Command = new RelayCommand(OpenSettingsFile) });
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Reload settings from disk", Command = new RelayCommand(ReloadSettings) });
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(new MenuFlyoutItem
        {
            Text    = "Check for updates",
            Command = new RelayCommand(() => _ = CheckForUpdatesAsync()),
        });
        Flyout.Items.Add(settingsMenu);

        // ── About / Exit ──────────────────────────────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "About…", Command = new RelayCommand(() => ShowAbout()) });
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(onExit) });

        RefreshState();
    }

    /// <summary>
    /// Inserts (or updates) an "Update available" item at the top of the menu.
    /// Safe to call more than once — subsequent calls update the existing item.
    /// </summary>
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
            Command = new RelayCommand(() => _ = CheckForUpdatesAsync()),
        };

        // Insert before the first toggle item so it's always at the top.
        Flyout.Items.Insert(0, _updateItem);
        Flyout.Items.Insert(1, new MenuFlyoutSeparator());
    }

    /// <summary>Re-reads live state into the toggle check marks and availability. Call right before the menu opens.</summary>
    public void RefreshState()
    {
        foreach (var (item, feature) in _toggles)
        {
            item.IsEnabled = SafeCall(() => feature.IsAvailable, fallback: true);
            item.IsChecked = item.IsEnabled && SafeCall(() => feature.IsEnabled, fallback: false);
        }

        // Preset check marks: highlight whichever preset is active (null = custom / none).
        string? active = SettingsService.Current.ActivePreset;
        foreach (var (item, preset) in _presetItems)
            item.IsChecked = preset.Name == active;

        // Travel override label.
        if (_travelItem is not null)
            _travelItem.Text = TravelOverrideService.ActionLabel;

        // Icon mode toggle.
        if (_iconModeItem is not null)
            _iconModeItem.IsChecked = SettingsService.Current.IconMode == TrayIconMode.Numeric;

        // Settings submenu radio-style items.
        var s = SettingsService.Current;
        if (_lowBattEnabledItem is not null)
            _lowBattEnabledItem.IsChecked = s.LowBatteryWarningEnabled;
        foreach (var (item, pct) in _lowBattPctItems)
            item.IsChecked = pct == s.LowBatteryWarningPct;
        foreach (var (item, secs) in _startupDelayItems)
            item.IsChecked = secs == s.StartupDelaySeconds;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private MenuFlyoutSubItem BuildPresetsSubmenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Presets" };
        AddPresetItems(sub);
        return sub;
    }

    /// <summary>
    /// Clears and re-adds the Presets submenu's items from the (possibly just-reloaded/imported)
    /// live settings. <see cref="RefreshState"/> alone is NOT enough for this submenu: it only
    /// toggles IsChecked on the EXISTING items, which still close over the ThresholdPreset objects
    /// captured when the menu was built — after an out-of-band edit to settings.json (the exact
    /// scenario "Reload settings from disk" exists for), those closures would keep applying the
    /// stale, pre-edit Start/Stop values. Call after any operation that can replace
    /// <see cref="SettingsService.Current"/> wholesale (<see cref="SettingsService.Reload"/>,
    /// <see cref="SettingsService.Import"/>).
    /// </summary>
    private void RebuildPresetsSubmenu()
    {
        if (_presetsSubmenu is null) return;
        _presetItems.Clear();
        _presetsSubmenu.Items.Clear();
        AddPresetItems(_presetsSubmenu);
    }

    private void AddPresetItems(MenuFlyoutSubItem sub)
    {
        foreach (var preset in SettingsService.Current.Presets)
        {
            var p    = preset; // local copy for lambda capture
            var item = new ToggleMenuFlyoutItem
            {
                Text      = $"{p.Name}  ({p.Start}–{p.Stop} %)",
                IsChecked = p.Name == SettingsService.Current.ActivePreset,
            };
            item.Command = new RelayCommand(() => ApplyPreset(p));
            _presetItems.Add((item, p));
            sub.Items.Add(item);
        }
    }

    private static void ApplyPreset(ThresholdPreset preset)
        => Task.Run(() =>
        {
            try
            {
                // Enable Smart Charge then apply the preset thresholds.
                ChargeThresholdService.SetEnabled(true);
                bool ok = ChargeThresholdService.SetThresholds(preset.Start, preset.Stop);
                if (ok)
                    // Update() (not "Current.ActivePreset = x; Save();") — this spans the RPC call
                    // above, so a concurrent "Reload settings from disk" could otherwise swap
                    // Current out from under a plain captured-reference mutation and silently lose
                    // this write (see SettingsService.Update's doc comment).
                    SettingsService.Update(s => s.ActivePreset = preset.Name);
            }
            catch { }
        });

    private static void OnTravelOverride()
    {
        if (TravelOverrideService.IsActive)
            TravelOverrideService.Cancel();
        else
            TravelOverrideService.Activate();
    }

    private void ToggleIconMode()
    {
        var s = SettingsService.Current;
        s.IconMode = s.IconMode == TrayIconMode.Arc ? TrayIconMode.Numeric : TrayIconMode.Arc;
        SettingsService.Save();
        _onIconModeChanged();
    }

    // ── Settings submenus (low-battery warning, startup delay, export/import) ──

    private MenuFlyoutSubItem BuildLowBatteryMenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Low battery warning" };

        _lowBattEnabledItem = new ToggleMenuFlyoutItem
        {
            Text      = "Enabled",
            IsChecked = SettingsService.Current.LowBatteryWarningEnabled,
            Command   = new RelayCommand(ToggleLowBatteryEnabled),
        };
        sub.Items.Add(_lowBattEnabledItem);
        sub.Items.Add(new MenuFlyoutSeparator());

        foreach (var pct in new[] { 10, 15, 20, 25, 30 })
        {
            var p    = pct; // capture
            var item = new ToggleMenuFlyoutItem
            {
                Text      = $"Warn at {p}%",
                IsChecked = SettingsService.Current.LowBatteryWarningPct == p,
                Command   = new RelayCommand(() => SetLowBatteryPct(p)),
            };
            _lowBattPctItems.Add((item, p));
            sub.Items.Add(item);
        }
        return sub;
    }

    private MenuFlyoutSubItem BuildStartupDelayMenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Startup delay" };
        foreach (var (label, secs) in new[] { ("Off", 0), ("5 seconds", 5), ("10 seconds", 10), ("30 seconds", 30), ("60 seconds", 60) })
        {
            var s    = secs; // capture
            var item = new ToggleMenuFlyoutItem
            {
                Text      = label,
                IsChecked = SettingsService.Current.StartupDelaySeconds == s,
                Command   = new RelayCommand(() => SetStartupDelay(s)),
            };
            _startupDelayItems.Add((item, s));
            sub.Items.Add(item);
        }
        return sub;
    }

    private static void ToggleLowBatteryEnabled()
    {
        SettingsService.Current.LowBatteryWarningEnabled = !SettingsService.Current.LowBatteryWarningEnabled;
        SettingsService.Save();
    }

    private static void SetLowBatteryPct(int pct)
    {
        SettingsService.Current.LowBatteryWarningPct     = pct;
        SettingsService.Current.LowBatteryWarningEnabled = true; // choosing a level implies "on"
        SettingsService.Save();
    }

    private static void SetStartupDelay(int seconds)
    {
        SettingsService.Current.StartupDelaySeconds = seconds;
        SettingsService.Save();
    }

    private void ExportSettings()
    {
        // No owner window needed — the tray menu has no HWND; Win32 dialogs accept NULL owner.
        var path = NativeMethods.ShowSaveFileDialog(IntPtr.Zero, "Export ChargeKeeper settings",
            "ChargeKeeper-settings.json", "json",
            "Settings JSON (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            SettingsService.Export(path);
            NativeMethods.Info("Settings exported.", AppName);
        }
        catch (Exception ex)
        {
            NativeMethods.Warn($"Export failed:\n{ex.Message}", AppName);
        }
    }

    /// <summary>
    /// Re-reads settings.json from disk without restarting the app (e.g. after a manual edit, or a
    /// file synced in from another machine) — same purpose as HyperVManagerTray's "Reload config
    /// from disk", but read-only against the canonical file rather than adopting an arbitrary path
    /// like <see cref="ImportSettings"/> does.
    /// </summary>
    private void ReloadSettings() =>
        ApplySettingsLoadResult(SettingsService.Reload(),
            "Settings reloaded from disk.",
            "Could not reload settings — the file is missing or invalid.");

    private void ImportSettings()
    {
        var path = NativeMethods.ShowOpenFileDialog(IntPtr.Zero, "Import ChargeKeeper settings",
            "json", "Settings JSON (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;

        ApplySettingsLoadResult(SettingsService.Import(path),
            "Settings imported.",
            "Could not import settings — the file is missing or invalid.");
    }

    /// <summary>
    /// Shared success/failure handling for the two commands that can replace
    /// <see cref="SettingsService.Current"/> wholesale (Reload, Import): on success, refreshes
    /// everything built from a one-time snapshot of the old settings — icon mode, the menu's
    /// check marks, AND the Presets submenu (which <see cref="RefreshState"/> alone does not
    /// rebuild, see <see cref="RebuildPresetsSubmenu"/>) — then confirms via a toast either way.
    /// </summary>
    private void ApplySettingsLoadResult(bool ok, string successMessage, string failureMessage)
    {
        if (ok)
        {
            _onIconModeChanged();     // icon mode takes effect immediately
            RebuildPresetsSubmenu();  // stale closures over the old Presets list, not just stale IsChecked
            RefreshState();           // resync the remaining menu check marks
            NativeMethods.Info(successMessage, AppName);
        }
        else
        {
            NativeMethods.Warn(failureMessage, AppName);
        }
    }

    // ── About / updates ─────────────────────────────────────────────────────

    private const string AppName = "ChargeKeeper";

    private void ShowAbout()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }
        _aboutWindow = new AboutWindow(_onExit);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Activate();
    }

    private async Task CheckForUpdatesAsync()
    {
        // Capture the foreground HWND now (tray flyout is open) so ShowUpdateDialog has a
        // parent even if the flyout is dismissed by the time the HTTP check completes.
        // Do NOT use ConfigureAwait(false) here: TaskDialogIndirect requires comctl32 v6
        // (activated via the app manifest's SxS context), and thread-pool threads do not
        // inherit that context — the dialog shows nothing if called from a thread-pool thread.
        var hwnd    = NativeMethods.CaptureHwnd();
        var outcome = await UpdateCheckService.CheckNowAsync();
        var running = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        switch (outcome.Status)
        {
            case UpdateCheckService.UpdateStatus.Available:
                bool canDownload = outcome.InstallerUrl is not null;
                var action = NativeMethods.ShowUpdateDialog(
                    outcome.LatestVersion!, running,
                    outcome.ReleaseNotes ?? "", AppName,
                    canDownload, hwnd);

                switch (action)
                {
                    case NativeMethods.UpdateAction.Update:
                        NativeMethods.Info(
                            $"Downloading v{outcome.LatestVersion}...\n\nThe installer will launch automatically when ready.",
                            AppName);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var path = await UpdateCheckService
                                    .DownloadInstallerAsync(outcome.InstallerUrl!)
                                    .ConfigureAwait(false);
                                // Launch installer, then exit so no elevated process remains for
                                // the installer to kill (which would need its own UAC prompt).
                                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                                Flyout.DispatcherQueue.TryEnqueue(() => _onExit());
                            }
                            catch (Exception ex)
                            {
                                NativeMethods.Warn(
                                    $"Download failed:\n{ex.Message}\n\nTry updating from the releases page.",
                                    AppName);
                                Process.Start(new ProcessStartInfo(outcome.ReleaseUrl) { UseShellExecute = true });
                            }
                        });
                        break;

                    case NativeMethods.UpdateAction.ShowReleases:
                        Process.Start(new ProcessStartInfo(outcome.ReleaseUrl) { UseShellExecute = true });
                        break;
                }
                break;

            case UpdateCheckService.UpdateStatus.UpToDate:
                NativeMethods.Info($"You're on the latest version (v{running}).", AppName);
                break;

            case UpdateCheckService.UpdateStatus.NoReleases:
                NativeMethods.Info("No releases have been published yet.", AppName);
                break;

            default:
                NativeMethods.Warn("Could not check for updates.\nCheck your internet connection.", AppName);
                break;
        }
    }

    private static void OpenSettingsFile()
    {
        // Open Explorer with the settings file selected so the user can find, copy, or edit it.
        var filePath = SettingsService.FilePath;
        var dir      = Path.GetDirectoryName(filePath) ?? filePath;

        // If the file exists, select it; otherwise just open the folder.
        var args = File.Exists(filePath) ? $"/select,\"{filePath}\"" : $"\"{dir}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = args,
            UseShellExecute = true,
        });
    }

    // Apply target state off the UI thread — RPC/service writes can block for seconds.
    private static void Toggle(IToggleFeature feature, bool enable)
        => Task.Run(() =>
        {
            try
            {
                bool ok = feature.SetEnabled(enable);
                if (!ok)
                    System.Diagnostics.Debug.WriteLine($"[TrayMenu] Toggle '{feature.Name}' → {enable} returned false");
            }
            catch (Exception ex)
            {
                // AutoStartFeature can throw InvalidOperationException when exe path can't be resolved.
                System.Diagnostics.Debug.WriteLine($"[TrayMenu] Toggle '{feature.Name}' failed: {ex.Message}");
            }
        });

    private static T SafeCall<T>(Func<T> fn, T fallback)
    {
        try { return fn(); }
        catch { return fallback; }
    }
}
