using Microsoft.UI.Xaml;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ZeroZero.Brand.WinUI;

namespace ChargeKeeper.UI;

/// <summary>
/// Drives one Check for updates button from the tray menu's shared update check: it shows any check
/// running, whoever started it, and opens the update dialog when selected with an update showing.
/// Shared by the About window and the Settings About page so the two cannot behave apart.
/// </summary>
internal sealed class UpdateCheckButtonController
{
    private readonly BrandBracketButton _button;
    private readonly TrayMenu _menu;

    /// <summary>Wraps the update dialog, for a host that must stay open while it is up.</summary>
    private readonly Action<Action> _aroundDialog;

    // The check the button currently reflects. A newer one replaces it, and a stale result is dropped.
    private Task<UpdateCheckService.CheckOutcome>? _following;

    // The outcome behind the update-available state, offered as it stands when the button is selected.
    private UpdateCheckService.CheckOutcome? _available;

    private bool _detached;

    internal UpdateCheckButtonController(BrandBracketButton button, TrayMenu menu,
                                         Action<Action>? aroundDialog = null)
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

    private void OnCheckStarted(Task<UpdateCheckService.CheckOutcome> check) => Follow(check);

    private void OnClick()
    {
        try
        {
            switch (_button.State)
            {
                case BrandBracketButtonState.Busy:
                    return;

                case BrandBracketButtonState.Attention when _available is { } outcome:
                    _available = null;
                    Show(UpdateButtonPolicy.Rest);
                    _aroundDialog(() => _menu.OfferUpdate(outcome));
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

    private void OnStateChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_button.State == BrandBracketButtonState.Rest && _button.Label != UpdateButtonPolicy.RestLabel)
            _button.Label = UpdateButtonPolicy.RestLabel;
    }

    // async void: an event and click continuation with nothing to return to. Every path is caught.
    private async void Follow(Task<UpdateCheckService.CheckOutcome> check)
    {
        try
        {
            // The check this button started is announced before it is returned, so it arrives twice.
            if (_detached || ReferenceEquals(check, _following)) return;

            _following = check;
            _available = null;
            Show(UpdateButtonPolicy.Checking);

            var outcome = await check;
            if (_detached || !ReferenceEquals(check, _following)) return;

            _available = outcome.Status == UpdateStatus.Available ? outcome : null;
            Show(UpdateButtonPolicy.After(outcome));
        }
        catch (Exception ex)
        {
            AppLog.Error("UpdateCheckButtonController.Follow", ex);
            try { if (!_detached) Show(UpdateButtonPolicy.Rest); }
            catch (Exception inner) { AppLog.Error("UpdateCheckButtonController.Follow.Rest", inner); }
        }
    }

    private void Show(UpdateButtonPolicy.Look look)
    {
        _button.Label = look.Label;
        _button.State = look.State;
    }
}
