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
/// writing minidumps of itself into a user's profile. <c>ChargeKeeper.exe /debug</c> turns it on,
/// <c>ChargeKeeper.exe /debug off</c> turns it back off; debug builds arm it unconditionally.
/// See <see cref="DumpsEnabled"/>.
///
/// Because the switch controls an HKLM registry key that OUTLIVES the process, "off" cannot simply
/// mean "don't arm" — a machine that once ran with dumps on would keep dumping forever. So a run
/// with the intent OFF actively DISARMS (<see cref="TryDisarmLocalDumps"/>). That is the same lesson
/// the retired SilentProcessExit monitor taught: leaving a stale key behind is what caused the
/// hundreds-of-MB-a-day noise in the first place.
///
/// <para>The intent is PERSISTED (<see cref="MarkerPath"/>), never re-derived from this process's
/// argv. Reading argv at startup was tried first and could not work, for three independent reasons:
/// (1) the /debug process almost never reaches startup — the tray app is already running, so it
/// loses the single-instance mutex and exits before arming anything; (2) the "ChargeKeeper AutoStart"
/// logon task launches with NO arguments, indistinguishable from a user start, so every sign-in would
/// disarm the dumps — killing the reboot-to-reproduce workflow the switch exists for; (3) the intent
/// belongs to the MACHINE (as does the key it controls), not to one invocation. Persisting it means
/// <see cref="ApplyPolicy"/> is safe to run from ANY instance — user start, AutoStart, watchdog
/// probe, self-heal relaunch — because they all read the same stored answer instead of guessing from
/// how they happened to be spawned.</para>
///
/// Writing under HKLM needs admin; the app is requireAdministrator, so every launch (including a
/// bare <c>/debug</c> from a shell) is elevated by the time managed code runs.
/// Entirely best-effort — any failure (e.g. a non-elevated run) is swallowed.
/// Docs: https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps
/// </summary>
internal static class CrashDumps
{
    private const string ExeName = "ChargeKeeper.exe";

    /// <summary>
    /// Opt-in switch for crash-dump capture: <c>ChargeKeeper.exe /debug [on|off]</c>.
    /// Matched case-insensitively — it is typed by a human, and Windows switches conventionally
    /// ignore case (unlike the machine-generated <see cref="WatchdogTask.WatchdogArg"/>).
    /// </summary>
    internal const string DebugArg = "/debug";

    /// <summary>The one value after <see cref="DebugArg"/> that means "turn it back off".</summary>
    private const string DebugOffValue = "off";

    /// <summary>
    /// Where the armed intent lives: a marker file whose mere EXISTENCE means "capture is on"
    /// (<see cref="WatchdogTask.HoldMarkerPath"/>'s pattern, same folder, no content that matters).
    ///
    /// <para>Deliberately NOT a field in <see cref="AppSettings"/>, which is where it started. The
    /// tray app holds settings in memory and rewrites the WHOLE file on any <c>Save()</c>, so a
    /// running instance's stale copy would clobber a flag written underneath it by a separate
    /// <c>/debug</c> process: arm dumps, toggle any preset in the tray, and the setting is silently
    /// back off — with the next boot starting disarmed. That is precisely the reboot-to-reproduce
    /// case this switch exists for, so the flag cannot live anywhere a second process rewrites
    /// wholesale. A marker is its own file: the only writers are the two lines below, each touching
    /// nothing else, so no amount of unrelated settings traffic can reach it.</para>
    /// </summary>
    private static string MarkerPath => AppPaths.DataFile("crash-dumps-armed.marker");

    /// <summary>
    /// Creates or removes the marker at <paramref name="path"/>. Best-effort and idempotent (arming
    /// twice, or disarming what was never armed, are both no-ops), matching every other marker/
    /// registry write here — the caller has no console to report a failure to, so a failure is
    /// logged and swallowed. Takes the path rather than reading <see cref="MarkerPath"/> so the
    /// arm/disarm behaviour is testable without touching the real %AppData%.
    /// </summary>
    internal static void SetMarker(string path, bool arm)
    {
        try
        {
            if (arm)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Presence is the whole signal; the timestamp is only here to make the file
                // self-explaining to whoever finds it in the data folder.
                File.WriteAllText(path, DateTimeOffset.Now.ToString("O"));
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) { AppLog.Error($"CrashDumps.SetMarker(arm: {arm})", ex); }
    }

