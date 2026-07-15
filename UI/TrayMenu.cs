using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using ChargeKeeper.Features;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ZeroZero.Brand.Core;
using ZeroZero.Brand.WinUI;

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
///
/// <para>
/// State model: the menu has ONE read path and ONE apply path. <see cref="ReadState"/>
/// captures every input the menu reflects (the feature toggle states) into a single immutable
/// <see cref="MenuState"/> snapshot; <see cref="ApplyState"/> derives EVERY item's
/// IsChecked/IsEnabled from that snapshot. No command handler updates its own item — each
/// mutates the underlying service/settings and funnels through <see cref="QueueRefresh"/>
/// (directly, or via <see cref="TravelOverrideService.StateChanged"/>), so two items can never
/// disagree about the current state.
/// </para>
///
/// <para>
/// Visual language: every STATEFUL item is a <see cref="ToggleMenuFlyoutItem"/> whose check
/// mark reflects the snapshot (the three feature toggles — Smart Charge, Smart Standby, Launch
/// at startup); every ACTION is a plain text item (Settings…, Check for updates, About…, Exit).
/// No regular item carries an icon — the only emoji is the transient "⬆ Update available" alert
/// badge.
/// </para>
///
/// <para>
/// TODO #19 / #28: this menu used to also carry a 4-level-deep Settings ▸ Network profiles ▸ Add
/// configuration ▸ &lt;preset&gt; tree, plus a Presets submenu, a "Charge to 100 % once" travel
/// override, a "Numeric % icon" toggle, and Open-settings-folder / Reload-settings actions. All
/// of the configuration moved into <c>UI.SettingsWindow</c> (opened via the "Settings…" item),
/// including the Open-folder / Reload entry points (now an Advanced footer there); the travel
/// override lives on the Dashboard. The menu is now flat — quick toggles + a handful of actions,
/// no submenus. Network-location auto-apply (<see cref="OnNetworkLocationChanged"/>) is NOT a
/// menu item and stays wired here regardless — it is a background reaction, not UI.
/// </para>
/// </summary>
internal sealed class TrayMenu
{
    private readonly List<(ToggleMenuFlyoutItem Item, IToggleFeature Feature)> _toggles = [];

    private MenuFlyoutItem?   _updateItem;
    private BrandAboutWindow? _aboutWindow;

    private readonly Action _onIconModeChanged;
    private readonly Action _onExit;
    private readonly Action _onOpenSettings;

    /// <summary>The flyout to assign to <c>TaskbarIcon.ContextFlyout</c>.</summary>
    public MenuFlyout Flyout { get; }

    public TrayMenu(IReadOnlyList<IToggleFeature> features, Action onExit, Action onIconModeChanged,
                    Action onOpenSettings)
    {
        _onExit            = onExit;
        _onIconModeChanged = onIconModeChanged;
        _onOpenSettings    = onOpenSettings;
        Flyout = new MenuFlyout();

        // Builds a toggle for one feature and registers it for the state refresh. The item is added
        // to the flyout at the position its GROUP belongs (power toggles at the top, Launch at
        // startup down in the updates group) rather than all in one top run.
        ToggleMenuFlyoutItem MakeToggle(IToggleFeature feature)
        {
            var item = new ToggleMenuFlyoutItem { Text = feature.Name };
            // Capture current IsChecked at click time to avoid TOCTOU: the target state comes
            // from the item the user just interacted with rather than a fresh OS read.
            item.Command = new RelayCommand(() => Toggle(feature, !item.IsChecked));
            _toggles.Add((item, feature));
            return item;
        }

        // ── Power toggles: Smart Charge + Smart Standby ─────────────────────────
        // Launch-at-startup is a feature too, but it reads as an app-management action rather than a
        // power control, so it lives in the updates group below (TODO #28) — built there, not here.
        IToggleFeature? autoStart = null;
        foreach (var feature in features)
        {
            if (feature is AutoStartFeature)
            {
                autoStart = feature;
                continue;
            }
            Flyout.Items.Add(MakeToggle(feature));
        }

        // ── Settings entry point (TODO #19) ─────────────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Settings…", Command = new RelayCommand(_onOpenSettings) });

        // ── Updates + Launch at startup (TODO #28) ──────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem
        {
            Text    = "Check for updates",
            Command = new RelayCommand(() => _ = CheckForUpdatesAsync()),
        });
        if (autoStart is not null)
            Flyout.Items.Add(MakeToggle(autoStart));

        // ── About ───────────────────────────────────────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "About…", Command = new RelayCommand(() => ShowAbout()) });

        // ── Exit ────────────────────────────────────────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(onExit) });

