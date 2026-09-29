using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The second, display-only tray icon's identity, and the digit style names the settings document
/// stores.
/// </summary>
public class PercentageTrayIconTests
{
    /// <summary>Pinned as its own literal, for the reason the main icon's is: a regenerated value
    /// costs the installation the tray position its owner chose, silently.</summary>
    private const string PinnedPercentageIdentity = "3C0B6A57-9E44-4E1B-B0A2-6D8F4C21B7E9";

    [Fact]
    public void TheSecondIdentity_IsTheValueEveryInstallationAlreadyHas() =>
        Assert.Equal(new Guid(PinnedPercentageIdentity), TrayIconIdentity.PercentageValue);

    // Where the digit style is offered, and what it is stored as.

    [Fact]
    public void TheStoredDigitStyles_AreTheNamesEveryInstallationAlreadyHas()
    {
        // The settings document stores the member name, so a renamed or reordered member resets every
        // installation's chosen style. "Staggered" is a label; ClockCells is what is on disk.
        Assert.Equal(["Standard", "Cropped", "ClockCells", "HoursRemaining"], Enum.GetNames<TrayDigitStyle>());
        Assert.Equal(["\"Standard\"", "\"Cropped\"", "\"ClockCells\"", "\"HoursRemaining\""],
                     Enum.GetValues<TrayDigitStyle>().Select(s => System.Text.Json.JsonSerializer.Serialize(s)));

        // The three that were there before keep their positions: the Settings page casts the box's
        // index to this enum, so a member that moved would silently pick a different style.
        Assert.Equal(0, (int)TrayDigitStyle.Standard);
        Assert.Equal(1, (int)TrayDigitStyle.Cropped);
        Assert.Equal(2, (int)TrayDigitStyle.ClockCells);
    }
}