    /// <summary>What a command line asked us to do about crash dumps.</summary>
    internal enum DebugCommand
    {
        /// <summary>No <see cref="DebugArg"/> present — a normal launch, leave the stored intent alone.</summary>
        None,
        /// <summary>Turn crash-dump capture on and remember it.</summary>
        Arm,
        /// <summary>Turn crash-dump capture off and remember it.</summary>
        Disarm,
    }

    /// <summary>
    /// Reads the crash-dump intent out of a command line. Pure (no registry, no settings, no argv
    /// lookup of its own) so the arg-shape rules below are unit-testable.
    ///
    /// <para>Only the exact token <c>off</c> disarms. Anything else following <c>/debug</c> — a
    /// missing value, <c>on</c>, or an unrelated later switch — arms, because <c>/debug</c> alone is
    /// the form a user actually types and silently doing nothing would be the worst possible answer
    /// for a diagnostics switch. Deliberately NOT an error for an unknown value: this exe has no
    /// console attached (it is a windowed, elevated app), so there is nowhere to report a usage
    /// error TO.</para>
    /// </summary>
    /// <param name="args">A full command line, <see cref="Environment.GetCommandLineArgs"/>-shaped
    /// (element 0 is the exe path and is skipped like any other non-matching token).</param>
    internal static DebugCommand ParseDebugCommand(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, DebugArg, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return DebugCommand.None;

        bool off = i + 1 < args.Length &&
                   string.Equals(args[i + 1], DebugOffValue, StringComparison.OrdinalIgnoreCase);
        return off ? DebugCommand.Disarm : DebugCommand.Arm;
    }

    /// <summary>
    /// Handles <c>/debug</c> as a COMMAND — persist the intent, apply it to the registry now, and
    /// tell the caller to exit — returning false for a normal launch that should carry on booting.
    ///
    /// <para>It cannot be a launch MODE. The tray app running is this app's steady state, so a
    /// <c>/debug</c> invocation is overwhelmingly likely to lose the single-instance mutex and exit
    /// before any startup work happens; the switch would then never take effect at all. Running as a
    /// command sidesteps the mutex entirely (the caller invokes this BEFORE the guard) and needs no
    /// elevation dance of its own — the app manifest is requireAdministrator, so the shell has
    /// already elevated this process before Main, which is exactly what the HKLM write below
    /// needs.</para>
    ///
    /// <para>Applying the registry change here as well as storing it means the effect is immediate
    /// even while another instance owns the tray — that instance's own dumps are governed by the
    /// same machine-wide key, so it needs no notification and nothing has to be restarted.</para>
    /// </summary>
    /// <param name="args">This process's command line (<see cref="Environment.GetCommandLineArgs"/>).</param>
    /// <param name="dumpDir">Folder WER should write minidumps into.</param>
    /// <returns>True when this process was a <c>/debug</c> command and should now exit.</returns>
    internal static bool TryHandleDebugCommand(string[] args, string dumpDir)
    {
        var command = ParseDebugCommand(args);
        if (command == DebugCommand.None) return false;

        bool arm = command == DebugCommand.Arm;

        SetMarker(MarkerPath, arm);

        // Apply straight away rather than leaving it to the next startup: the whole point of the
        // switch is to arm dumps for a crash that may happen in the next minute.
        if (arm) TryRegisterLocalDumps(dumpDir);
        else     TryDisarmLocalDumps();

        AppLog.Info($"CrashDumps: '{DebugArg}{(arm ? "" : " " + DebugOffValue)}' command — capture " +
                    $"{(arm ? "ARMED" : "DISARMED")} and remembered; exiting without starting the tray.");
        return true;
    }

    /// <summary>
    /// Whether crash-dump capture should be armed for this run: always on debug builds, and on the
    /// user's stored intent — the presence of <see cref="MarkerPath"/> — otherwise. Stored, not read
    /// from argv: <see cref="CrashDumps"/>'s type doc has the three reasons why argv cannot answer
    /// this question, and <see cref="MarkerPath"/> has the reason it is a file of its own.
    /// </summary>
    private static bool DumpsEnabled =>
#if DEBUG
        true;
#else
        File.Exists(MarkerPath);
#endif

