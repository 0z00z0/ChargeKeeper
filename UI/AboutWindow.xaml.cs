using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

public sealed partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/0z00z0/ChargeKeeper";
    private const string BmacUrl   = "https://buymeacoffee.com/ezpl";
    private const string AppName   = "ChargeKeeper";

    private readonly Action _onExit;

    internal AboutWindow(Action onExit)
    {
        _onExit = onExit;
        InitializeComponent();
        ConfigureChrome();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver is not null ? $"v{ver.ToString(3)}" : "v—";

        CloseBtn.Click  += (_, _) => Close();
        GitHubBtn.Click += (_, _) => Open(GitHubUrl);
        UpdateBtn.Click += (_, _) => _ = CheckForUpdatesAsync();
        BmacBtn.Click   += (_, _) => Open(BmacUrl);
    }

    private void ConfigureChrome()
    {
        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        Root.Width = 320;
        Root.Measure(new Size(320, double.PositiveInfinity));

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int cw = (int)Math.Round(320 * scale);
        int ch = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 270) * scale);

        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.Left + (work.Right  - work.Left - outer.Width)  / 2,
            work.Top  + (work.Bottom - work.Top  - outer.Height) / 2));
    }

    private async Task CheckForUpdatesAsync()
    {
        // Do NOT use ConfigureAwait(false): ShowUpdateDialog → TaskDialogIndirect needs the
        // comctl32 v6 activation context, which only the UI thread has. Capture parent HWND
        // first so the dialog appears in front of this always-on-top window.
        var hwnd    = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var outcome = await UpdateCheckService.CheckNowAsync();
        var running = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        if (outcome.Status == UpdateCheckService.UpdateStatus.Available)
        {
            var action = NativeMethods.ShowUpdateDialog(
                outcome.LatestVersion!, running,
                outcome.ReleaseNotes ?? "", AppName,
                canDownload: outcome.InstallerUrl is not null,
                hwndParent: hwnd);

            switch (action)
            {
                case NativeMethods.UpdateAction.Update:
                    NativeMethods.Info(
                        $"Downloading v{outcome.LatestVersion}...\n\nThe installer will launch automatically when ready.",
                        AppName);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var path = await UpdateCheckService
                                .DownloadInstallerAsync(outcome.InstallerUrl!)
                                .ConfigureAwait(false);
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                            DispatcherQueue.TryEnqueue(() => _onExit());
                        }
                        catch (Exception ex)
                        {
                            NativeMethods.Warn(
                                $"Download failed:\n{ex.Message}\n\nTry updating from the releases page.",
                                AppName);
                            Process.Start(new ProcessStartInfo(outcome.ReleaseUrl) { UseShellExecute = true });
                        }
                    });
                    break;

                case NativeMethods.UpdateAction.ShowReleases:
                    Open(outcome.ReleaseUrl);
                    break;
            }
        }
        else if (outcome.Status == UpdateCheckService.UpdateStatus.NoReleases)
            NativeMethods.Info("No releases have been published yet.", AppName);
        else if (outcome.Status == UpdateCheckService.UpdateStatus.UpToDate)
            NativeMethods.Info($"You're on the latest version (v{running}).", AppName);
        else
            NativeMethods.Warn("Could not check for updates. Check your internet connection.", AppName);
    }

    private static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
