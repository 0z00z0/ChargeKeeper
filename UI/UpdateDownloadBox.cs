using ChargeKeeper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZeroZero.Update;

namespace ChargeKeeper.UI;

/// <summary>
/// The small window shown while an update downloads. Built in code rather than markup: it is four
/// controls with no styling of its own, and a XAML page for it would be more to keep in step than
/// the window is worth.
/// </summary>
/// <remarks>
/// The update components draw nothing themselves for any trigger, so what a person sees between
/// accepting an update and Setup starting is entirely this.
/// <para>The trap this is built around: verification runs after the last byte arrives and reports
/// nothing. A bar that reached the end would then stand still through the hash and signature checks
/// and read as a hang, so reaching the total switches the wording to say the file is being checked.
/// Where no total was given the bar never fills — it runs as a moving bar throughout — so that case
/// cannot look stuck in the first place and says only how much has arrived.</para>
/// </remarks>
internal sealed class UpdateDownloadBox
{
    private const int WidthDip  = 380;
    private const int HeightDip = 190;

    private readonly Action<Action> _runOnUi;
    private readonly string _fileName;

    private Window? _window;
    private ProgressBar? _bar;
    private TextBlock? _status;
    private bool _finished;

    internal UpdateDownloadBox(Action<Action> runOnUi, string fileName)
    {
        _runOnUi  = runOnUi;
        _fileName = fileName;
        Reporter  = new Progress(this);
    }

    /// <summary>What the flow reports into. Handed to <c>UpdateFlowOptions.Progress</c>.</summary>
    internal IProgress<DownloadProgress> Reporter { get; }

    /// <summary>Opens the window. Safe to call from any thread.</summary>
    internal void Show() => _runOnUi(() =>
    {
        if (_window is not null) return;

        _bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 8, 0, 8) };
        _status = new TextBlock
        {
            Text = "Starting the download…",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 2, Padding = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(new TextBlock { Text = "Downloading the update", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = _fileName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.8,
        });
        panel.Children.Add(_bar);
        panel.Children.Add(_status);

        _window = new Window { Title = $"{Helpers.AppInfo.Name} — update", Content = panel };

        // Dismissable only by the flow ending: closing it would leave the download running with
        // nothing on screen, which is the state this window exists to remove.
        _window.AppWindow.Closing += (_, e) => e.Cancel = !_finished;
        _window.Activate();

        // Sized after activation: the rasterisation scale is only known once the content has a
        // XamlRoot, and a fixed pixel size would come out tiny on a high-DPI display.
        double scale = panel.XamlRoot?.RasterizationScale ?? 1.0;
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(WidthDip * scale), (int)Math.Round(HeightDip * scale)));
    });

    /// <summary>Closes the window and allows it to close. Safe to call from any thread, and more
    /// than once.</summary>
    internal void Finish() => _runOnUi(() =>
    {
        _finished = true;
        _window?.Close();
        _window = null;
    });

    private void Apply(DownloadProgress progress) => _runOnUi(() =>
    {
        if (_bar is null || _status is null) return;

        if (progress.Fraction is { } fraction)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(fraction, 0, 1);
            _status.Text = fraction >= 1
                ? "Checking the file…"
                : $"{Megabytes(progress.BytesReceived)} of {Megabytes(progress.TotalBytes ?? 0)} MB";
            return;
        }

        // No size was given by either the response or the release, and nothing is guessed: a moving
        // bar and the count that is actually known.
        _bar.IsIndeterminate = true;
        _status.Text = $"{Megabytes(progress.BytesReceived)} MB so far";
    });

    private static string Megabytes(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    // A named reporter rather than System.Progress<T>, which captures whichever synchronisation
    // context it was constructed on — the flow runs off the UI thread, so the callbacks would not
    // be able to touch the window.
    private sealed class Progress(UpdateDownloadBox box) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            try { box.Apply(value); }
            catch (Exception ex) { AppLog.Error($"{nameof(UpdateDownloadBox)}.{nameof(Report)}", ex); }
        }
    }
}
