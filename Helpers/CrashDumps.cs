using Microsoft.Win32;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Crash-forensics for this exe, pared back now that the bug it was built for is fixed.
///
/// Two mechanisms were armed while the "vanished tray icon, zero trace" deaths were unexplained
/// (2026-07-03 … 07-08): WER "LocalDumps" (a minidump on any unhandled FAULT) and a
/// "SilentProcessExit" monitor (a minidump + Event 3001 on any fault-LESS termination). The
/// SilentProcessExit monitor did its job — it pinned the root cause to Task Scheduler
/// hard-terminating the app at undock (StopIfGoingOnBatteries, now fixed in
/// <see cref="WatchdogTask"/>). But it fires on EVERY exit of this exe, and the watchdog probe
/// (<c>--watchdog-relaunch</c>) exits every 5 minutes, so it was writing an ~11 MB minidump per
/// probe — hundreds of megabytes a day of pure noise. So it is now RETIRED: no longer armed, and
/// <see cref="TryDisarmSilentExitMonitor"/> actively removes it from machines that already have it.
///
/// Only WER LocalDumps stays — it triggers solely on a genuine unhandled fault (never on the clean
/// probe exits) — and it is now OPT-IN on release builds: a shipped app should not be silently
/// writing minidumps of itself into a user's profile. Pass <c>/debug</c> (see <see cref="DebugArg"/>)
/// to arm it; debug builds arm it unconditionally. See <see cref="DumpsEnabled"/>.
///
/// Because the switch controls an HKLM registry key that OUTLIVES the process, "off" cannot simply
/// mean "don't arm" — a machine that once ran with dumps on would keep dumping forever. So a run
/// without the switch actively DISARMS (<see cref="TryDisarmLocalDumps"/>). That is the same lesson
/// the retired SilentProcessExit monitor taught: leaving a stale key behind is what caused the
/// hundreds-of-MB-a-day noise in the first place.
///
/// Writing under HKLM needs admin; the app is requireAdministrator, so this succeeds at startup.
/// Entirely best-effort — any failure (e.g. a non-elevated run) is swallowed.
/// Docs: https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps
/// </summary>
internal static class CrashDumps
{
    private const string ExeName = "ChargeKeeper.exe";

    /// <summary>
    /// Opt-in switch for crash-dump capture on release builds: <c>ChargeKeeper.exe /debug</c>.
    /// Matched case-insensitively — it is typed by a human, and Windows switches conventionally
    /// ignore case (unlike the machine-generated <see cref="WatchdogTask.WatchdogArg"/>).
    /// </summary>
    internal const string DebugArg = "/debug";

    /// <summary>
    /// Whether crash-dump capture should be armed for this run: always on debug builds, and only
    /// with <see cref="DebugArg"/> on release builds. When false the caller must DISARM rather than
    /// merely skip arming — see the class remarks.
    /// </summary>
    internal static bool DumpsEnabled =>
#if DEBUG
        true;
#else
        Environment.GetCommandLineArgs().Contains(DebugArg, StringComparer.OrdinalIgnoreCase);
#endif
    private const string LocalDumpsKey =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + ExeName;
    private const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + ExeName;
    private const string SilentExitKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\" + ExeName;

    // FLG_MONITOR_SILENT_PROCESS_EXIT — the GlobalFlag bit that turns the SilentProcessExit monitor
    // on. Only referenced now to clear it back off (see TryDisarmSilentExitMonitor).
    private const int FlgMonitorSilentProcessExit = 0x200;

    /// <summary>
    /// Registers a minidump-on-crash for this exe into <paramref name="dumpDir"/>. Never throws.
    /// Logs whether it actually armed — a silent HKLM-write failure (policy) would otherwise be
    /// indistinguishable from "armed fine but no fault has occurred."
    /// </summary>
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
            // needs admin / policy-restricted — best-effort only, but log it: see doc comment above.
            AppLog.Error("CrashDumps.TryRegisterLocalDumps", ex);
        }
    }

    /// <summary>
    /// Removes this exe's WER LocalDumps registration — the counterpart to
    /// <see cref="TryRegisterLocalDumps"/>, run whenever <see cref="DumpsEnabled"/> is false.
    /// Necessary because the key lives in HKLM and outlives the process: a machine that once ran a
    /// build (or a <c>/debug</c> session) that armed dumps would otherwise keep writing them
    /// forever, long after the switch was dropped. Never throws; idempotent.
    /// </summary>
    internal static void TryDisarmLocalDumps()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(LocalDumpsKey))
            {
                if (key is null) return;   // never armed on this machine — nothing to undo
            }
            Registry.LocalMachine.DeleteSubKeyTree(LocalDumpsKey, throwOnMissingSubKey: false);
            AppLog.Info($"CrashDumps: WER LocalDumps disarmed (no {DebugArg} switch).");
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryDisarmLocalDumps", ex);
        }
    }

    /// <summary>
    /// Removes the SilentProcessExit monitor this app used to arm. It fired a minidump on every exit
    /// of the exe — including the 5-minute watchdog probe — so on any machine that ran a version
    /// which armed it, ~11 MB dumps piled up ~288×/day until this runs. Deletes the monitor key and
    /// clears the GlobalFlag bit (dropping the now-empty IFEO value/subkey so no stray Image File
    /// Execution Options entry lingers for our exe). Never throws; idempotent.
    /// </summary>
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

            // Drop the IFEO subkey for our exe if disarming left it completely empty — it only ever
            // existed to hold that GlobalFlag bit. Read the emptiness under a handle that is closed
            // again BEFORE the delete (deleting a key while still holding a handle to it is fragile).
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

    /// <summary>
    /// Clears out the dump directory. The retired SilentProcessExit monitor left one ~11 MB
    /// SUBFOLDER per exit ("ChargeKeeper.exe-(PID-…)-…") — all of it benign watchdog-probe noise,
    /// so every subfolder is removed. WER LocalDumps writes flat .dmp files on genuine faults; those
    /// are kept, newest <paramref name="keepNewest"/> (matching WER's own DumpCount). Handling the
    /// two kinds separately means a real crash dump is never sacrificed to keep a newer noise
    /// folder. Never throws.
    /// </summary>
    internal static void TryCleanupOldDumps(string dumpDir, int keepNewest = 5)
    {
        try
        {
            var dir = new DirectoryInfo(dumpDir);
            if (!dir.Exists) return;

            // SilentProcessExit (retired) noise — every per-exit subfolder goes.
            foreach (var sub in dir.GetDirectories())
            {
                try { sub.Delete(recursive: true); }
                catch { /* best-effort */ }
            }

            // Genuine WER fault dumps — keep the newest few.
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