        // Resync when the override auto-reverts (battery reached full) or is toggled from the
        // dashboard — otherwise the menu wouldn't learn about it until the next right-click.
        // Fires on a background thread; QueueRefresh marshals the apply back to the UI thread.
        // Never unsubscribed: TrayMenu lives for the whole process.
        TravelOverrideService.StateChanged += QueueRefresh;

        // Resync after ANY shared charge-control change — including one driven by an inbound MQTT
        // command (issue #40 item 4). Before this the MQTT path mutated Smart Charge / thresholds /
        // preset without telling the tray, so the menu (and tooltip/dashboard) stayed stale until the
        // next right-click/tick. ChargeControlService now funnels the tray AND MQTT paths, and fires
        // StateChanged after each, so both reconcile identically. Same background-thread + never-
        // unsubscribed reasoning as the TravelOverrideService subscription above.
        ChargeControlService.StateChanged += QueueRefresh;

        // Network-location auto-apply (TODO #31) — fires on a background thread (same as
        // TravelOverrideService.StateChanged above); OnNetworkLocationChanged marshals via
        // ApplyPreset/QueueRefresh, both of which already handle that. Never unsubscribed, same
        // "lives for the whole process" reasoning. Not a menu item (TODO #19 moved the Network
        // profiles submenu into SettingsWindow) — this is a background reaction that stays wired
        // here regardless of what the menu itself shows.
        NetworkLocationService.LocationChanged += OnNetworkLocationChanged;

        // QueueRefresh, not RefreshState: the initial state read (ReadState) does a Lenovo RPC +
        // Task Scheduler COM connect + SCM query + NIC enumeration, and this constructor runs on the
        // UI thread inside InitTrayIcon — BEFORE the tray icon is created. Doing that work
        // synchronously here delayed the icon appearing. QueueRefresh does the read off-thread and
        // marshals ApplyState back, and it's redundant for correctness anyway (RightClickCommand
        // calls RefreshState on every right-click, so the user never sees pre-refresh state).
        QueueRefresh();
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

    /// <summary>
    /// Re-reads live state into every item's check mark / availability, synchronously on the UI
    /// thread. Call right before the menu opens: H.NotifyIcon builds the native popup from the
    /// flyout at right-click time, so the snapshot must be applied before it is shown.
    /// </summary>
    public void RefreshState() => ApplyState(ReadState());

    /// <summary>
    /// Silent resync for a settings change made OUTSIDE the tray menu itself — i.e. from
    /// <c>UI.SettingsWindow</c> (TODO #19). Extracted from the old settings-load path
    /// (Reload keeps its own toast; the Settings window calls this bare — showing a toast on top
    /// of the very window the user is looking at would be noise). Refreshes exactly what a
    /// settings edit can invalidate: the icon-mode callback (in case IconMode changed) and every
    /// item's check marks/availability via <see cref="RefreshState"/>. The tray menu no longer
    /// carries a Presets submenu (TODO #28 moved it fully into the Settings window), so there is
    /// nothing here to rebuild from the edited preset list — only the toggles resync.
    /// </summary>
    public void ReconcileFromExternalChange()
    {
        _onIconModeChanged();
        // QueueRefresh, not RefreshState: ReadState() does a Lenovo RPC + SCM query + Task
        // Scheduler COM connect (see the constructor's own reasoning above), and this method is
        // now called on every Settings-window preset edit/add/delete, not just the rare menu-open
        // or Reload path — calling the synchronous version here would freeze the UI thread on
        // every such edit.
        QueueRefresh();
    }