    /// <summary>
    /// Applies the whole crash-dump policy for this run, in the one place that owns the HKLM key.
    ///
    /// <para>Deliberately not "expose a bool and let the caller branch": the arm/disarm rule has two
    /// halves that are only safe together — "off" must ACTIVELY disarm, because the key outlives the
    /// process and a machine that once armed it would otherwise keep dumping forever.</para>
    ///
    /// <para>Safe to call from EVERY instance, however it was spawned. It used to take a
    /// "deliberateStart" flag so that a watchdog probe (which never carries <c>/debug</c>) could not
    /// disarm the user's dumps within 5 minutes of the very crash they were armed to capture — a
    /// guard that only existed because the intent was inferred from argv. Now that the intent is
    /// stored, a probe reads the same answer the user set and simply re-asserts it.</para>
    /// </summary>
    /// <param name="dumpDir">Folder WER should write minidumps into.</param>
    internal static void ApplyPolicy(string dumpDir)
    {
        if (DumpsEnabled) TryRegisterLocalDumps(dumpDir);
        else              TryDisarmLocalDumps();
    }
    /// <summary>
    /// The SHARED parent every app's LocalDumps registration hangs off. Its mere EXISTENCE turns WER
    /// dump collection on MACHINE-WIDE, at WER's defaults (%LocalAppData%\CrashDumps, DumpCount=10) —
    /// a per-exe subkey only overrides those defaults for that exe. Verified on the dev machine
    /// 2026-07-16: with a bare, value-less parent present, 84 MB of dumps had accumulated from four
    /// exes that have NO subkey of their own, including a Wacom driver and a Windows system process.
    ///
    /// <para>That makes this key radioactive: <see cref="Registry.CreateSubKey(string)"/> on the
    /// per-exe path below creates this parent as a side effect, so ARMING our own dumps silently
    /// opts the whole machine in. Hence <see cref="TryDisarmLocalDumps"/> drops the parent too —
    /// but ONLY when it is empty, since other apps (HyperVManagerTray) legitimately register here
    /// and deleting a shared key out from under them would break their dumps.</para>
    /// </summary>
    private const string LocalDumpsRoot =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    private const string LocalDumpsKey = LocalDumpsRoot + @"\" + ExeName;

    /// <summary>
    /// This app's pre-rename exe name (Lenovo Power Tray, v1.1.x and older). Versions under that
    /// name armed their own LocalDumps registration pointing at %AppData%\LenovoPowerTray\dumps —
    /// a folder the app has since migrated away from — and nothing ever removed it, so it is still
    /// armed on every upgraded machine (confirmed on the dev machine 2026-07-16). Swept up for the
    /// same reason the installer's [InstallDelete] sweeps the stale LenovoTray.* binaries. It also
    /// has to go before <see cref="LocalDumpsRoot"/> can ever be seen as empty.
    /// </summary>
    private const string LegacyExeName = "LenovoTray.exe";
    private const string LegacyLocalDumpsKey = LocalDumpsRoot + @"\" + LegacyExeName;

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
    /// <see cref="TryRegisterLocalDumps"/>, run whenever <see cref="DumpsEnabled"/> is false and by
    /// the <c>/debug off</c> command. Necessary because the key lives in HKLM and outlives the
    /// process: a machine that once ran a build (or a <c>/debug</c> session) that armed dumps would
    /// otherwise keep writing them forever, long after the intent was withdrawn.
    ///
    /// <para>Removing our own leaf is NOT enough. See <see cref="LocalDumpsRoot"/>: the shared
    /// parent that arming creates as a side effect switches dump collection on machine-wide, so
    /// leaving it behind means every OTHER crashing app keeps writing ~14 MB minidumps into the
    /// user's profile — the precise opposite of what gating dumps behind <c>/debug</c> is for. The
    /// parent is therefore dropped too, but ONLY when it is empty: other apps register there
    /// legitimately and deleting a shared key from under them would silently break their dumps.
    /// Same shape as <see cref="TryDisarmSilentExitMonitor"/>'s empty-IFEO cleanup.</para>
    ///
    /// <para>Never throws; idempotent.</para>
    /// </summary>
    internal static void TryDisarmLocalDumps()
    {
        try
        {
            bool removedOurs = TryDeleteSubKeyIfPresent(LocalDumpsKey);
            // Pre-rename residue: still armed on every machine upgraded from Lenovo Power Tray,
            // pointing at an %AppData% folder the app no longer uses. Ours to clean up.
            bool removedLegacy = TryDeleteSubKeyIfPresent(LegacyLocalDumpsKey);

            // Drop the shared parent only if WE emptied it — never touch it while another app's
            // registration (or a machine-wide DumpFolder/DumpType value) is still living there.
            // Read emptiness under a handle that is CLOSED before the delete: deleting a key while
            // still holding a handle to it is fragile (same care as TryDisarmSilentExitMonitor).
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
