using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using ChargeKeeper.Features;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// Owns the tray icon's right-click context menu — a flat list of quick toggles and actions.
/// H.NotifyIcon rebuilds a native Win32 popup from the flyout on every right-click and invokes only
/// each item's <c>Command</c>; the XAML <c>Click</c> and <c>Opening</c> events never fire. Items are
/// built once with command bindings, and every mutation funnels through <see cref="QueueRefresh"/>.
/// </summary>
internal sealed class TrayMenu
{
    private readonly List<(ToggleMenuFlyoutItem Item, IToggleFeature Feature)> _toggles = [];

    private MenuFlyoutItem? _updateItem;
    private AboutWindow?    _aboutWindow;
    private WhatsNewWindow? _whatsNewWindow;

    private readonly Action _onIconModeChanged;
    private readonly Action _onExit;
    private readonly Action _onOpenSettings;

    // App's window-creation gate. About is the one window this class creates, so it must wait on it.
    private readonly Task _windowsReady;

    /// <summary>The flyout to assign to <c>TaskbarIcon.ContextFlyout</c>.</summary>
    public MenuFlyout Flyout { get; }

    public TrayMenu(IReadOnlyList<IToggleFeature> features, Action onExit, Action onIconModeChanged,
                    Action onOpenSettings, Task windowsReady)
    {
        _onExit            = onExit;
        _onIconModeChanged = onIconModeChanged;
        _onOpenSettings    = onOpenSettings;
        _windowsReady      = windowsReady;
        Flyout = new MenuFlyout();

        ToggleMenuFlyoutItem MakeToggle(IToggleFeature feature)
        {
            var item = new ToggleMenuFlyoutItem { Text = feature.Name };
            // Target state comes from the item the user just clicked, not a fresh OS read (TOCTOU).
            item.Command = new RelayCommand(() => Toggle(feature, !item.IsChecked));
            _toggles.Add((item, feature));
            return item;
        }

        Flyout.Items.Add(new MenuFlyoutItem { Text = "Settings…", Command = new RelayCommand(_onOpenSettings) });

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem
        {
            Text    = "Check for updates",
            Command = new RelayCommand(() => _ = CheckForUpdatesAsync()),
        });
        foreach (var feature in features)
            Flyout.Items.Add(MakeToggle(feature));

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "What's new…", Command = new RelayCommand(() => ShowWhatsNew()) });
        Flyout.Items.Add(new MenuFlyoutItem { Text = "About…", Command = new RelayCommand(() => ShowAbout()) });

        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(onExit) });

        // Never unsubscribed — the subscription lives for the whole process.
        NetworkLocationService.LocationChanged += OnNetworkLocationChanged;

        // Off-thread: ReadState blocks on a vendor RPC and this runs on the UI thread before the
        // tray icon exists. Seeds the first snapshot RefreshState re-applies.
        QueueRefresh();
    }

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
            Command = new RelayCommand(() => _ = CheckForUpdatesAsync()),
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
        IReadOnlyList<(bool Available, bool Enabled)> Features);   // aligned with _toggles

    private MenuState ReadState()
    {
        var features = new (bool Available, bool Enabled)[_toggles.Count];
        for (int i = 0; i < _toggles.Count; i++)
        {
            var feature = _toggles[i].Feature;
            // One combined read — "enabled" is meaningful only when available.
            var (available, enabled) = SafeCall(() => feature.ReadState(),
                                                fallback: (Available: true, Enabled: false));
            features[i] = (available, available && enabled);
        }
        return new MenuState(features);
    }

    // The most recent snapshot, re-applied by RefreshState. UI thread only, so no synchronisation.
    private MenuState? _lastApplied;

    private void ApplyState(MenuState state)
    {
        _lastApplied = state;
        for (int i = 0; i < _toggles.Count; i++)
        {
            var (available, enabled) = state.Features[i];
            _toggles[i].Item.IsEnabled = available;
            _toggles[i].Item.IsChecked = enabled;
        }
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
            // No network at all is not an "unknown network" — it is nothing to react to.
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
                return;
            }

            _aboutWindow = new AboutWindow(ShowWhatsNew);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Activate();
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

    /// <summary>Runs the update check. An accepted update downloads in the background and exits the app itself.</summary>
    private async Task CheckForUpdatesAsync()
    {
        // Capture the HWND while the flyout is open, and no ConfigureAwait(false) below:
        // TaskDialogIndirect needs the manifest's comctl32 v6 context, which pool threads lack.
        var hwnd    = NativeMethods.CaptureHwnd();
        var outcome = await UpdateCheckService.Shared.CheckNowAsync();
        var running = AppInfo.Version;

        switch (outcome.Status)
        {
            case UpdateStatus.Available:
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
                                // Before the new directory exists, so each run clears the last one.
                                InstallerSignature.SweepPreviousDownloads();

                                var path = await UpdateCheckService.Shared
                                    .DownloadInstallerAsync(outcome.InstallerUrl!)
                                    .ConfigureAwait(false);

                                // Fail closed: CI signs the installer, so anything else is not it.
                                var verdict = InstallerSignature.Verify(path);
                                if (!InstallerSignaturePolicy.MayLaunch(verdict))
                                {
                                    AppLog.Info($"Update: refusing to launch {path} — {verdict}.");
                                    InstallerSignature.Discard(path);
                                    NativeMethods.Warn(InstallerSignaturePolicy.MessageFor(verdict), AppName);
                                    Process.Start(new ProcessStartInfo(outcome.ReleaseUrl) { UseShellExecute = true });
                                    return;
                                }

                                // Exit right after: a process the installer has to kill needs UAC.
                                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                                Flyout.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    try { _onExit(); }
                                    catch (Exception ex) { AppLog.Error("TrayMenu.exit", ex); }
                                });
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

            // Every other status is worded by UpdateMessage — a pure helper the tests drive.
            default:
                if (UpdateMessage.For(outcome, running, DateTimeOffset.Now) is { } notice)
                {
                    if (notice.IsError) NativeMethods.Warn(notice.Text, AppName);
                    else                NativeMethods.Info(notice.Text, AppName);
                }
                break;
        }
    }

    // Apply target state off the UI thread — RPC/service writes can block for seconds.
    private void Toggle(IToggleFeature feature, bool enable)
        => Task.Run(() =>
        {
            // No StateChanged here, so the finally re-reads the OS — an unreported failure would
            // silently un-tick the item.
            try
            {
                bool ok = feature.SetEnabled(enable);
                if (!ok)
                    AppLog.Info($"Toggle '{feature.Name}' → {enable} was refused — the write returned false.");
            }
            catch (Exception ex)
            {
                // AutoStartFeature throws when the exe path cannot be resolved.
                AppLog.Error($"TrayMenu.Toggle '{feature.Name}'", ex);
                NativeMethods.Warn($"Could not change '{feature.Name}'.\n\n{ex.Message}", AppName);
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
