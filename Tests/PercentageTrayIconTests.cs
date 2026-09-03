using System.Drawing;
using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The second, display-only tray icon. Its identity, the rule that stops it duplicating the Numeric
/// % style, and the two properties of its creation that cannot be seen by looking at the tray: the
/// efficiency flag, which is a property of the whole process, and disposal on exit, which is what
/// stops a ghost icon being left behind.
/// </summary>
public class PercentageTrayIconTests
{
    /// <summary>Pinned as its own literal, for the reason the main icon's is: a regenerated value
    /// costs the installation the tray position its owner chose, silently.</summary>
    private const string PinnedPercentageIdentity = "3C0B6A57-9E44-4E1B-B0A2-6D8F4C21B7E9";

    private static string AppSourceWithoutComments() =>
        Regex.Replace(File.ReadAllText(RepoFiles.Find("App.xaml.cs")), @"//[^\r\n]*", string.Empty);

    [Fact]
    public void TheSecondIdentity_IsTheValueEveryInstallationAlreadyHas() =>
        Assert.Equal(new Guid(PinnedPercentageIdentity), TrayIconIdentity.PercentageValue);

    [Fact]
    public void TheTwoIcons_DoNotShareOneIdentity() =>
        // One value for both would make the shell treat them as one icon and track a single
        // position for the pair.
        Assert.NotEqual(TrayIconIdentity.Value, TrayIconIdentity.PercentageValue);

    [Fact]
    public void TheSecondIdentity_IsDeclaredAsThatLiteral() =>
        Assert.Contains($"new(\"{PinnedPercentageIdentity}\")",
                        File.ReadAllText(RepoFiles.Find("Helpers/TrayIconIdentity.cs")),
                        StringComparison.Ordinal);

    // The interlock with the numeric style. Two icons drawing the same number is the failure.

    // The mode arrives by name: TrayIconMode is internal, and an internal type cannot be a public
    // test method's parameter.
    [Theory]
    [InlineData("Arc",       true,  true)]
    [InlineData("BrandMark", true,  true)]
    [InlineData("Numeric",   true,  false)]
    [InlineData("Arc",       false, false)]
    [InlineData("Numeric",   false, false)]
    public void TheSecondIcon_IsDrawnOnlyWhereItAddsAReading(string mode, bool stored, bool drawn)
    {
        var settings = new AppSettings
        {
            IconMode           = Enum.Parse<TrayIconMode>(mode),
            ShowPercentageIcon = stored,
        };

        Assert.Equal(drawn, settings.PercentageIconWanted);
    }

    [Fact]
    public void ChoosingTheNumericStyle_AlsoClearsTheStoredFlag()
    {
        // Disabling the control is not enough on its own: a flag left stored as on would bring the
        // duplicate back the moment another style was chosen.
        string body = SourceMethods.Body(
            Regex.Replace(File.ReadAllText(RepoFiles.Find("UI/SettingsWindow.xaml.cs")), @"//[^\r\n]*", string.Empty),
            "OnIconModeChanged");

        Assert.Contains("ShowPercentageIcon = false", body, StringComparison.Ordinal);
        Assert.Contains("ApplyPercentageIconAvailability", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPage_OffersTheToggleAndItsExplanation()
    {
        string xaml = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")));

        // The label is the one the requirement names, so a reworded header is a decision rather
        // than a slip.
        Assert.Contains("Header=\"Also show percentage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PercentageIconToggle\"", xaml, StringComparison.Ordinal);
    }

    // Creation, read off the source: none of this can be seen by looking at a running tray.

    [Fact]
    public void TheSecondIcon_NeverArmsTheLibrarysEfficiencyMode()
    {
        // The flag drops the WHOLE process to the lowest priority class and into a throttled power
        // band, once and for the rest of the run, so a single icon created with the library's
        // default undoes the main icon's care.
        string body = SourceMethods.Body(AppSourceWithoutComments(), "ApplyPercentageIcon");

        Assert.Contains("ForceCreate(enablesEfficiencyMode: false)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecondIcon_TakesItsIdentityBeforeItIsCreated()
    {
        string body = SourceMethods.Body(AppSourceWithoutComments(), "ApplyPercentageIcon");

        int identity = body.IndexOf("TrayIconIdentity.PercentageValue", StringComparison.Ordinal);
        int create   = body.IndexOf("ForceCreate", StringComparison.Ordinal);
        Assert.True(identity >= 0, "The second icon no longer takes its own identity.");
        Assert.True(create > identity, "The second icon is created before it is given its identity.");
    }

    [Fact]
    public void TheSecondIcon_CarriesNothingInteractive()
    {
        // Display-only: the menu, the dashboard and the tooltip stay on the main icon.
        string body = SourceMethods.Body(AppSourceWithoutComments(), "ApplyPercentageIcon");

        foreach (string member in new[] { "ContextFlyout", "LeftClickCommand", "RightClickCommand", "ToolTipText" })
            Assert.DoesNotContain(member, body, StringComparison.Ordinal);
    }

    [Fact]
    public void BothIcons_AreRemovedOnExit()
    {
        // A tray icon whose process is gone stays on screen until the shell is poked.
        string source = AppSourceWithoutComments();
        Assert.Contains("_percentageIcon?.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("_currentPercentageIcon?.Dispose()", source, StringComparison.Ordinal);
    }

    // The drawing itself. The second icon exists to be read, so the reading has to fill the frame.

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TheDigitsFillTheFrameRatherThanSittingInsideAMargin(int size)
    {
        using var bmp = IconGenerator.RenderPercentageBitmap(size, 88, PowerState.Discharging);

        int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (bmp.GetPixel(x, y).A > 40)
                {
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x); right = Math.Max(right, x);
                }

        Assert.True(bottom >= 0, $"Nothing was drawn at {size} px.");

        // Both edges, because a reading that fills the width and letterboxes the height is the
        // shape this replaced.
        Assert.True(right - left + 1 >= size - 1,
                    $"The digits span {right - left + 1} of {size} px across.");
        Assert.True(bottom - top + 1 >= size - 1,
                    $"The digits span {bottom - top + 1} of {size} px down.");
    }

    [Fact]
    public void TheReadingIsWhatChanges_NotJustTheColour()
    {
        using var low  = IconGenerator.RenderPercentageBitmap(16, 8,  PowerState.Discharging);
        using var high = IconGenerator.RenderPercentageBitmap(16, 88, PowerState.Discharging);

        bool same = true;
        for (int y = 0; y < 16 && same; y++)
            for (int x = 0; x < 16 && same; x++)
                if (low.GetPixel(x, y) != high.GetPixel(x, y)) same = false;

        Assert.False(same, "Two different readings render identically.");
    }

    [Fact]
    public void ThreeDigitsAreCondensedRatherThanShrunkAwayFromTheFrame()
    {
        // "100" is the widest reading. Condensing is what keeps it as tall as a two-digit one; the
        // floor stops it collapsing into a smear.
        var (_, twoDigits)   = IconGenerator.DigitFit(16, new RectangleF(0, 0, 110, 100));
        var (_, threeDigits) = IconGenerator.DigitFit(16, new RectangleF(0, 0, 165, 100));

        Assert.True(threeDigits < twoDigits, "Three digits are not condensed any further than two.");
        Assert.True(threeDigits >= IconGenerator.DigitMinimumCondense,
                    "The condensing floor is not respected.");
    }
}
