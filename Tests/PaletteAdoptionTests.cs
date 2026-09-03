using ChargeKeeper.Helpers;
using Xunit;
using ZeroZero.Brand.Core;

namespace ChargeKeeper.Tests;

/// <summary>
/// The colours ChargeKeeper takes from the studio palette rather than declaring. A hand-typed
/// literal that drifts from the shared value changes a colour on screen with nothing saying so,
/// which is what these pin.
/// </summary>
public class PaletteAdoptionTests
{
    private static string Hex(uint argb) => $"#{argb & 0x00FFFFFFu:x6}";

    [Fact]
    public void StudioValues_AreTheOnesTheAppWasBuiltAgainst()
    {
        // The studio side of the pin: a change in ZeroZero.Brand.Core is a decision, not a typo,
        // and it has to reach this file before it reaches a build. #11a9d6 is the one the app got
        // wrong by eye, so it is pinned here as well as in the palette.
        Assert.Equal("#c9926b", Brand.ColorTerracotta);
        Assert.Equal("#7fa8b8", Brand.ColorSteelBlue);
        Assert.Equal("#d8a657", Brand.ColorAmber);
        Assert.Equal("#11a9d6", Brand.ColorBlue);
    }

    [Fact]
    public void GaugePalette_ReadsItsThreeStudioColoursFromTheBrand()
    {
        Assert.Equal(Brand.ColorTerracotta, Hex(GaugePalette.Terracotta));
        Assert.Equal(Brand.ColorSteelBlue,  Hex(GaugePalette.SteelBlue));
        Assert.Equal(Brand.ColorAmber,      Hex(GaugePalette.Amber));
    }

    [Fact]
    public void GaugePalette_KeepsItsOwnColoursTheSharedPaletteDoesNotCarry()
    {
        string[] studio =
        [
            Brand.ColorBg, Brand.ColorBg2, Brand.ColorTeal, Brand.ColorBlue, Brand.ColorPurple,
            Brand.ColorIndigo, Brand.ColorAmber, Brand.ColorSteelBlue, Brand.ColorTerracotta,
        ];

        foreach (uint own in new[] { GaugePalette.Ember, GaugePalette.SageGreen, GaugePalette.Lavender, GaugePalette.Orchid })
            Assert.DoesNotContain(Hex(own), studio);
    }

    [Fact]
    public void FromHex_TakesAStudioConstantAsAnOpaquePackedValue()
    {
        Assert.Equal(0xFF7FA8B8u, GaugePalette.FromHex("#7fa8b8"));
        Assert.Equal(0xFF7FA8B8u, GaugePalette.FromHex("7FA8B8"));
    }
}
