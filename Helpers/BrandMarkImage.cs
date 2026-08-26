using System.Drawing.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Sources an in-window <see cref="Image"/> with the brand mark drawn at the pixel size that element
/// occupies, the way the tray icon and the build-time bitmaps get theirs, rather than resampling one
/// 256 px asset down to it. A 36 px stroke authored for 256 px falls under a pixel at 18 DIPs, which
/// reads as a thin, soft mark however good the resampler is.
/// <para>The classic proportions, from <see cref="IconGenerator.RenderAppIconBitmap"/>: the
/// maximised set belongs to the notification-area slot alone.</para>
/// </summary>
internal static class BrandMarkImage
{
    // A rasterisation scale runs 1..5 in practice. The clamp exists only so a bogus one cannot ask
    // for an empty bitmap or a huge one.
    private const int MinPixelSize = 8;
    private const int MaxPixelSize = 512;

    /// <summary>Physical pixel size for a mark laid out at <paramref name="dipSize"/> DIPs on a
    /// surface rasterising at <paramref name="rasterizationScale"/>.</summary>
    internal static int PixelSizeForDip(double dipSize, double rasterizationScale)
    {
        // The scale is only real once the element is in a live visual tree; 1 is the honest reading
        // before that, and NaN is what an unset Width returns.
        if (!(rasterizationScale > 0)) rasterizationScale = 1.0;
        if (!(dipSize > 0)) return MinPixelSize;

        int pixels = (int)Math.Round(dipSize * rasterizationScale, MidpointRounding.AwayFromZero);
        return Math.Clamp(pixels, MinPixelSize, MaxPixelSize);
    }

    /// <summary>Draws the mark into <paramref name="image"/> at its declared DIP size and redraws it
    /// when the host's rasterisation scale moves. The element's own <c>Width</c> is the size, so the
    /// figure is stated in the XAML that lays it out and nowhere else. Call once per element.</summary>
    internal static void Attach(Image image)
    {
        double    dipSize    = image.Width;
        XamlRoot? watchedRoot = null;
        int       lastPixels  = 0;

        image.Loaded   += (_, _) => OnLoaded();
        image.Unloaded += (_, _) => StopWatching();

        void OnLoaded()
        {
            // A window closed and reopened arrives with a different XamlRoot, so the subscription
            // follows the element rather than being made once.
            if (!ReferenceEquals(watchedRoot, image.XamlRoot))
            {
                StopWatching();
                watchedRoot = image.XamlRoot;
                if (watchedRoot is not null) watchedRoot.Changed += OnRootChanged;
            }

            Redraw();
        }

        void StopWatching()
        {
            if (watchedRoot is null) return;
            watchedRoot.Changed -= OnRootChanged;
            watchedRoot = null;
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Redraw();

        void Redraw()
        {
            // XamlRoot.Changed also carries plain size changes, which move no pixel of the mark.
            int pixels = PixelSizeForDip(dipSize, image.XamlRoot?.RasterizationScale ?? 1.0);
            if (pixels == lastPixels) return;

            lastPixels = pixels;
            Apply(image, pixels);
        }
    }

    /// <summary>Renders the mark at <paramref name="pixelSize"/> and hands it to the element. Async
    /// because the XAML decoder is; a failure leaves the previous source in place rather than
    /// throwing out of an event handler, where nothing would catch it.</summary>
    private static async void Apply(Image image, int pixelSize)
    {
        try
        {
            byte[] png;
            using (var mark   = IconGenerator.RenderAppIconBitmap(pixelSize))
            using (var encoded = new MemoryStream())
            {
                mark.Save(encoded, ImageFormat.Png);
                png = encoded.ToArray();
            }

            var source = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                var writer = new DataWriter(stream);
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();

                stream.Seek(0);
                await source.SetSourceAsync(stream);
            }

            image.Source = source;
        }
        catch (Exception ex)
        {
            AppLog.Error($"BrandMarkImage.Apply({pixelSize})", ex);
        }
    }
}
