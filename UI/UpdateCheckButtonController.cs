using Microsoft.UI.Xaml;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ZeroZero.Brand.WinUI;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.UI;

/// <summary>
/// Drives one Check for updates button from the tray menu's shared update check: it shows any check
/// running, whoever started it, and opens the update window when selected with an update showing.
/// Shared by the About window and the Settings About page so the two cannot behave apart.
/// </summary>
internal sealed class UpdateCheckButtonController
{
    private readonly BrandBracketButton _button;
    private readonly TrayMenu _menu;

    /// <summary>Wraps the update window, for a host that must stay open while it is up. Awaited, so
    /// the hold lasts as long as the offer and the download behind it.</summary>
    private readonly Func<Func<Task>, Task> _aroundDialog;

    // The check the button currently reflects. A newer one replaces it, and a stale result is dropped.
    private Task<UpdateFlowRun>? _following;

    // The run behind the update-available state, offered as it stands when the button is selected.
    private UpdateFlowRun? _available;

    private bool _detached;

    internal UpdateCheckButtonController(BrandBracketButton button, TrayMenu menu,
                                         Func<Func<Task>, Task>? aroundDialog = null)
    {
        _button       = button;
        _menu         = menu;
        _aroundDialog = aroundDialog ?? (body => body());

        Show(UpdateButtonPolicy.Rest);
        _button.Click += (_, _) => OnClick();

        // The control returns from Success to Rest by itself and leaves the last label in place.
        _button.RegisterPropertyChangedCallback(BrandBracketButton.StateProperty, OnStateChanged);

        _menu.UpdateChecks.CheckStarted += OnCheckStarted;
    }

    /// <summary>The check a window runs as it is shown. Its outcome appears on the button alone.</summary>
    internal void CheckAutomatically() => Follow(_menu.CheckForUpdates(UpdateCheckTrigger.Automatic));

    /// <summary>Stops following the shared check. The coordinator lives for the process, so a closed
    /// window left subscribed would stay reachable and keep touching a torn-down tree.</summary>
    internal void Detach()
    {
        _detached = true;
        _menu.UpdateChecks.CheckStarted -= OnCheckStarted;
    }

    private void OnCheckStarted(Task<UpdateFlowRun> check) => Follow(check);

    private void OnClick()
    {
        try
        {
            switch (_button.State)
            {
                case BrandBracketButtonState.Busy:
                    return;

                case BrandBracketButtonState.Attention when _available is { } run:
                    _available = null;
                    Show(UpdateButtonPolicy.Rest);
                    _ = OfferAsync(run);
                    return;

                default:
                    Follow(_menu.CheckForUpdates(UpdateCheckTrigger.Button));
                    return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("UpdateCheckButtonController.OnClick", ex);
        }
    }

    private async Task OfferAsync(UpdateFlowRun run)
    {
        try { await _aroundDialog(() => _menu.OfferUpdate(run)); }
        catch (Exception ex) { AppLog.Error("UpdateCheckButtonController.Offer", ex); }
    }

    private void OnStateChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_button.State == BrandBracketButtonState.Rest && _button.Label != UpdateButtonPolicy.RestLabel)
            _button.Label = UpdateButtonPolicy.RestLabel;
    }

    // async void: an event and click continuation with nothing to return to. Every path is caught.
    // The coordinator raises CheckStarted from whichever thread called Run() — the component's own
    // background scheduler included — so this method's own body can already be running off the UI
    // thread before the first await, and the await itself resumes wherever the check completed.
    // Every touch of the button therefore goes through RunOnUi rather than Show() directly.
    private async void Follow(Task<UpdateFlowRun> check)
    {
        try
        {
            // The check this button started is announced before it is returned, so it arrives twice.
            if (_detached || ReferenceEquals(check, _following)) return;

            _following = check;
            _available = null;
            RunOnUi(() => Show(UpdateButtonPolicy.Checking));

            var run = await check;
            if (_detached || !ReferenceEquals(check, _following)) return;

            RunOnUi(() =>
            {
                _available = run.Result == UpdateFlowResult.UpdateAvailable ? run : null;
                Show(UpdateButtonPolicy.After(run));
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("UpdateCheckButtonController.Follow", ex);
            try { if (!_detached) RunOnUi(() => Show(UpdateButtonPolicy.Rest)); }
            catch (Exception inner) { AppLog.Error("UpdateCheckButtonController.Follow.Rest", inner); }
        }
    }

    /// <summary>Marshals <paramref name="action"/> onto the button's UI thread. An unhandled
    /// exception inside a raw <see cref="DispatcherQueue"/> callback is a stowed exception that
    /// tears the whole process down, so every touch that can be reached off the UI thread goes
    /// through here.</summary>
    private void RunOnUi(Action action)
    {
        try
        {
            _button.DispatcherQueue?.TryEnqueue(() =>
            {
                // Detach() can land between the enqueue and the callback running, and the button's
                // own window can have been destroyed in between — nothing left to update.
                if (_detached) return;
                try { action(); }
                catch (Exception ex) { AppLog.Error("UpdateCheckButtonController.RunOnUi", ex); }
            });
        }
        catch (Exception ex) { AppLog.Error("UpdateCheckButtonController.RunOnUi enqueue", ex); }
    }

    private void Show(UpdateButtonPolicy.Look look)
    {
        _button.Label = look.Label;
        _button.State = look.State;
    }
}
