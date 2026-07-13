using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Keeps the tray app alive via Task Scheduler, and keeps Task Scheduler from being the thing
/// that kills it.
///
/// Root cause found 2026-07-08: the "LenovoTray AutoStart" logon task was created with plain
/// `schtasks /Create`, whose DEFAULTS include StopIfGoingOnBatteries=true (Task Scheduler
/// hard-terminates the instance the second AC power drops — i.e. at undock, matching the
/// same-second "Power source change" ↔ "Host window closed" ↔ dead-process forensics of
/// 2026-07-06 and 2026-07-08), DisallowStartIfOnBatteries=true (no auto-start on battery), and
/// an implicit 72h ExecutionTimeLimit (silent kill after 3 days of uptime). The scheduler's
/// stop sequence — close the windows politely, then TerminateProcess — is exactly the observed
/// signature: window-Closed log lines, then nothing (no ShutdownStarting, no ProcessExit, no
/// WER, no dump). The CLI has no switches for those settings, so both tasks here are registered
/// from full XML instead.
///
/// Two tasks are maintained at startup (best-effort, elevated app so no UAC):
///  1. "ChargeKeeper AutoStart" — repaired in place (power-safe settings) when it exists and
///     points at THIS exe; never created here (running at startup stays the user's choice).
///  2. "ChargeKeeper Watchdog"  — created/refreshed: relaunches the exe with --watchdog-relaunch
///     every 5 minutes plus on session unlock and resume-from-standby. A probe that finds a
///     live instance exits instantly via the single-instance mutex; one that finds the
///     hold-marker (user chose Exit from the tray menu) stays down. This is the backstop for
///     the kill classes no in-process code can survive (external TerminateProcess, GPU/
///     compositor teardown) — self-heal in OnProcessExit depends on code in the dying process
///     running, which those kills skip by definition.
/// </summary>
internal static class WatchdogTask
{
    internal const string WatchdogArg = "--watchdog-relaunch";

    private const string AutoStartTaskName = "ChargeKeeper AutoStart";
    private const string WatchdogTaskName  = "ChargeKeeper Watchdog";

    // Bumping the def version forces a rewrite of both task definitions on next startup.
    private const string DefStamp = "[ChargeKeeper def-v1]";

    private static string HoldMarkerPath => AppPaths.DataFile("watchdog-hold.marker");

    internal static bool HoldMarkerExists => File.Exists(HoldMarkerPath);

