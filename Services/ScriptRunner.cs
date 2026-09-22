using System.Diagnostics;
using System.Text;

namespace ChargeKeeper.Services;

/// <summary>How one run of a script ended.</summary>
internal enum ScriptOutcomeKind
{
    Succeeded,

    /// <summary>PowerShell ran and reported a non-zero exit code, or wrote to its error stream —
    /// which a non-terminating error does while the exit code stays 0.</summary>
    Failed,

    /// <summary>The time limit was reached and the run was ended.</summary>
    TimedOut,

    /// <summary>PowerShell could not be started at all.</summary>
    DidNotStart,
}

/// <summary>One run of one script: how it ended, what it printed, what it reported as errors, and how
/// long it took.</summary>
internal readonly record struct ScriptOutcome(
    ScriptOutcomeKind Kind, int ExitCode, string Output, TimeSpan Took, string Reason = "",
    IReadOnlyList<string>? Errors = null)
{
    public bool Ok => Kind == ScriptOutcomeKind.Succeeded;

    public IReadOnlyList<string> ErrorMessages => Errors ?? [];
}

/// <summary>
/// A started script, as the runner needs to see one: something to wait on, something to end, and
/// what it printed. Separated from the process itself so the one-run-at-a-time gate and the time
/// limit can be shown without starting PowerShell.
/// </summary>
internal interface IScriptProcess : IDisposable
{
    /// <summary>Waits up to <paramref name="limit"/> and reports whether the run ended within it.</summary>
    bool WaitForExit(TimeSpan limit);

    /// <summary>Ends the run. Only the PowerShell process, never what it started: a script whose
    /// whole purpose is to start a program must not have that program taken away from it.</summary>
    void Kill();

    int ExitCode { get; }

    /// <summary>What the run printed on its standard output, already capped.</summary>
    string Output { get; }

    /// <summary>What the run wrote to its error stream, already capped.</summary>
    string ErrorOutput { get; }
}

/// <summary>
/// Runs the named scripts bound to the application's own state changes. One run at a time per
/// script, a time limit, no console window, output to the log capped per run, and a failure
/// notification latched per script.
/// </summary>
/// <remarks>
/// Scripts run with the rights the application has, which are administrator rights, and a program a
/// script starts inherits them. Starting one as the signed-in person needs a token taken from the
/// shell and a process created with it — a mechanism nothing in this application has, and the same
/// one wanted for opening a file from the Settings page.
/// </remarks>
internal sealed class ScriptRunner
{
    /// <summary>How long a run is given before it is ended. Fixed rather than configurable, and
    /// stated on the Scripts page; the parameter exists so the limit can be driven in a test.</summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>How much of a run's output is kept. A script printing in a loop would otherwise
    /// crowd every other entry out of the log, which rotates by size.</summary>
    internal const int OutputCapCharacters = 4000;

    /// <summary>The runner the application uses. A second instance exists only in a test, which
    /// supplies its own way of starting a script.</summary>
    public static ScriptRunner Instance { get; } = new(WindowsPowerShell.Start);

    private readonly Func<ScriptDefinition, IScriptProcess> _start;

    // Keyed on the script's identifier, not its name: a name is editable and need not be unique, so
    // two scripts would otherwise share one gate and one latch.
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal ScriptRunner(Func<ScriptDefinition, IScriptProcess> start) => _start = start;

    /// <summary>Raised the first time a script fails, and not again for that script until one of its
    /// runs succeeds. The application wires it to a notification: one broken script must report once
    /// rather than on every firing.</summary>
    public event Action<string, string>? RunFailed;

    /// <summary>Runs every script bound to <paramref name="trigger"/>. Safe to call from an OS
    /// callback: nothing here waits on a run.</summary>
    public void Fire(ScriptTrigger trigger) => Fire(trigger, null, null);

    /// <summary>
    /// The same, for a trigger that names a network profile: only the scripts bound to that profile
    /// run. <paramref name="profileName"/> reaches the log alone — what is bound is the identifier.
    /// </summary>
    public void Fire(ScriptTrigger trigger, string? profileId, string? profileName)
    {
        var window = SettleWindow;

        // Off, or a trigger that keeps no window: the event runs as it always did.
        if (window <= TimeSpan.Zero || ScriptSettleWindow.SubjectOf(trigger) is not { } subject)
        {
            RunMatching(trigger, profileId, CauseFor(trigger, profileName));
            return;
        }

        SettleArrival arrival;
        lock (_settleGate) arrival = _settle.Observe(trigger, window, Now());

        if (arrival.Run) RunMatching(trigger, profileId, CauseFor(trigger, profileName));
        else if (arrival.AnnounceIgnored) AppLog.Info(ScriptMessages.EventIgnored(trigger, subject));

        ArmSettleTimer();
    }

