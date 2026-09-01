using System.Linq;
using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The tray icon style list exists in four places that must agree: the TrayIconMode enum, the
// Settings ComboBox (cast to and from the enum BY POSITION), the MQTT select's advertised options
// and the command parser. A member inserted rather than appended silently remaps every saved
// setting after it, and nothing on screen says so.
public class TrayIconStyleTests
{
    // The ComboBox label for each enum member, in enum order. This table is the contract the
    // index cast rests on.
    private static readonly (string Mode, string Label)[] Styles =
    [
        (nameof(TrayIconMode.Arc),       "Arc gauge"),
        (nameof(TrayIconMode.Numeric),   "Numeric %"),
        (nameof(TrayIconMode.BrandMark), "Brand mark"),
    ];

    /// <summary>The ComboBoxItem labels inside the IconModeCombo block of SettingsWindow.xaml, in
    /// markup order.</summary>
    private static string[] ReadComboBoxLabels()
    {
        string xaml  = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")));
        int    start = xaml.IndexOf("x:Name=\"IconModeCombo\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "IconModeCombo is no longer declared in SettingsWindow.xaml.");
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, "The IconModeCombo block is not closed.");

        return Regex.Matches(xaml[start..end], @"<ComboBoxItem\s+Content=""(?<label>[^""]*)""")
                    .Select(m => m.Groups["label"].Value)
                    .ToArray();
    }

    [Fact]
    public void EnumOrder_MatchesTheDeclaredContract() =>
        Assert.Equal(Styles.Select(s => s.Mode), Enum.GetNames<TrayIconMode>());

    [Fact]
    public void ComboBoxItemOrder_MatchesTheEnumOrder() =>
        // Position IS the mapping: SettingsWindow casts SelectedIndex to TrayIconMode and back.
        Assert.Equal(Styles.Select(s => s.Label), ReadComboBoxLabels());

    [Fact]
    public void MqttSelectOptions_MatchTheEnumOrder() =>
        Assert.Equal(Styles.Select(s => s.Mode), MqttEntityCatalog.IconModeOptions);

    // The brand mark's interior band, which the charge fill and (from #113) the threshold marks are
    // placed on. The canonical values reproduce brand\chargekeeper-icon.svg's fixed geometry.

    [Fact]
    public void InteriorBand_RunsFromEmptyToFull()
    {
        Assert.Equal(36f,  IconGenerator.MarkInteriorX(0));
        Assert.Equal(185f, IconGenerator.MarkInteriorX(100));
    }

    [Fact]
    public void InteriorBand_ClampsOutOfRangeReadings()
    {
        Assert.Equal(36f,  IconGenerator.MarkInteriorX(-5));
        Assert.Equal(185f, IconGenerator.MarkInteriorX(140));
    }

    [Fact]
    public void CanonicalGuard_LandsWhereTheBrandSvgPutsIt() =>
        // The SVG's guard line sits at x 161 on the 256-unit canvas.
        Assert.Equal(161f, IconGenerator.MarkInteriorX(IconGenerator.MarkCanonicalGuard), 0.5);

    [Fact]
    public void CanonicalFill_LandsWhereTheBrandSvgPutsIt() =>
        // The SVG's fill rect ends at x 146. Geometry alone — the canonical renders take the brand's
        // fixed sage, so this level no longer decides a colour.
        Assert.Equal(146f, IconGenerator.MarkInteriorX(IconGenerator.MarkCanonicalPercent), 4.0);

    [Fact]
    public void EveryStyleRenders_AtEveryLevelAndPowerState()
    {
        // Narrow smoke cover: the dispatch reaches a real renderer for each member, and no style
        // throws at the extremes. A tray-icon render failure is caught and logged in App, so a
        // broken new style would otherwise show only as an icon that never changes.
        foreach (var mode in Enum.GetValues<TrayIconMode>())
            foreach (int pct in new[] { 0, 10, 50, 100 })
                foreach (var state in Enum.GetValues<PowerState>())
                {
                    using var icon = IconGenerator.RenderBatteryIcon(pct, state, mode);
                    Assert.True(icon.Width >= 16,
                                $"{mode} at {pct} % {state} rendered {icon.Width} px.");
                }
    }
}
