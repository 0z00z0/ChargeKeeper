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
/// captures every input the menu reflects (feature states, settings, travel override) into a
/// single immutable <see cref="MenuState"/> snapshot; <see cref="ApplyState"/> derives EVERY
/// item's IsChecked/IsEnabled from that snapshot. No command handler updates its own item —
/// each mutates the underlying service/settings and funnels through <see cref="QueueRefresh"/>
/// (directly, or via <see cref="TravelOverrideService.StateChanged"/>), so two items can never
/// disagree about the current state.
/// </para>
///
/// <para>
/// Visual language: every STATEFUL item is a <see cref="ToggleMenuFlyoutItem"/> whose check
/// mark reflects the snapshot (features, presets, travel override, icon mode); every ACTION is
/// a plain text item (Settings…, About…, Exit). No regular item carries an icon — the only
/// emoji is the transient "⬆ Update available" alert badge.
/// </para>
///
/// <para>
/// TODO #19: this menu used to also carry a 4-level-deep Settings ▸ Network profiles ▸ Add
/// configuration ▸ &lt;preset&gt; tree covering every configurable setting (low-battery %,
/// startup delay, downtime gap, drain-anomaly %, network profiles/rules, Home Assistant/MQTT).
/// All of that moved into <c>UI.SettingsWindow</c> (opened via the "Settings…" item below), so
/// this menu now carries only glanceable status + quick toggles, at most one level deep
/// (Presets ▸). Network-location auto-apply (<see cref="OnNetworkLocationChanged"/>) is NOT a
/// menu item and stays wired here regardless — it is a background reaction, not UI.
/// </para>
/// </summary>
internal sealed class TrayMenu
{
    private readonly List<(ToggleMenuFlyoutItem Item, IToggleFeature Feature)> _toggles = [];
    private readonly List<(ToggleMenuFlyoutItem Item, ThresholdPreset Preset)> _presetItems = [];

    // Index of the Smart Charge toggle in _toggles (-1 if absent). Its state gates the Presets
    // submenu and the travel-override item; caching the index lets those read from the same
    // snapshot slot as the toggle itself, so the gate can never disagree with the check mark.
    private readonly int _smartChargeIndex = -1;

    private MenuFlyoutItem?       _updateItem;
    private ToggleMenuFlyoutItem? _travelItem;
    private ToggleMenuFlyoutItem? _iconModeItem;
    private MenuFlyoutSubItem?    _presetsSubmenu;
    private BrandAboutWindow?     _aboutWindow;

    private readonly Action _onIconModeChanged;
    private readonly Action _onExit;
    private readonly Action _onOpenSettings;

    /// <summary>The flyout to assign to <c>TaskbarIcon.ContextFlyout</c>.</summary>
    public MenuFlyout Flyout { get; }

    /// <summary>
    /// Set by <c>App</c> to <c>() =&gt; _settings?.RefreshAllSections()</c> — lets an external
    /// settings replacement (Reload/Import, from <see cref="ApplySettingsLoadResult"/>) reach an
    /// already-open Settings window. Without this hook the window's own doc comment claim (that it
    /// resyncs after a background reload) would be false: nothing else can see the window instance,
    /// since the reference only runs the other way (Settings window holds a TrayMenu, not vice
    /// versa). Property rather than a constructor parameter so it can be wired after both objects
    /// exist, and null (no-op) whenever the window isn't currently open.
    /// </summary>
    public Action? OnExternalReload { get; set; }