    /// <summary>Starts every script the trigger matches. The scripts and the profile list are read
    /// together, so a run cannot be decided against half of one document and half of another.</summary>
    private void RunMatching(ScriptTrigger trigger, string? profileId, ActionCause cause)
    {
        var (scripts, profiles) = SettingsService.Read(
            s => (s.Scripts.ToList(), s.NetworkLocationRules.Select(r => r.Id).ToList()));

        foreach (var script in ScriptTriggerPolicy.Matching(scripts, trigger, profileId, profiles))
            Start(script, cause, TimeLimit);
    }

    /// <summary>What a trigger reads as in the line recording the run. The specific thing rather
    /// than its category: the profile that was joined, the direction the lid moved.</summary>
    internal static ActionCause CauseFor(ScriptTrigger trigger, string? profileName) => trigger switch
    {
        ScriptTrigger.MainsConnected    => ActionCause.Charger(connected: true),
        ScriptTrigger.MainsDisconnected => ActionCause.Charger(connected: false),
        ScriptTrigger.LidClosed         => ActionCause.Lid(closed: true),
        ScriptTrigger.LidOpened         => ActionCause.Lid(closed: false),
        _ => profileName is { Length: > 0 } named
             ? ActionCause.NetworkProfile(named, joined: trigger == ScriptTrigger.NetworkJoined)
             : $"the '{ScriptTriggerLabels.For(trigger)}' event",
    };

    // ---- the settling window -------------------------------------------------------------------

    private readonly ScriptSettleWindow _settle = new();
    private readonly Lock _settleGate = new();
    private System.Threading.Timer? _settleTimer;

    /// <summary>The clock, as a seam: the flap has to be shown without waiting ten seconds.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    /// <summary>How long a window runs. A seam for the same reason, and so a test need not write a
    /// settings document. Zero is a real choice and switches the settling off.</summary>
    internal Func<TimeSpan> SettleLength { get; set; } = () =>
        TimeSpan.FromSeconds(Math.Max(0, SettingsService.Read(s => s.ScriptSettleSeconds)
                                          ?? ScriptSettleWindow.DefaultSeconds));

    /// <summary>
    /// The subject's state as it is now, read fresh at the moment the window closes. Never the last
    /// event that was swallowed: a charger pulled out and pushed back in leaves the machine on mains,
    /// and replaying the disconnect would pause a sync client that should be running.
    /// </summary>
    internal Func<ScriptSubject, ScriptTrigger?> ReadState { get; set; } = LiveState;

    private TimeSpan SettleWindow => SettleLength();

    /// <summary>Reads the machine rather than a remembered event. The power source comes off the
    /// battery interface; the lid switch has no query interface at all, so the latest state Windows
    /// itself delivered is the current one.</summary>
    private static ScriptTrigger? LiveState(ScriptSubject subject) => subject switch
    {
        ScriptSubject.Charger => Windows.System.Power.PowerManager.BatteryStatus switch
        {
            Windows.System.Power.BatteryStatus.NotPresent => null,
            var status => Helpers.BatteryStatsFormatter.IsOnAC(status)
                          ? ScriptTrigger.MainsConnected
                          : ScriptTrigger.MainsDisconnected,
        },
        _ => ScriptLidTrigger.CurrentlyClosed switch
        {
            true  => ScriptTrigger.LidClosed,
            false => ScriptTrigger.LidOpened,
            null  => null,
        },
    };

