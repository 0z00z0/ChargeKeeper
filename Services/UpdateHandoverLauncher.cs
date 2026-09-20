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

    /// <summary>The release an install is running for, set before the flow is started.</summary>
    internal string? TargetVersion { get; set; }

    public void Start(string path, string arguments)
    {
        if (TargetVersion is { Length: > 0 } version) UnattendedUpdate.Record(version);
        else AppLog.Info("Update: starting Setup with no target version recorded.");

        _inner.Start(path, arguments);
    }
}
