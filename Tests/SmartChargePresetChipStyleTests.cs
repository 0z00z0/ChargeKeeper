using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// #207: the Smart Charge preset chips and "Charge to 100 % once" take the same
/// TimeScaleButtonStyle chrome as the Lid delay and Keep Awake chips, marked in use the same way —
/// a Checked ToggleButton — rather than a bespoke look built from scratch. Read from source, like
/// <see cref="DashboardBoxClickTests"/>, since WinUI cannot be driven without a display.
/// </summary>
public class SmartChargePresetChipStyleTests
{
    private static string DashboardSource() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "DashboardWindow.xaml.cs")));

    private static string DashboardMarkup() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "DashboardWindow.xaml")));

    /// <summary>The object initializer BuildPresetButtons hands each chip. Stops at the first "};",
    /// which is the outer initializer's own close — the one nested TextBlock inside it closes with
    /// "}," instead, so it cannot be mistaken for the end.</summary>
    private static string PresetChipInitializer()
    {
        string source = DashboardSource();
        int method = source.IndexOf("void BuildPresetButtons", StringComparison.Ordinal);
        Assert.True(method >= 0, "BuildPresetButtons no longer exists in DashboardWindow.xaml.cs.");

        // Scoped to this one method: BuildLidChips and BuildKeepAwakeChips build ToggleButton chips
        // of their own further down the file, and this must not read one of theirs by mistake.
        int start = source.IndexOf("new ToggleButton", method, StringComparison.Ordinal);
        Assert.True(start >= 0, "BuildPresetButtons no longer builds a ToggleButton.");

        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start, "The preset chip's object initializer could not be isolated.");
        return source[start..end];
    }

    [Fact]
    public void PresetChipTakesTheSharedChipStyle() =>
        Assert.Contains("QuickButtonStyle", PresetChipInitializer(), StringComparison.Ordinal);

    [Theory]
    [InlineData("FontSize")]
    [InlineData("CornerRadius")]
    [InlineData("BorderThickness")]
    [InlineData("Height")]
    [InlineData("Padding")]
    public void PresetChipSetsNoMetricOfItsOwn(string property) =>
        Assert.DoesNotContain(property, PresetChipInitializer(), StringComparison.Ordinal);

    [Fact]
    public void TheActivePresetIsNeverMarkedByDisablingTheButton()
    {
        // The dashboard used to fake its highlight through the DISABLED template brushes (Settings'
        // own approach, PresetRows.ApplyActiveResources) — replaced by the Lid delay chips' own
        // Checked-brush marker so the two no longer fight the same visual state.
        string source = DashboardSource();
        Assert.DoesNotContain("ButtonBackgroundDisabled", source, StringComparison.Ordinal);
        Assert.Contains("ApplyCheckedChipResources", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TravelOverrideButtonTakesTheSharedChipStyle()
    {
        string xaml  = DashboardMarkup();
        int    start = xaml.IndexOf("x:Name=\"TravelOverrideButton\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "TravelOverrideButton is no longer declared in DashboardWindow.xaml.");

        int tagStart = xaml.LastIndexOf('<', start);
        int tagEnd   = xaml.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.True(tagStart >= 0 && tagEnd > tagStart, "TravelOverrideButton is no longer a self-closing element.");
        string element = xaml[tagStart..tagEnd];

        Assert.Contains("TimeScaleButtonStyle", element, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=", element, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=", element, StringComparison.Ordinal);
    }
}
