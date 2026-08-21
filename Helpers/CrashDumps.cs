using Microsoft.Win32;
using ChargeKeeper.Services;

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
    /// <summary>The shared parent of every app's LocalDumps registration. Its mere existence turns
    /// WER dump collection on MACHINE-WIDE, and <see cref="Registry.CreateSubKey(string)"/> on the
    /// per-exe path below creates it as a side effect — so arming ours opts the whole machine in, and
    /// <see cref="TryDisarmLocalDumps"/> drops the parent again when it is left empty.</summary>
    private const string LocalDumpsRoot =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    private const string LocalDumpsKey = LocalDumpsRoot + @"\" + ExeName;

    /// <summary>This app's pre-rename exe name. Its LocalDumps registration is still armed on every
    /// upgraded machine, and must go before <see cref="LocalDumpsRoot"/> can be seen as empty.</summary>
    private const string LegacyExeName = "LenovoTray.exe";
    private const string LegacyLocalDumpsKey = LocalDumpsRoot + @"\" + LegacyExeName;

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
            using var key = Registry.LocalMachine.CreateSubKey(LocalDumpsKey);
            if (key is null)
            {
                AppLog.Info($"CrashDumps: CreateSubKey returned null for {LocalDumpsKey} — not armed.");
                return;
            }
            key.SetValue("DumpFolder", dumpDir, RegistryValueKind.ExpandString);
            key.SetValue("DumpCount",  5, RegistryValueKind.DWord);
            key.SetValue("DumpType",   1, RegistryValueKind.DWord); // 1 = mini (small, has all thread stacks)
            AppLog.Info($"CrashDumps: WER LocalDumps armed -> {dumpDir}");
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
            bool removedOurs = TryDeleteSubKeyIfPresent(LocalDumpsKey);
            bool removedLegacy = TryDeleteSubKeyIfPresent(LegacyLocalDumpsKey);

            // Emptiness is read under a handle CLOSED before the delete — deleting a key while still
            // holding a handle to it is fragile.
            bool rootEmpty;
            using (var root = Registry.LocalMachine.OpenSubKey(LocalDumpsRoot))
                rootEmpty = root is not null && root.ValueCount == 0 && root.SubKeyCount == 0;
            if (rootEmpty)
            {
                Registry.LocalMachine.DeleteSubKey(LocalDumpsRoot, throwOnMissingSubKey: false);
                AppLog.Info("CrashDumps: removed the now-empty LocalDumps root (its mere presence " +
                            "enables WER dump collection machine-wide, for every application).");
            }

            if (removedOurs)   AppLog.Info("CrashDumps: WER LocalDumps disarmed (capture is not enabled).");
            if (removedLegacy) AppLog.Info($"CrashDumps: removed the stale {LegacyExeName} LocalDumps registration (pre-rename residue).");
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryDisarmLocalDumps", ex);
        }
    }

    /// <summary>Deletes <paramref name="path"/> if it exists; returns whether it did. Never throws.</summary>
    private static bool TryDeleteSubKeyIfPresent(string path)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(path))
        {
            if (key is null) return false;
        }
        Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return true;
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
    internal static void TryCleanupOldDumps(string dumpDir, int keepNewest = 5)
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

            foreach (var dmp in dir.GetFiles("*.dmp")
                                   .OrderByDescending(f => f.LastWriteTimeUtc)
                                   .Skip(keepNewest))
            {
                try { dmp.Delete(); }
                catch { /* best-effort — a dump still held open by WER is left for next time */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryCleanupOldDumps", ex);
        }
    }
}