    /// <summary>Written on tray-menu Exit so watchdog probes leave a deliberate exit alone.</summary>
    internal static void WriteHoldMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HoldMarkerPath)!);
            File.WriteAllText(HoldMarkerPath, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) { AppLog.Error("WatchdogTask.WriteHoldMarker", ex); }
    }

    /// <summary>Cleared on every deliberate start, so resurrection is re-armed.</summary>
    internal static void TryClearHoldMarker()
    {
        try { File.Delete(HoldMarkerPath); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Registers the watchdog task and repairs the AutoStart task. Never throws. Skipped
    /// entirely for non-installed runs (dev builds under bin\...) — a watchdog or startup task
    /// pointing at a build-output exe would resurrect stale dev binaries for weeks.
    /// </summary>
    internal static void TryEnsureTasks()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            // Two accepted install locations: fresh installs live in "...\ChargeKeeper";
            // installs upgraded across the Lenovo Power Tray -> ChargeKeeper rename keep the
            // old folder name (the installer reuses the AppId-recorded {app} directory).
            if (!exe.EndsWith(@"\ChargeKeeper\ChargeKeeper.exe", StringComparison.OrdinalIgnoreCase) &&
                !exe.EndsWith(@"\Lenovo Power Tray\ChargeKeeper.exe", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info("Watchdog: not running from the install directory — task registration skipped.");
                return;
            }

            string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            if (sid.Length == 0) return;

            EnsureWatchdogTask(exe, sid);
            RepairAutoStartTask(exe, sid);
        }
        catch (Exception ex) { AppLog.Error("WatchdogTask.TryEnsureTasks", ex); }
    }

    private static void EnsureWatchdogTask(string exe, string sid)
    {
        var (code, xml) = RunSchtasks($"/Query /TN \"{WatchdogTaskName}\" /XML");
        if (code == 0 && xml.Contains(DefStamp) && xml.Contains(exe, StringComparison.OrdinalIgnoreCase))
            return;   // current definition already registered

        string triggers = $"""
              <TimeTrigger>
                <Repetition>
                  <Interval>PT5M</Interval>
                  <StopAtDurationEnd>false</StopAtDurationEnd>
                </Repetition>
                <StartBoundary>2026-01-01T00:00:00</StartBoundary>
                <Enabled>true</Enabled>
              </TimeTrigger>
              <SessionStateChangeTrigger>
                <Enabled>true</Enabled>
                <StateChange>SessionUnlock</StateChange>
                <UserId>{sid}</UserId>
                <Delay>PT5S</Delay>
              </SessionStateChangeTrigger>
              <EventTrigger>
                <Enabled>true</Enabled>
                <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
                <Delay>PT15S</Delay>
              </EventTrigger>
        """;

        bool ok = RegisterTask(WatchdogTaskName,
            BuildTaskXml(WatchdogTaskName,
                $"Relaunches ChargeKeeper if its process is gone (probe exits instantly when it is running, or when the user exited via the tray menu). {DefStamp}",
                triggers, exe, WatchdogArg, sid));
        AppLog.Info(ok
            ? $"Watchdog: scheduled task '{WatchdogTaskName}' registered (5-min + unlock + resume probes)."
            : $"Watchdog: FAILED to register '{WatchdogTaskName}' — no external restart safety net this run.");
    }

    private static void RepairAutoStartTask(string exe, string sid)
    {
        var (code, xml) = RunSchtasks($"/Query /TN \"{AutoStartTaskName}\" /XML");
        if (code != 0) return;                                   // no task — user opted out of autostart
        if (xml.Contains(DefStamp)) return;                      // already repaired
        if (!xml.Contains(exe, StringComparison.OrdinalIgnoreCase))
        {
            // Task points at some other exe (e.g. an old install path) — leave it alone rather
            // than hijack it; the installer owns that transition.
            AppLog.Info("Watchdog: AutoStart task points at a different exe — repair skipped.");
            return;
        }

        string triggers = $"""
              <LogonTrigger>
                <Enabled>true</Enabled>
                <UserId>{sid}</UserId>
              </LogonTrigger>
        """;

        bool ok = RegisterTask(AutoStartTaskName,
            BuildTaskXml(AutoStartTaskName,
                $"Starts ChargeKeeper at logon, elevated, with power-safe settings. {DefStamp}",
                triggers, exe, arguments: null, sid));
        AppLog.Info(ok
            ? "Watchdog: AutoStart task repaired — StopIfGoingOnBatteries and the 72h execution "
              + "limit removed (schtasks defaults; they hard-killed the app at undock)."
            : "Watchdog: FAILED to repair the AutoStart task — undock may still kill task-started instances.");
    }

    /// <summary>
    /// Element order mirrors what `schtasks /Query /XML` exports on this OS (RegistrationInfo,
    /// Principals, Settings, Triggers, Actions) — /Create /XML validates against the schema
    /// sequence, and the exported order is the one form guaranteed to round-trip.
    /// </summary>
    private static string BuildTaskXml(
        string name, string description, string triggersXml, string exe, string? arguments, string sid)
    {
        string argumentsXml = arguments is null ? "" : $"\n      <Arguments>{arguments}</Arguments>";
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\{name}</URI>
            <Description>{description}</Description>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{sid}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Triggers>
        {triggersXml}
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>"{exe}"</Command>{argumentsXml}
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static bool RegisterTask(string name, string xml)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ChargeKeeperTask-{Guid.NewGuid():N}.xml");
        try
        {
            // UTF-16 with BOM to match the declaration — schtasks rejects a mismatch.
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var (code, output) = RunSchtasks($"/Create /TN \"{name}\" /XML \"{tmp}\" /F");
            if (code != 0)
                AppLog.Info($"Watchdog: schtasks /Create '{name}' exited {code}: {output.Trim()}");
            return code == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error($"WatchdogTask.RegisterTask({name})", ex);
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p is null) return (-1, "Process.Start returned null");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { } return (-1, "schtasks timed out"); }
        return (p.ExitCode, output);
    }
}