    public TrayMenu(IReadOnlyList<IToggleFeature> features, Action onExit, Action onIconModeChanged,
                    Action onOpenSettings)
    {
        _onExit            = onExit;
        _onIconModeChanged = onIconModeChanged;
        _onOpenSettings    = onOpenSettings;
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
                _smartChargeIndex = _toggles.Count - 1;   // the toggle just added above
                _presetsSubmenu = BuildPresetsSubmenu();
                Flyout.Items.Add(_presetsSubmenu);

                // Constant caption + check-mark-while-active: stateful items all speak the same
                // visual language, and no regular item carries an emoji icon. (The dashboard's
                // plain Button keeps TravelOverrideService.ActionLabel — a button's caption must
                // say what clicking does; a toggle's check mark already does.)
                _travelItem = new ToggleMenuFlyoutItem
                {
                    Text    = "Charge to 100 % once",
                    Command = new RelayCommand(OnTravelOverride),
                };
                Flyout.Items.Add(_travelItem);
            }
        }

        // ── Quick toggles + Settings entry point (TODO #19) ─────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());

        _iconModeItem = new ToggleMenuFlyoutItem
        {
            Text    = "Numeric % icon",
            Command = new RelayCommand(ToggleIconMode),
        };
        Flyout.Items.Add(_iconModeItem);

        Flyout.Items.Add(new MenuFlyoutItem { Text = "Settings…", Command = new RelayCommand(_onOpenSettings) });
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Open settings folder", Command = new RelayCommand(OpenSettingsFile) });
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Reload settings from file", Command = new RelayCommand(ReloadSettings) });

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem
        {
            Text    = "Check for updates",
            Command = new RelayCommand(() => _ = CheckForUpdatesAsync()),
        });

        // ── About / Exit ──────────────────────────────────────────────────────
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "About…", Command = new RelayCommand(() => ShowAbout()) });
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(onExit) });

        // Resync when the override auto-reverts (battery reached full) or is toggled from the
        // dashboard — otherwise the menu wouldn't learn about it until the next right-click.
        // Fires on a background thread; QueueRefresh marshals the apply back to the UI thread.
        // Never unsubscribed: TrayMenu lives for the whole process.
        TravelOverrideService.StateChanged += QueueRefresh;

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
    /// <c>UI.SettingsWindow</c> (TODO #19). Extracted from <see cref="ApplySettingsLoadResult"/>
    /// (Reload keeps its own toast; the Settings window calls this bare — showing a toast on top
    /// of the very window the user is looking at would be noise). Refreshes exactly what a
    /// settings edit can invalidate: the icon-mode callback (in case IconMode changed), the
    /// Presets submenu (its items close over <see cref="ThresholdPreset"/> objects, which a
    /// rename/delete/add can replace wholesale — see <see cref="RebuildPresetsSubmenu"/>), and
    /// every other item's check marks/availability via <see cref="RefreshState"/>.
    /// </summary>
    public void ReconcileFromExternalChange()
    {
        _onIconModeChanged();
        RebuildPresetsSubmenu();
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
    /// </summary>
    private sealed record MenuState(
        IReadOnlyList<(bool Available, bool Enabled)> Features,   // aligned with _toggles
        string? ActivePreset,
        bool    TravelOverrideActive,
        bool    NumericIcon);

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

        var s = SettingsService.Current;
        return new MenuState(
            features,
            s.ActivePreset,
            TravelOverrideService.IsActive,
            s.IconMode == TrayIconMode.Numeric);
    }

    private void ApplyState(MenuState state)
    {
        for (int i = 0; i < _toggles.Count; i++)
        {
            var (available, enabled) = state.Features[i];
            _toggles[i].Item.IsEnabled = available;
            _toggles[i].Item.IsChecked = enabled;
        }

        // Smart Charge's own snapshot slot gates the presets + travel override, so those can never
        // disagree with its toggle. (false,false) when there is no Smart Charge feature.
        var (scAvailable, scEnabled) = _smartChargeIndex >= 0
            ? state.Features[_smartChargeIndex]
            : (Available: false, Enabled: false);

        // A preset shows checked only while its thresholds are actually in effect: Smart Charge
        // on and no travel override lifting them. Activating the override deliberately KEEPS
        // ActivePreset in settings (the auto-revert restores that preset's thresholds), but the
        // menu must never show a checked preset next to a checked "Charge to 100 % once".
        foreach (var (item, preset) in _presetItems)
            item.IsChecked = scEnabled &&
                             !state.TravelOverrideActive &&
                             preset.Name == state.ActivePreset;
        if (_presetsSubmenu is not null)
            _presetsSubmenu.IsEnabled = scAvailable;

        // Travel override: checked while a "charge to 100 % once" is in progress; greyed out on
        // hardware without threshold support (mirrors the dashboard hiding its button).
        if (_travelItem is not null)
        {
            _travelItem.IsEnabled = scAvailable;
            _travelItem.IsChecked = state.TravelOverrideActive;
        }

        if (_iconModeItem is not null)
            _iconModeItem.IsChecked = state.NumericIcon;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private MenuFlyoutSubItem BuildPresetsSubmenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "Presets" };
        AddPresetItems(sub);
        return sub;
    }

    /// <summary>
    /// Clears and re-adds the Presets submenu's items from the (possibly just-edited/reloaded)
    /// live settings. <see cref="RefreshState"/> alone is NOT enough for this submenu: it only
    /// toggles IsChecked on the EXISTING items, which still close over the ThresholdPreset objects
    /// captured when the menu was built — after an out-of-band edit to settings.json, or an edit
    /// made in <c>UI.SettingsWindow</c> (TODO #19), those closures would keep applying the stale
    /// pre-edit Start/Stop values. Call after any operation that can replace the live Presets list
    /// wholesale (<see cref="SettingsService.Reload"/> via <see cref="ApplySettingsLoadResult"/>,
    /// or any Settings-window commit via <see cref="ReconcileFromExternalChange"/>).
    /// </summary>
    private void RebuildPresetsSubmenu()
    {
        if (_presetsSubmenu is null) return;
        _presetItems.Clear();
        _presetsSubmenu.Items.Clear();
        AddPresetItems(_presetsSubmenu);
    }

    // Single source for how a preset renders as a menu label, so the Presets submenu never drifts
    // from the format the Settings window shows for the same preset (TODO #19).
    private static string PresetLabel(ThresholdPreset p) => $"{p.Name}  ({p.Start}–{p.Stop} %)";

    private void AddPresetItems(MenuFlyoutSubItem sub)
    {
        foreach (var preset in SettingsService.Current.Presets)
        {
            var p    = preset; // local copy for lambda capture
            var item = new ToggleMenuFlyoutItem { Text = PresetLabel(p) };
            item.Command = new RelayCommand(() => ApplyPreset(p));
            _presetItems.Add((item, p));
            sub.Items.Add(item);
        }
    }

    private void ApplyPreset(ThresholdPreset preset)
        => Task.Run(() =>
        {
            try
            {
                // A preset IS an explicit threshold choice — it supersedes an in-flight
                // "charge to 100 % once" override. Clear the override state FIRST and WITHOUT
                // reverting (Deactivate, not Cancel): the preset thresholds below are the new
                // truth, and an armed auto-revert would otherwise clobber them with the
                // pre-override values as soon as the battery next reports full.
                TravelOverrideService.Deactivate();

                // Writing valid non-zero thresholds IS how the interface enables Smart Charge, so
                // SetThresholds alone both enables and sets. A preceding SetEnabled(true) would only
                // do a throwaway write of default/old values that this call immediately overwrites —
                // and briefly commit those wrong thresholds to firmware in between.
                bool ok = ChargeThresholdService.SetThresholds(preset.Start, preset.Stop);
                if (ok)
                    // Update() (not "Current.ActivePreset = x; Save();") — this spans the RPC call
                    // above, so a concurrent "Reload settings from disk" could otherwise swap
                    // Current out from under a plain captured-reference mutation and silently lose
                    // this write (see SettingsService.Update's doc comment).
                    SettingsService.Update(s => s.ActivePreset = preset.Name);
            }
            catch { }
            finally { QueueRefresh(); }
        });

    // No explicit QueueRefresh here: Activate/Cancel settle asynchronously and fire
    // StateChanged when done — which is subscribed to QueueRefresh in the constructor.
    // Refreshing before they settle would only capture the not-yet-changed state.
    private static void OnTravelOverride()
    {
        if (TravelOverrideService.IsActive)
            TravelOverrideService.Cancel();
        else
            TravelOverrideService.Activate();
    }

    private void ToggleIconMode()
    {
        SettingsService.Update(s =>
            s.IconMode = s.IconMode == TrayIconMode.Arc ? TrayIconMode.Numeric : TrayIconMode.Arc);
        _onIconModeChanged();
        QueueRefresh();
    }

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

    /// <summary>
    /// Re-reads settings.json from disk without restarting the app (e.g. after a manual edit, or a
    /// file synced in from another machine) — same purpose as HyperVManagerTray's "Reload config
    /// from disk", but read-only against the canonical file rather than adopting an arbitrary path.
    /// </summary>
    private void ReloadSettings() =>
        ApplySettingsLoadResult(SettingsService.Reload(),
            "Settings reloaded from disk.",
            "Could not reload settings — the file is missing or invalid.");

    /// <summary>
    /// Success/failure handling for <see cref="ReloadSettings"/>, the one remaining command that
    /// can replace <see cref="SettingsService.Current"/> wholesale (TODO #19 removed the other,
    /// Import): on success, delegates to <see cref="ReconcileFromExternalChange"/> for the silent
    /// resync, then confirms via a toast either way — unlike the Settings window's own commits,
    /// Reload has no "same window" to make a toast redundant.
    /// </summary>
    private void ApplySettingsLoadResult(bool ok, string successMessage, string failureMessage)
    {
        if (ok)
        {
            ReconcileFromExternalChange();
            OnExternalReload?.Invoke();   // resync an already-open Settings window, if any
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
    private void Toggle(IToggleFeature feature, bool enable)
        => Task.Run(() =>
        {
            try
            {
                // Re-enabling Smart Charge while "charge to 100 % once" is running means
                // "threshold back on" — which is exactly the override's cancel path: it restores
                // the saved pre-override thresholds AND disarms the auto-revert. A bare
                // SetEnabled(true) would instead apply DEFAULT thresholds (activating the
                // override wiped the firmware values to 0/0) and leave the auto-revert armed to
                // clobber them at the next full charge.
                if (feature is SmartChargeFeature && enable && TravelOverrideService.IsActive)
                {
                    TravelOverrideService.Cancel();   // fires StateChanged → QueueRefresh
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
