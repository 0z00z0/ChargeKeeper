using Microsoft.Win32;
using ChargeKeeper.Services;
using ZeroZero.Diagnostics.Dumps;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Arms or disarms WER LocalDumps for this exe — a minidump on an unhandled fault. Always armed on
/// debug builds; on release builds only when <c>ChargeKeeper.exe /debug</c> has asked for it. Every
/// registry write needs admin (the manifest is requireAdministrator) and is best-effort.
/// Docs: https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps
/// </summary>
internal static class CrashDumps
{
    private const string ExeName = "ChargeKeeper.exe";

    /// <summary>Opt-in switch: <c>ChargeKeeper.exe /debug [on|off]</c>, matched case-insensitively.</summary>
    internal const string DebugArg = "/debug";

    private const string DebugOffValue = "off";

    /// <summary>The dump folder's name inside the Logs subfolder.</summary>
    internal const string DumpFolderName = "dumps";

    /// <summary>Where WER is told to write this exe's minidumps.</summary>
    internal static string DumpDir => AppPaths.LogFile(DumpFolderName);

    /// <summary>The armed intent — a marker file whose mere existence means "capture is on". A file
    /// of its own rather than an <see cref="AppSettings"/> field, because the tray app rewrites the
    /// whole settings file on save and would clobber a flag written under it.</summary>
    private static string MarkerPath => AppPaths.DataFile("crash-dumps-armed.marker");

    /// <summary>Creates or removes the marker at <paramref name="path"/>. Best-effort and idempotent;
    /// takes the path so arm/disarm is testable without touching the real %AppData%.</summary>
    internal static void SetMarker(string path, bool arm)
    {
        try
        {
            if (arm)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Presence is the signal; the timestamp only makes the file self-explaining.
                File.WriteAllText(path, DateTimeOffset.Now.ToString("O"));
            }
            else if (File.Exists(path))
            {
                // Guarded: deleting a missing file is a no-op, but a missing parent directory throws.
                File.Delete(path);
            }
        }
        catch (Exception ex) { AppLog.Error($"CrashDumps.SetMarker(arm: {arm})", ex); }
    }

    /// <summary>What a command line asked us to do about crash dumps.</summary>
    internal enum DebugCommand
    {
        None,
        Arm,
        Disarm,
    }

    /// <summary>Reads the crash-dump intent out of a command line. Only the exact token <c>off</c>
    /// disarms; anything else after <c>/debug</c> arms, because a windowed app has no console to
    /// report a usage error to.</summary>
    internal static DebugCommand ParseDebugCommand(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, DebugArg, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return DebugCommand.None;

        bool off = i + 1 < args.Length &&
                   string.Equals(args[i + 1], DebugOffValue, StringComparison.OrdinalIgnoreCase);
        return off ? DebugCommand.Disarm : DebugCommand.Arm;
    }

    /// <summary>Handles <c>/debug</c> as a command — persist the intent, apply it now, and return
    /// true so the caller exits. Must run BEFORE the single-instance guard: a <c>/debug</c> launch
    /// would otherwise lose the mutex to the running tray before the switch took effect.</summary>
    internal static bool TryHandleDebugCommand(string[] args, string dumpDir)
    {
        var command = ParseDebugCommand(args);
        if (command == DebugCommand.None) return false;

        bool arm = command == DebugCommand.Arm;

        SetMarker(MarkerPath, arm);

        // Apply now, not at the next startup — the crash to capture may be a minute away.
        if (arm) TryRegisterLocalDumps(dumpDir);
        else     TryDisarmLocalDumps();

        AppLog.Info($"CrashDumps: '{DebugArg}{(arm ? "" : " " + DebugOffValue)}' command — capture " +
                    $"{(arm ? "ARMED" : "DISARMED")} and remembered; exiting without starting the tray.");
        return true;
    }

    /// <summary>Armed on debug builds always, otherwise on the stored intent at <see cref="MarkerPath"/>.</summary>
    private static bool DumpsEnabled =>
#if DEBUG
        true;
#else
        File.Exists(MarkerPath);
