using ChargeKeeper.Helpers;
using ZeroZero.Update;

namespace ChargeKeeper.Services;

/// <summary>
/// Starts Setup the way the shared component does, and records the handover on the way past. The
/// record has to go down as Setup starts and not before: Setup replaces the files this process
/// holds, so the process is gone before an outcome exists, while an offer the user declines must
/// leave nothing behind for the next start to report.
/// </summary>
internal sealed class UpdateHandoverLauncher : IInstallerLauncher
{
    private readonly IInstallerLauncher _inner = new ShellInstallerLauncher();

    public void Start(string path, string arguments)
    {
        if (VersionOf(path) is { } version) UnattendedUpdate.Record(version);
        else AppLog.Info("Update: starting Setup with no target version recorded.");

        _inner.Start(path, arguments);
    }

    /// <summary>The component names the download after <see cref="AppUpdates.InstallerAssetName"/>.</summary>
    internal static string? VersionOf(string installerPath)
    {
        string[] around = AppUpdates.InstallerAssetName.Split("{version}");
        string name = Path.GetFileName(installerPath);
        return name.Length > around[0].Length + around[1].Length
               && name.StartsWith(around[0], StringComparison.Ordinal)
               && name.EndsWith(around[1], StringComparison.Ordinal)
            ? name[around[0].Length..^around[1].Length]
            : null;
    }
}
