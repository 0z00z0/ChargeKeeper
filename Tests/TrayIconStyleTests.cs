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

    /// <summary>Probes upwards for the repo marker rather than hard-coding the test output's depth.</summary>
    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ChargeKeeper.csproj")))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }

    /// <summary>The ComboBoxItem labels inside the IconModeCombo block of SettingsWindow.xaml, in
    /// markup order.</summary>
    private static string[] ReadComboBoxLabels()
    {
        string xaml  = File.ReadAllText(FindRepoFile(Path.Combine("UI", "SettingsWindow.xaml")));
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
        Assert.Equal(Styles.Select(s => s.Mode), HaEntityCatalog.IconModeOptions);

    [Fact]
    public void EveryAdvertisedOption_ParsesBackToItsOwnMode()
    {
        foreach (string option in HaEntityCatalog.IconModeOptions)
        {
            Assert.True(HaCommand.TryParse(HaEntityCatalog.IconMode, option, out var cmd),
                        $"The select advertises '{option}' but the parser refuses it.");
            Assert.Equal(option, ((TrayIconMode)cmd.IntValue).ToString());
        }
    }

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
        // The SVG's fill rect ends at x 146; 76 % is the nearest level still in the sage tier.
        Assert.Equal(146f, IconGenerator.MarkInteriorX(IconGenerator.MarkCanonicalPercent), 4.0);

    [Fact]
    public void EveryStyleRenders_AtEveryTierAndOnAc()
    {
        // Narrow smoke cover: the dispatch reaches a real renderer for each member, and no style
        // throws at the extremes. A tray-icon render failure is caught and logged in App, so a
        // broken new style would otherwise show only as an icon that never changes.
        foreach (var mode in Enum.GetValues<TrayIconMode>())
            foreach (int pct in new[] { 0, 10, 50, 100 })
                foreach (bool charging in new[] { false, true })
                {
                    using var icon = IconGenerator.RenderBatteryIcon(pct, charging, mode);
                    Assert.True(icon.Width >= 16, $"{mode} at {pct} % rendered {icon.Width} px.");
                }
    }
}