    /// <summary>
    /// The funnel every state mutation ends in: captures a fresh <see cref="MenuState"/> OFF the
    /// UI thread (the feature reads go through the Lenovo RPC bridge — same off-thread-read
    /// pattern as <c>DashboardWindow.Refresh</c>) and marshals one <see cref="ApplyState"/> back
    /// to the UI thread. Safe to call from any thread. If the native menu is already open the
    /// visible popup won't repaint (it's a snapshot by design), but the flyout is consistent for
    /// the next open even if that open skips <see cref="RefreshState"/>.
    /// </summary>
    private void QueueRefresh() => Task.Run(() =>
    {
        try
        {
            var state = ReadState();
            Flyout.DispatcherQueue?.TryEnqueue(() =>
            {
                // A throw inside a raw dispatcher callback tears the process down as an opaque
                // stowed exception (see App.RunOnUi) — catch and log instead.
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
    /// One immutable snapshot of every input the menu reflects — the single internal state all
    /// items are derived from. <see cref="ReadState"/> is the only producer (may perform RPC —
    /// callable from any thread); <see cref="ApplyState"/> is the only consumer (UI thread only).
    /// Since TODO #28 the menu's only stateful items are the feature toggles, so the snapshot is
    /// just their (available, enabled) reads.
    /// </summary>
    private sealed record MenuState(
        IReadOnlyList<(bool Available, bool Enabled)> Features);   // aligned with _toggles

    private MenuState ReadState()
    {
        var features = new (bool Available, bool Enabled)[_toggles.Count];
        for (int i = 0; i < _toggles.Count; i++)
        {
            var feature = _toggles[i].Feature;
            // One combined read (Smart Charge answers both flags from a single RPC — see
            // SmartChargeFeature.ReadState); "enabled" is meaningful only when available.
            var (available, enabled) = SafeCall(() => feature.ReadState(),
                                                fallback: (Available: true, Enabled: false));
            features[i] = (available, available && enabled);
        }
        return new MenuState(features);
    }

    private void ApplyState(MenuState state)
    {
        for (int i = 0; i < _toggles.Count; i++)
        {
            var (available, enabled) = state.Features[i];
            _toggles[i].Item.IsEnabled = available;
            _toggles[i].Item.IsChecked = enabled;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ApplyPreset(ThresholdPreset preset) => RunApplyPreset(preset.Name);

    /// <summary>
    /// Applies the preset with the given name — the Settings window's network-profile editor calls
    /// this so a profile added/edited for the network you're currently on takes effect immediately
    /// (TODO #19/#22). Delegates to <see cref="RunApplyPreset"/> so the device write + ActivePreset +
    /// reconcile stay in one place; no-op when the name is blank or matches no preset.
    /// </summary>
    public void ApplyPresetByName(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName)) return;
        // Resolve here first so an unknown name is a no-op WITHOUT spinning up a Task (preserves the
        // old contract). The device write + persist + reconcile happen inside RunApplyPreset.
        if (SettingsService.Current.Presets.Any(p => p.Name == presetName))
            RunApplyPreset(presetName);
    }

    /// <summary>
    /// Applies the named preset off the UI thread (the vendor RPC blocks) via the shared
    /// <see cref="ChargeControlService"/> — the SAME composition the MQTT preset command uses (issue
    /// #40 item 4), so the "supersede any in-flight override, write, then persist ActivePreset"
    /// ordering lives in exactly one place. QueueRefresh in the finally is belt-and-suspenders on top
    /// of the ChargeControlService.StateChanged subscription.
    /// </summary>
    private void RunApplyPreset(string name)
        => Task.Run(() =>
        {
            try { ChargeControlService.ApplyPresetByName(name); }
            catch { }
            finally { QueueRefresh(); }
        });

    // ── Network-location auto-apply (TODO #31) ──────────────────────────────────
    // Not a menu item — see the class doc comment and the constructor's subscription. Configuring
    // WHICH rule maps to which preset now happens entirely in UI.SettingsWindow (TODO #19); this
    // stays here because it is the live reaction to NetworkLocationService.LocationChanged, which
    // TrayMenu already owns the lifetime/subscription of.

    /// <summary>
    /// Auto-apply on detected location change (TODO #31). Fires on whatever thread
    /// <see cref="NetworkLocationService.LocationChanged"/> raised on (a debounce-timer thread, not
    /// the UI thread) — <see cref="ApplyPreset"/> already marshals its own UI-thread work via
    /// QueueRefresh, so this method itself needs no explicit marshalling.
    /// </summary>
    private void OnNetworkLocationChanged(NetworkLocation location)
    {
        var s = SettingsService.Current;
        if (s.NetworkProfilesEnabled)
        {
            // A matched rule wins outright; otherwise fall back to the "unknown network" preset —
            // but only when a real (non-empty) location was detected. An empty location (no
            // network at all) isn't "unknown", it's "nothing to react to".
            string? presetName = s.FindNetworkRule(location)?.PresetName
                ?? (!location.IsEmpty ? s.UnknownNetworkPresetName : null);
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

    // ── About / updates ─────────────────────────────────────────────────────

    private const string AppName = AppInfo.Name;

    private void ShowAbout()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        var options = new BrandAboutOptions
        {
            Info = new AboutInfo
            {
                AppName     = AppName,
                Version     = AppInfo.Version,
                Description = "Keeps your laptop battery healthy — charge limits, a live battery gauge and smart standby control from the system tray. Runs on ThinkPads today (requires the Lenovo Power Management Driver).",
                RepoUrl     = "https://github.com/0z00z0/ChargeKeeper",
                // Keep this list in sync with the README's "External libraries" table (memory
                // preference) — same non-Microsoft NuGet dependencies.
                ExternalLibraries =
                [
                    new ExternalLibrary("H.NotifyIcon.WinUI", "HavenDV", "System-tray icon + native context menu for WinUI 3", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
                    new ExternalLibrary("TaskScheduler", "David Hall", "Managed wrapper over the Windows Task Scheduler API (auto-start)", "MIT", "https://github.com/dahall/TaskScheduler"),
                    new ExternalLibrary("CommunityToolkit.WinUI.Controls.RangeSelector", ".NET Foundation", "Dual-handle range slider (Smart Charge start/stop threshold)", "MIT", "https://github.com/CommunityToolkit/Windows"),
                    new ExternalLibrary("CommunityToolkit.WinUI.Controls.SettingsControls", ".NET Foundation", "SettingsCard/SettingsExpander rows (Settings window)", "MIT", "https://github.com/CommunityToolkit/Windows"),
                    new ExternalLibrary("WinUIEx", "Morten Nielsen", "WinUI 3 window helper extensions (Settings window placement)", "MIT", "https://github.com/dotMorten/WinUIEx"),
                    new ExternalLibrary("MQTTnet", "The MQTTnet Project", "MQTT client for the Home Assistant integration", "MIT", "https://github.com/dotnet/MQTTnet"),
                ],
            },
            // Reuses this class's own CheckForUpdatesAsync (below) rather than duplicating a second
            // near-identical copy of the old AboutWindow.xaml.cs update-check block: both versions
            // differed only in how they captured the parent HWND (AppWindow.Id of the About window
            // itself vs NativeMethods.CaptureHwnd()), and BrandAboutWindow doesn't expose its own
            // HWND — so CaptureHwnd() is required here either way.
            //
            // CheckForUpdatesAsync always returns false (it never asks the window to drive the exit):
            // when an update is chosen it shows a "Downloading…" prompt and kicks off a *background*
            // installer download, then ChargeKeeper terminates itself via _onExit() once that
            // completes (see below). Because the window is never told an update was applied, its
            // OnBeforeExit teardown hook is never invoked — so it's left null here.
            OnCheckForUpdates = CheckForUpdatesAsync,
        };

        _aboutWindow = new BrandAboutWindow(options);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Activate();
    }

    // Returns true only if the caller (the shared About window) should now drive an app exit for an
    // installer relaunch. ChargeKeeper always returns false: it runs the installer download on a
    // background task and terminates itself via _onExit() when that finishes, so it never needs the
    // window to co-ordinate the exit. See the ShowAbout wiring comment.
    private async Task<bool> CheckForUpdatesAsync()
    {
        // Capture the foreground HWND now (tray flyout is open) so ShowUpdateDialog has a
        // parent even if the flyout is dismissed by the time the HTTP check completes.
        // Do NOT use ConfigureAwait(false) here: TaskDialogIndirect requires comctl32 v6
        // (activated via the app manifest's SxS context), and thread-pool threads do not
        // inherit that context — the dialog shows nothing if called from a thread-pool thread.
        var hwnd    = NativeMethods.CaptureHwnd();
        var outcome = await UpdateCheckService.CheckNowAsync();
        var running = AppInfo.Version;

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
                                Flyout.DispatcherQueue?.TryEnqueue(() => _onExit());
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

        // ChargeKeeper self-drives any update-triggered exit (background download → _onExit), so the
        // window is never asked to close the app on our behalf.
        return false;
    }

    // Apply target state off the UI thread — RPC/service writes can block for seconds.
    private void Toggle(IToggleFeature feature, bool enable)
        => Task.Run(() =>
        {
            try
            {
                // Smart Charge funnels through the shared ChargeControlService — the SAME composition
                // the MQTT smart_charge switch uses (issue #40 item 4), so the load-bearing
                // "re-enable mid-override → cancel the override (restore saved thresholds + disarm
                // the auto-revert), not a bare SetEnabled(true) that would apply firmware's 0/0
                // defaults and leave the revert armed" rule lives in exactly one place. Other
                // features (Smart Standby, Launch at startup) are a plain enable/disable.
                if (feature is SmartChargeFeature)
                {
                    ChargeControlService.SetSmartChargeEnabled(enable);   // fires StateChanged → QueueRefresh
                    return;
                }

                bool ok = feature.SetEnabled(enable);
                if (!ok)
                    System.Diagnostics.Debug.WriteLine($"[TrayMenu] Toggle '{feature.Name}' → {enable} returned false");
            }
            catch (Exception ex)
            {
                // AutoStartFeature can throw InvalidOperationException when exe path can't be resolved.
                System.Diagnostics.Debug.WriteLine($"[TrayMenu] Toggle '{feature.Name}' failed: {ex.Message}");
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
