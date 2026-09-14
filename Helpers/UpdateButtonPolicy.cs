using ChargeKeeper.Services;
using ZeroZero.Brand.WinUI;

namespace ChargeKeeper.Helpers;

/// <summary>Who asked for an update check, which decides where its outcome is reported.</summary>
internal enum UpdateCheckTrigger
{
    /// <summary>The About window being shown or the Settings window opening. The outcome shows on
    /// the button alone, failures included.</summary>
    Automatic,

    /// <summary>The Check for updates button. Up to date and an available update show on the button;
    /// a failure keeps its dialog.</summary>
    Button,

    /// <summary>The tray menu, which has no button to show anything on, so every outcome is a
    /// dialog.</summary>
    TrayMenu,
}

/// <summary>
/// What the Check for updates button shows and which outcomes still reach a dialog. Pure, so the
/// promise that a window opening never raises a dialog is assertable without a display.
/// </summary>
internal static class UpdateButtonPolicy
{
    internal const string RestLabel     = "Check for updates";
    internal const string CheckingLabel = "Checking…";
    internal const string UpToDateLabel = "Up to date";

    internal static string AvailableLabel(string version) => $"Update to {version}";

    /// <summary>A state of the button and the label shown with it.</summary>
    internal readonly record struct Look(BrandBracketButtonState State, string Label);

    internal static Look Rest => new(BrandBracketButtonState.Rest, RestLabel);

    internal static Look Checking => new(BrandBracketButtonState.Busy, CheckingLabel);

    /// <summary>The button once a check has ended. A failure has no state of its own and returns the
    /// button to rest.</summary>
    internal static Look After(UpdateCheckService.CheckOutcome outcome) => outcome.Status switch
    {
        UpdateStatus.UpToDate => new(BrandBracketButtonState.Success, UpToDateLabel),
        UpdateStatus.Available when outcome.LatestVersion is { Length: > 0 } version
            => new(BrandBracketButtonState.Attention, AvailableLabel(version)),
        _ => Rest,
    };

    /// <summary>Whether the outcome's message box is shown.</summary>
    internal static bool ShowsNotice(UpdateStatus status, UpdateCheckTrigger trigger) => trigger switch
    {
        UpdateCheckTrigger.Automatic => false,
        UpdateCheckTrigger.Button    => status is not (UpdateStatus.UpToDate or UpdateStatus.Available),
        _                            => status is not UpdateStatus.Available,
    };

    /// <summary>Whether the update dialog opens as soon as the check ends. From a button it opens
    /// only when the button is selected in its update-available state.</summary>
    internal static bool OpensUpdateDialog(UpdateStatus status, UpdateCheckTrigger trigger) =>
        trigger == UpdateCheckTrigger.TrayMenu && status == UpdateStatus.Available;
}
