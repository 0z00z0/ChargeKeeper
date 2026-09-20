using System.Text.Json;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>What the version that starts next can say about the update the previous one began.</summary>
internal enum UpdateVerdict
{
    /// <summary>No update was handed over, so there is nothing to report. The zero value.</summary>
    NothingHandedOver,

    /// <summary>The running version is the one the update was for, or newer. Reported by the
    /// "What's new" window rather than a second message of its own.</summary>
    Installed,

    /// <summary>The update was started and the running version is still the old one.</summary>
    DidNotComplete,
}

/// <summary>
/// The handover between the application and the Setup run it starts for its own update. Setup runs
/// unattended and replaces files this process holds, so the process that asked for the update is
/// gone before an outcome exists: the record left here is how the version that starts next states
/// whether the update landed.
/// </summary>
/// <remarks>The installer script writes <see cref="RefusalFileName"/> and reads
/// <see cref="StartedByApplicationSwitch"/>. Neither side can read the other's constants, so the
/// pair is pinned by <c>Tests\UnattendedUpdateTests.cs</c>.</remarks>
internal static class UnattendedUpdate
{
    /// <summary>Names this flow to Setup, which cannot otherwise tell it from a winget or scheduled
    /// run carrying the same silent switches. Setup reads it as <c>{param:UPDATEFROMAPP}</c>.</summary>
    internal const string StartedByApplicationSwitch = "/UPDATEFROMAPP=1";

    /// <summary>The record the outgoing version leaves for its successor.</summary>
    internal const string HandoverFileName = "update-handover.json";

    /// <summary>Setup's own reason for installing nothing, written by the installer script at the
    /// one point it refuses. One ASCII line.</summary>
    internal const string RefusalFileName = "update-refused.txt";

    /// <summary>Setup's log. An unattended run leaves no other trace of what it did.</summary>
    internal const string InstallerLogFileName = "update-install.log";

    /// <summary>The version an update was started for, and when.</summary>
    internal sealed record Handover
    {
        /// <summary>Three-part, as the release tag states it.</summary>
        public string TargetVersion { get; init; } = "";

        /// <summary>Round-trip UTC. Read only to state how long ago the attempt was.</summary>
        public string StartedUtc { get; init; } = "";
    }

    internal static string HandoverPath     => AppPaths.DataFile(HandoverFileName);
    internal static string RefusalPath      => AppPaths.DataFile(RefusalFileName);
    internal static string InstallerLogPath => AppPaths.LogFile(InstallerLogFileName);

    /// <summary>
    /// What Setup is started with. <c>/SILENT</c> rather than <c>/VERYSILENT</c>: neither shows a
    /// wizard or a page to advance, which is the requirement, and the progress window is the only
    /// thing on screen in the seconds between the application closing and its successor starting.
    /// <c>/SUPPRESSMSGBOXES</c> because an error box raised under a silent run has no wizard behind
    /// it and no process left to own it; the outcome is reported by the version that starts next
    /// instead. <c>/LOG</c> because nothing else records what an unattended run did.
    /// </summary>
    /// <remarks>One string, because the shared update component hands the command line to Setup as
    /// it stands. The log path is quoted: a data folder can carry a space.</remarks>
    internal static string Arguments(string installerLogPath) =>
        $"/SILENT /SUPPRESSMSGBOXES /NORESTART {StartedByApplicationSwitch} /LOG=\"{installerLogPath}\"";

    /// <summary>Records the attempt before Setup is started, so a version that never gets the
    /// chance to write anything is still accounted for. Never throws.</summary>
    internal static void Record(string targetVersion)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            // Setup is handed a /LOG path inside it and is not relied on to create the folder.
            Directory.CreateDirectory(AppPaths.LogsDir);
            // A refusal from an earlier attempt says nothing about this one.
            Discard(RefusalPath);
            File.WriteAllText(HandoverPath, JsonSerializer.Serialize(new Handover
            {
                TargetVersion = targetVersion,
                StartedUtc    = DateTimeOffset.UtcNow.ToString("O"),
            }));
        }
        catch (Exception ex)
        {
            AppLog.Error("UnattendedUpdate.Record", ex);
        }
    }

    /// <summary>The record left by the version that started an update, or null where there is
    /// none — the ordinary state. Never throws.</summary>
    internal static Handover? Read()
    {
        try
        {
            if (!File.Exists(HandoverPath)) return null;
            return JsonSerializer.Deserialize<Handover>(File.ReadAllText(HandoverPath));
        }
        catch (Exception ex)
        {
            AppLog.Error("UnattendedUpdate.Read", ex);
            return null;
        }
    }

    /// <summary>Setup's stated reason for installing nothing, or null where it wrote none — which
    /// includes every failure that never reached Setup at all.</summary>
    internal static string? ReadRefusal()
    {
        try
        {
            return File.Exists(RefusalPath) ? File.ReadAllText(RefusalPath).Trim() : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("UnattendedUpdate.ReadRefusal", ex);
            return null;
        }
    }

    /// <summary>Drops the handover and any refusal beside it. Called once the outcome has been
    /// reported, so a single attempt is never reported twice.</summary>
    internal static void Clear()
    {
        Discard(HandoverPath);
        Discard(RefusalPath);
    }

    /// <summary>
    /// What the running version can say about the handed-over attempt. The version comparison is
    /// the evidence, not the installer's exit code: the process that could have read that code is
    /// the one Setup had to close.
    /// </summary>
    internal static UpdateVerdict VerdictFor(string? targetVersion, string runningVersion)
    {
        if (targetVersion is not { Length: > 0 }) return UpdateVerdict.NothingHandedOver;

        // An unparseable version on either side is not evidence of a failure, so it reports none.
        if (!Version.TryParse(targetVersion.TrimStart('v'), out var target) ||
            !Version.TryParse(runningVersion.TrimStart('v'), out var running))
            return UpdateVerdict.NothingHandedOver;

        return running >= target ? UpdateVerdict.Installed : UpdateVerdict.DidNotComplete;
    }

    /// <summary>The wording for an update that did not land, naming both versions and the one file
    /// that says more. Pure, so the text is readable without an update to fail.</summary>
    internal static string DidNotCompleteMessage(string targetVersion, string runningVersion,
                                                 string? refusal, string installerLogPath)
    {
        string reason = refusal is { Length: > 0 }
            ? $"\n\n{refusal}"
            : "";

        return $"The update to v{targetVersion} did not complete, and {AppInfo.Name} is still "
             + $"v{runningVersion}.{reason}\n\nTry again from the menu. The installer's own log is "
             + $"at {installerLogPath}.";
    }

    private static void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Error($"UnattendedUpdate.Discard '{path}'", ex); }
    }
}
