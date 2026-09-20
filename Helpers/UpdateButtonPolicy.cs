using ZeroZero.Brand.WinUI;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Helpers;

/// <summary>Who asked for an update check, which decides where its outcome is reported.</summary>
internal enum UpdateCheckTrigger
{
    /// <summary>The About window being shown or the Settings window opening. The outcome shows on
    /// the button alone, failures included.</summary>
    Automatic,

    /// <summary>The Check for updates button. Up to date and an available update show on the button;
    /// a failure is reported in the component's window.</summary>
    Button,

    /// <summary>The tray menu, which has no button to show anything on, so every outcome opens the
    /// component's window.</summary>
    TrayMenu,
}

/// <summary>
/// What the Check for updates button shows and which outcomes open the update component's own
/// window. Pure, so the promise that a window opening never raises another is assertable without a
/// display.
/// </summary>
/// <remarks>Every check runs under <see cref="UpdateTrigger.Silent"/>, so the shared flow shows
/// nothing of its own and this is the only rule deciding what appears.</remarks>
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
    internal static Look After(UpdateFlowRun run) => run.Result switch
    {
        UpdateFlowResult.UpToDate => new(BrandBracketButtonState.Success, UpToDateLabel),
        UpdateFlowResult.UpdateAvailable when run.Release?.VersionText is { Length: > 0 } version
            => new(BrandBracketButtonState.Attention, AvailableLabel(version)),
        _ => Rest,
    };

    /// <summary>Whether the outcome is reported in the component's window.</summary>
    internal static bool ShowsNotice(UpdateFlowResult result, UpdateCheckTrigger trigger) =>
        // Stopping a download is the person's own act, and the window they stopped it in closed
        // itself as they did. Reopening one to say so would report their own click back at them.
        result != UpdateFlowResult.DownloadCancelled && trigger switch
        {
            UpdateCheckTrigger.Automatic => false,
            UpdateCheckTrigger.Button    => result is not (UpdateFlowResult.UpToDate or UpdateFlowResult.UpdateAvailable),
            _                            => result is not UpdateFlowResult.UpdateAvailable,
        };

    /// <summary>Whether the update window opens as soon as the check ends. From a button it opens
    /// only when the button is selected in its update-available state.</summary>
    internal static bool OpensUpdateDialog(UpdateFlowResult result, UpdateCheckTrigger trigger) =>
        trigger == UpdateCheckTrigger.TrayMenu && result == UpdateFlowResult.UpdateAvailable;
}
