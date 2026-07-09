using Microsoft.Win32;
using LenovoTray.Services;

namespace LenovoTray.Helpers;

/// <summary>
/// Self-registers Windows Error Reporting "LocalDumps" for this exe so that a NATIVE crash — or
/// an external termination that Windows itself attributes to a fault — produces a usermode
/// minidump on disk. Those faults bypass .NET's managed exception handlers entirely (nothing
/// reaches AppDomain.UnhandledException, so app.log stays silent), which is exactly the signature
/// seen twice now (2026-07-03, 2026-07-05): the tray icon vanishes on a dock/display-topology
/// change with zero trace anywhere — no ProcessExit log line, no crash.log entry, no Application-log
/// event, and no WER report at all. A registered LocalDumps entry forces Windows to write a
/// minidump for ANY unhandled fault in this process, which is the missing piece needed to root-
/// cause that signature next time it happens.
///
/// Writing under HKLM needs admin; the app is requireAdministrator, so this succeeds at startup.
/// Entirely best-effort — any failure (e.g. a non-elevated run) is swallowed.
/// Docs: https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps
/// </summary>
internal static class CrashDumps
{
    private const string ExeName = "LenovoTray.exe";
    private const string LocalDumpsKey =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + ExeName;
    private const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + ExeName;
    private const string SilentExitKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\" + ExeName;

    // FLG_MONITOR_SILENT_PROCESS_EXIT — makes the kernel report terminations of this exe that
    // never reach the exception path (TerminateProcess by another process or by our own native
    // frameworks). OR'd into GlobalFlag so any other diagnostic bits already set there survive.
    private const int FlgMonitorSilentProcessExit = 0x200;

    /// <summary>
    /// Registers a minidump-on-crash for this exe into <paramref name="dumpDir"/>. Never throws.
    /// Logs whether it actually armed — given this whole file exists to root-cause a bug that has
    /// already resisted two occurrences of instrumentation, "did this even take effect this run" is
    /// the one thing worth confirming; a silent failure (e.g. a policy blocking HKLM writes) would
    /// otherwise be indistinguishable from "it armed correctly but the fault class never occurred."
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
    /// Arms Windows "silent process exit" monitoring for this exe. LocalDumps above only fires on
    /// an unhandled FAULT; the dock-undock kill (forensics 2026-07-06 + 2026-07-08: "Host window
    /// closed" in the same second as Kernel-Power "Power source change", then nothing — no
    /// ShutdownStarting, no ProcessExit, dumps folder empty) is a fault-less native termination,
    /// which LocalDumps can structurally never catch. This monitor covers that class: any
    /// TerminateProcess-style exit produces Application-log Event 3001
    /// (Microsoft-Windows-ProcessExitMonitor) naming the process that issued the kill, plus a
    /// minidump of this process in <paramref name="dumpDir"/>. If a future kill leaves NO 3001
    /// event either, the exit was a plain native ExitProcess from inside the framework — that
    /// distinction is exactly what this instrumentation exists to make.
    /// </summary>
    internal static void TryRegisterSilentExitMonitor(string dumpDir)
    {
        try
        {
            Directory.CreateDirectory(dumpDir);
            using (var sub = Registry.LocalMachine.CreateSubKey(SilentExitKey))
            {
                if (sub is null)
                {
                    AppLog.Info($"CrashDumps: CreateSubKey returned null for {SilentExitKey} — not armed.");
                    return;
                }
                sub.SetValue("ReportingMode", 6, RegistryValueKind.DWord); // 2 = local dump | 4 = Event 3001
                sub.SetValue("LocalDumpFolder", dumpDir, RegistryValueKind.String);
                sub.SetValue("DumpType", 1, RegistryValueKind.DWord); // 1 = mini (all thread stacks)
            }
            using (var ifeo = Registry.LocalMachine.CreateSubKey(IfeoKey))
            {
                if (ifeo is null)
                {
                    AppLog.Info($"CrashDumps: CreateSubKey returned null for {IfeoKey} — not armed.");
                    return;
                }
                int flags = ifeo.GetValue("GlobalFlag") is int existing ? existing : 0;
                ifeo.SetValue("GlobalFlag", flags | FlgMonitorSilentProcessExit, RegistryValueKind.DWord);
            }
            AppLog.Info($"CrashDumps: SilentProcessExit monitor armed -> {dumpDir}");
        }
        catch (Exception ex)
        {
            AppLog.Error("CrashDumps.TryRegisterSilentExitMonitor", ex);
        }
    }
}