#endif

    /// <summary>Applies the crash-dump policy for this run. "Off" must ACTIVELY disarm: the HKLM key
    /// outlives the process, so a machine that once armed it would otherwise keep dumping forever.</summary>
    internal static void ApplyPolicy(string dumpDir)
    {
        if (DumpsEnabled) TryRegisterLocalDumps(dumpDir);
        else              TryDisarmLocalDumps();
    }

    /// <summary>This app's pre-rename exe name. Its LocalDumps registration is still armed on every
    /// upgraded machine, and must go before the shared LocalDumps root can be seen as empty.</summary>
    private const string LegacyExeName = "LenovoTray.exe";

    /// <summary>How many minidumps WER keeps, and how many the start-up sweep leaves.</summary>
    private const int RetainedDumps = 5;

    private static readonly AppLogSink Log = new();

    /// <summary>The shared LocalDumps registration: it arms and disarms this exe's key, sweeps the
    /// legacy one, and drops the shared root once empty — its mere existence turns WER dump
    /// collection on machine-wide, for every application.</summary>
    private static DumpRegistration Registration => new(Registry.LocalMachine, Log);

    private const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + ExeName;
    private const string SilentExitKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\" + ExeName;

    // FLG_MONITOR_SILENT_PROCESS_EXIT — the GlobalFlag bit that enables the SilentProcessExit monitor.
    private const int FlgMonitorSilentProcessExit = 0x200;

    /// <summary>Registers a minidump-on-crash for this exe into <paramref name="dumpDir"/>. Never throws.</summary>
    internal static void TryRegisterLocalDumps(string dumpDir)
    {
        try
        {
            Directory.CreateDirectory(dumpDir);
            Registration.Arm(new DumpPolicy(ExeName, dumpDir, DumpType.Mini, RetainedDumps));
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryRegisterLocalDumps", ex);
        }
    }

    /// <summary>Removes this exe's WER LocalDumps registration, the legacy one, and the shared parent
    /// when that is left empty — never while another app's registration still lives there. Never
    /// throws; idempotent.</summary>
    internal static void TryDisarmLocalDumps()
    {
        try
        {
            var registration = Registration;
            registration.Disarm(ExeName);
            registration.RemoveResidue(LegacyExeName);
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryDisarmLocalDumps", ex);
        }
    }

    /// <summary>Removes the SilentProcessExit monitor left behind by earlier versions, clearing its
    /// GlobalFlag bit and dropping the IFEO subkey when that leaves it empty. The monitor writes a
    /// minidump on every exit of the exe, watchdog probes included. Never throws; idempotent.</summary>
    internal static void TryDisarmSilentExitMonitor()
    {
        bool changed = false;
        try
        {
            using (var sub = Registry.LocalMachine.OpenSubKey(SilentExitKey))
            {
                if (sub is not null) { Registry.LocalMachine.DeleteSubKeyTree(SilentExitKey); changed = true; }
            }

            using (var ifeo = Registry.LocalMachine.OpenSubKey(IfeoKey, writable: true))
            {
                if (ifeo is not null && ifeo.GetValue("GlobalFlag") is int flags &&
                    (flags & FlgMonitorSilentProcessExit) != 0)
                {
                    int cleared = flags & ~FlgMonitorSilentProcessExit;
                    if (cleared == 0) ifeo.DeleteValue("GlobalFlag", throwOnMissingValue: false);
                    else ifeo.SetValue("GlobalFlag", cleared, RegistryValueKind.DWord);
                    changed = true;
                }
            }

            // Emptiness is read under a handle CLOSED before the delete — deleting a key while still
            // holding a handle to it is fragile.
            bool ifeoEmpty;
            using (var ifeo = Registry.LocalMachine.OpenSubKey(IfeoKey))
                ifeoEmpty = ifeo is not null && ifeo.ValueCount == 0 && ifeo.SubKeyCount == 0;
            if (ifeoEmpty)
                Registry.LocalMachine.DeleteSubKey(IfeoKey, throwOnMissingSubKey: false);

            if (changed)
                AppLog.Info("CrashDumps: SilentProcessExit monitor disarmed (was dumping on every watchdog probe exit).");
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryDisarmSilentExitMonitor", ex);
        }
    }

    /// <summary>Clears out the dump directory. Every subfolder goes — those are per-exit
    /// SilentProcessExit noise — while the flat .dmp files WER writes on genuine faults are kept,
    /// newest <paramref name="keepNewest"/>. Never throws.</summary>
    internal static void TryCleanupOldDumps(string dumpDir, int keepNewest = RetainedDumps)
    {
        try
        {
            var dir = new DirectoryInfo(dumpDir);
            if (!dir.Exists) return;

            foreach (var sub in dir.GetDirectories())
            {
                try { sub.Delete(recursive: true); }
                catch { /* best-effort */ }
            }

            // A dump still held open by WER is logged and left for next time.
            DumpRetention.Prune(dumpDir, ExeName, keepNewest, Log);
            DumpRetention.Prune(dumpDir, LegacyExeName, keepNewest, Log);
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryCleanupOldDumps", ex);
        }
    }
}