    // One timer for the runner's lifetime, re-armed at whichever window is due first.
    private void ArmSettleTimer()
    {
        lock (_settleGate)
        {
            if (_settle.NextDueAt is not { } due)
            {
                _settleTimer?.Change(System.Threading.Timeout.InfiniteTimeSpan,
                                     System.Threading.Timeout.InfiniteTimeSpan);
                return;
            }

            var wait = due - Now();
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

            _settleTimer ??= new System.Threading.Timer(_ => OnSettleElapsed(), null,
                                                        System.Threading.Timeout.InfiniteTimeSpan,
                                                        System.Threading.Timeout.InfiniteTimeSpan);
            _settleTimer.Change(wait, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A window has passed. Driven by the timer, and called directly by a test that runs
    /// its own clock rather than waiting on one.</summary>
    internal void OnSettleElapsed()
    {
        IReadOnlyList<SettleClosure> closed;
        var window = SettleWindow;
        lock (_settleGate)
            closed = _settle.Expire(Now(), window, ReadState, RunInProgressFor);

        foreach (var closure in closed) Carry(closure);

        ArmSettleTimer();
    }

    /// <summary>Whether a script the trailing run would start is still going. The one-run-at-a-time
    /// gate is keyed on the script identifier and still applies, so the trailing run waits for
    /// another window rather than being refused: dropping it would leave the wrong state as the last
    /// word, which is the whole failure the window exists to stop.</summary>
    private bool RunInProgressFor(ScriptTrigger trigger)
    {
        var scripts = SettingsService.Read(s => s.Scripts.ToList());
        return ScriptTriggerPolicy.Matching(scripts, trigger).Any(script => IsRunning(script.Id));
    }

    private void Carry(SettleClosure closure)
    {
        if (closure.Ending == SettleEnding.StateUnreadable)
        {
            AppLog.Info(ScriptMessages.SettledOnNoReading(closure.Subject, closure.Ignored));
            return;
        }

        var state = closure.State!.Value;
        string phrase = ScriptSubjectLabels.State(state);

        if (closure.Ending == SettleEnding.AlreadyInThatState)
        {
            AppLog.Info(ScriptMessages.SettledOnTheSameState(closure.Subject, phrase, closure.Ignored));
            return;
        }

        AppLog.Info(ScriptMessages.SettledOnANewState(closure.Subject, phrase, closure.Ignored));
        RunMatching(state, null, ActionCause.SettledState(phrase));
    }

    /// <summary>
    /// Starts one run on a thread of its own and reports whether it started. A second firing while a
    /// run is in progress is skipped rather than queued, and said in the log — a slow script silently
    /// swallowing the events that arrive while it runs is the failure that never gets found.
    /// </summary>
    internal bool Start(ScriptDefinition script, ActionCause cause, TimeSpan limit)
    {
        lock (_gate)
        {
            if (!_running.Add(script.Id))
            {
                AppLog.Info(ScriptMessages.SkippedBecauseItIsRunning(script.DisplayName, cause));
                return false;
            }
        }

        AppLog.Info(ScriptMessages.Started(script.DisplayName, cause, DateTime.Now));

        // A dedicated thread rather than a pooled one: the run blocks for as long as the script
        // takes, and a pool thread parked for a minute costs every other queued item.
        Task.Factory.StartNew(() =>
        {
            try
            {
                Complete(script, Run(script, limit));
            }
            catch (Exception ex)
            {
                AppLog.Error($"ScriptRunner: the script '{script.DisplayName}' ended in a fault", ex);
                Complete(script, new ScriptOutcome(ScriptOutcomeKind.DidNotStart, 0, "", TimeSpan.Zero,
                                                   ex.Message));
            }
            finally
            {
                lock (_gate) _running.Remove(script.Id);
            }
        }, TaskCreationOptions.LongRunning);

        return true;
    }

    /// <summary>Whether a run of this script is in progress. The Settings page reads it to say so on
    /// the row rather than letting Run now look as though it did nothing.</summary>
    public bool IsRunning(string scriptId)
    {
        lock (_gate) return _running.Contains(scriptId);
    }

    /// <summary>
    /// One run, start to finish, on the calling thread. Owns the time limit: on reaching it the run
    /// is ended and that is what the outcome says, so a script that hangs cannot hold its own gate
    /// shut for good.
    /// </summary>
    internal ScriptOutcome Run(ScriptDefinition script, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();

        IScriptProcess process;
        try
        {
            process = _start(script);
        }
        catch (Exception ex)
        {
            return new ScriptOutcome(ScriptOutcomeKind.DidNotStart, 0, "", clock.Elapsed, ex.Message);
        }

        using (process)
        {
            if (!process.WaitForExit(limit))
            {
                process.Kill();
                return new ScriptOutcome(ScriptOutcomeKind.TimedOut, 0, process.Output, clock.Elapsed);
            }

            // The error stream decides as much as the exit code: a non-terminating error — a program
            // Start-Process could not find — leaves the exit code at 0.
            int exitCode = process.ExitCode;
            var errors = ScriptErrorText.Messages(process.ErrorOutput);
            var kind = exitCode == 0 && errors.Count == 0 ? ScriptOutcomeKind.Succeeded : ScriptOutcomeKind.Failed;
            return new ScriptOutcome(kind, exitCode, process.Output, clock.Elapsed, Errors: errors);
        }
    }

    /// <summary>Records how a run ended and reports a failure once. The latch clears on the first run
    /// of that script that succeeds, so a script that starts working stops warning.</summary>
    private void Complete(ScriptDefinition script, ScriptOutcome outcome)
    {
        string name = script.DisplayName;

        AppLog.Info(outcome.Kind switch
        {
            ScriptOutcomeKind.Succeeded => ScriptMessages.Succeeded(name, outcome.Took),
            ScriptOutcomeKind.Failed    => ScriptMessages.Failed(name, outcome.Took, outcome.ExitCode, outcome.ErrorMessages),
            ScriptOutcomeKind.TimedOut  => ScriptMessages.TimedOut(name, TimeLimit),
            _                           => ScriptMessages.DidNotStart(name, outcome.Reason),
        });

        if (outcome.Output.Length > 0) AppLog.Info(ScriptMessages.Output(name, outcome.Output));

        bool report;
        lock (_gate)
        {
            if (outcome.Ok) { _failing.Remove(script.Id); report = false; }
            else            { report = _failing.Add(script.Id); }
        }

        // Outside the lock — a subscriber shows a notification, which is a synchronous WinRT call.
        if (report) RunFailed?.Invoke(name, ReasonFor(outcome));
    }

    /// <summary>The failure as a notification body reads it: what went wrong, not what it printed.</summary>
    internal static string ReasonFor(ScriptOutcome outcome) => outcome.Kind switch
    {
        ScriptOutcomeKind.Failed when outcome.ErrorMessages.Count > 0
                                   => $"it reported an error: {outcome.ErrorMessages[0].TrimEnd('.')}",
        ScriptOutcomeKind.Failed   => $"it ended with exit code {outcome.ExitCode}",
        ScriptOutcomeKind.TimedOut => "it was still running at the time limit and was ended",
        _                          => "it could not be started",
    };

    /// <summary>
    /// Starts a script in Windows PowerShell with no console window and both streams captured.
    /// Windows PowerShell rather than PowerShell 7: it is present on every Windows installation and
    /// the other need not be, and guessing between them would make a script work on one machine and
    /// not the next.
    /// </summary>
    private sealed class WindowsPowerShell : IScriptProcess
    {
        private readonly Process       _process;
        private readonly string        _file;
        private readonly CappedText     _output = new();
        private readonly CappedText     _errors = new();

        public static IScriptProcess Start(ScriptDefinition script) => new WindowsPowerShell(script);

        private WindowsPowerShell(ScriptDefinition script)
        {
            _file = WriteScriptFile(script);

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // -File rather than -Command: a script arriving on a command line has to survive two
                // rounds of quoting, and a body holding a quote would be a different script by the
                // time PowerShell saw it.
                Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{_file}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, e) => _output.Append(e.Data);
            _process.ErrorDataReceived  += (_, e) => _errors.Append(e.Data);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public bool WaitForExit(TimeSpan limit)
        {
            if (!_process.WaitForExit((int)Math.Clamp(limit.TotalMilliseconds, 0, int.MaxValue)))
                return false;

            // The timed wait returns as soon as the process ends, before the asynchronous readers
            // have drained; the parameterless wait is what flushes them.
            _process.WaitForExit();
            return true;
        }

        public void Kill()
        {
            // The PowerShell process alone, deliberately not its children: a script's whole purpose
            // may be to start a program, and ending the tree would take that program away again.
            try { _process.Kill(entireProcessTree: false); }
            catch (Exception ex) { AppLog.Error("ScriptRunner: the run could not be ended", ex); }
        }

        public int ExitCode => _process.ExitCode;

        public string Output => _output.Text;

        public string ErrorOutput => _errors.Text;

        /// <summary>Writes the body to a file of its own beside the machine's other temporary files.
        /// UTF-8 with a byte order mark: Windows PowerShell reads a file without one as the machine's
        /// ANSI code page, which turns every accented character in a path or a message into
        /// something else.</summary>
        private static string WriteScriptFile(ScriptDefinition script)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ChargeKeeper");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"script-{script.Id}.ps1");
            File.WriteAllText(path, script.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }

        public void Dispose()
        {
            try { File.Delete(_file); } catch { /* best-effort cleanup */ }
            _process.Dispose();
        }
    }

    /// <summary>Starts a script the way the application does. For a test that has to see what Windows
    /// PowerShell itself writes, which a fake cannot tell it.</summary>
    internal static IScriptProcess StartInWindowsPowerShell(ScriptDefinition script) => WindowsPowerShell.Start(script);

    /// <summary>One stream's lines, capped. A script printing in a loop would otherwise crowd every
    /// other entry out of the log, which rotates by size.</summary>
    private sealed class CappedText
    {
        private readonly StringBuilder _text = new();
        private readonly Lock          _gate = new();
        private bool                   _trimmed;

        public string Text
        {
            get { lock (_gate) return _text.ToString(); }
        }

        public void Append(string? line)
        {
            if (line is null) return;

            lock (_gate)
            {
                if (_trimmed) return;

                int room = OutputCapCharacters - _text.Length;
                if (room <= 0)
                {
                    _trimmed = true;
                    _text.AppendLine(ScriptMessages.Trimmed(OutputCapCharacters));
                    return;
                }

                _text.AppendLine(line.Length <= room ? line : line[..room]);
            }
        }
    }
}
