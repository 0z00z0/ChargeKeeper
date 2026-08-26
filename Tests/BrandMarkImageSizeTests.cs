using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// The DIP-to-pixel mapping behind the in-window brand marks. #131: the Settings nav footer and the
// Dashboard header used to resample one 256 px asset, so the body stroke fell under a pixel at 18
// DIPs. They now draw at their own frame size, which is whatever this returns.
public class BrandMarkImageSizeTests
{
    [Theory]
    [InlineData(18, 1.0,  18)]   // Dashboard header at 100 %
    [InlineData(18, 1.25, 23)]   // 125 % — 22.5 rounds away from zero
    [InlineData(18, 1.5,  27)]
    [InlineData(18, 2.0,  36)]
    [InlineData(36, 1.0,  36)]   // Settings nav footer at 100 %
    [InlineData(36, 1.5,  54)]
    [InlineData(36, 2.0,  72)]
    [InlineData(36, 3.0, 108)]
    public void ScalesTheDeclaredDipSizeByTheRasterisationScale(double dip, double scale, int expected) =>
        Assert.Equal(expected, BrandMarkImage.PixelSizeForDip(dip, scale));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void UnknownScale_DrawsAtTheDeclaredDipSize(double scale) =>
        // What an element not yet in a live visual tree reports. One DIP per pixel is the honest
        // reading; the redraw on XamlRoot.Changed corrects it.
        Assert.Equal(18, BrandMarkImage.PixelSizeForDip(18, scale));

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]     // an Image with no Width set
    public void UnsetDipSize_ClampsToTheFloor(double dip) =>
        Assert.Equal(8, BrandMarkImage.PixelSizeForDip(dip, 2.0));

    [Fact]
    public void AbsurdScale_CannotAskForAGiantBitmap() =>
        Assert.Equal(512, BrandMarkImage.PixelSizeForDip(36, 1000));

    [Fact]
    public void TinyRequest_StaysAboveTheFloor() =>
        Assert.Equal(8, BrandMarkImage.PixelSizeForDip(2, 1.0));
}
