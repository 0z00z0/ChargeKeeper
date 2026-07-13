using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure "reject-on-save" validation tests for the Settings window's preset editor (TODO #19).
public class PresetEditValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_EmptyOrWhitespaceName_ReturnsError(string? name)
    {
        var error = PresetEditValidator.Validate(name!, 60, 80, existingNames: [], originalName: null);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_DuplicateName_CaseInsensitive_ReturnsError()
    {
        var error = PresetEditValidator.Validate("daily", 60, 80,
            existingNames: ["Daily", "Travel"], originalName: null);
        Assert.NotNull(error);
        Assert.Contains("already exists", error);
    }

    [Fact]
    public void Validate_UnchangedNameOnRename_NotTreatedAsDuplicate()
    {
        // Editing "Daily"'s thresholds without renaming it must not collide with itself in the
        // existing-names list.
        var error = PresetEditValidator.Validate("Daily", 60, 85,
            existingNames: ["Daily", "Travel"], originalName: "Daily");
        Assert.Null(error);
    }

    [Fact]
    public void Validate_RenameToAnotherExistingPreset_ReturnsError()
    {
        var error = PresetEditValidator.Validate("Travel", 60, 80,
            existingNames: ["Daily", "Travel"], originalName: "Daily");
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_GapBelowMinimum_ReturnsError()
    {
        var error = PresetEditValidator.Validate("Daily", 60, 63, existingNames: [], originalName: "Daily");
        Assert.NotNull(error);
        Assert.Contains("at least", error);
    }

    [Fact]
    public void Validate_GapExactlyAtMinimum_IsValid()
    {
        var error = PresetEditValidator.Validate("Daily", 60, 65, existingNames: [], originalName: "Daily");
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ZeroGap_ReturnsError()
    {
        // RangeSelector itself prevents Start from passing Stop, but allows them to end up equal
        // (see DashboardWindow's own comment on this) — the validator must still catch it.
        var error = PresetEditValidator.Validate("Daily", 70, 70, existingNames: [], originalName: "Daily");
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(-5, 80)]
    [InlineData(60, 105)]
    public void Validate_ThresholdOutOfRange_ReturnsError(int start, int stop)
    {
        var error = PresetEditValidator.Validate("Daily", start, stop, existingNames: [], originalName: "Daily");
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_NewPresetWithValidValues_ReturnsNull()
    {
        var error = PresetEditValidator.Validate("Weekend", 50, 90,
            existingNames: ["Daily", "Travel"], originalName: null);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Do nothing")]
    [InlineData("do NOTHING")]
    public void Validate_ReservedName_ReturnsError(string name)
    {
        // "Do nothing" is the sentinel SettingsWindow's unknown-network-preset combo uses for
        // "route nowhere" — a preset actually named this would be indistinguishable from it.
        var error = PresetEditValidator.Validate(name, 60, 80, existingNames: [], originalName: null);
        Assert.NotNull(error);
        Assert.Contains("reserved", error);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(4, 80)]
    public void Validate_StartBelowFloor_ReturnsError(int start, int stop)
    {
        // MinThreshold mirrors DashboardWindow's live-slider floor of 5, not the vendor layer's
        // bare minimum of 1 — a Start of 1-4 would pass LenovoChargeThreshold.SetThresholds but
        // still be inconsistent with every other threshold control in this app.
        var error = PresetEditValidator.Validate("Daily", start, stop, existingNames: [], originalName: "Daily");
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_StartAtFloor_IsValid()
    {
        var error = PresetEditValidator.Validate("Daily", 5, 80, existingNames: [], originalName: "Daily");
        Assert.Null(error);
    }
}
