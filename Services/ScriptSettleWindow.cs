namespace ChargeKeeper.Services;

/// <summary>What a settling window is kept for. A subject, not a trigger: a charger pulled out and
/// pushed back in is two different triggers on one subject, and a countdown per trigger would let
/// each of the pair run unhindered by the other — which is the flap this exists to stop.</summary>
internal enum ScriptSubject
{
    /// <summary>The power source: a charger connected or disconnected.</summary>
    Charger,

    /// <summary>The lid switch: shut or opened.</summary>
    Lid,
}

/// <summary>What each subject is called in a line, what its reading is called, and how its state
/// reads as a phrase. One table rather than the words restated at every sink.</summary>
internal static class ScriptSubjectLabels
{
    public static string For(ScriptSubject subject) =>
        subject == ScriptSubject.Charger ? "charger" : "lid";

    /// <summary>What the subject's own reading is called, for a line saying it could not be taken.</summary>
    public static string Reading(ScriptSubject subject) =>
        subject == ScriptSubject.Charger ? "power source" : "lid switch";

    /// <summary>A state as a line reads it. The charger's states are the machine's, not the
    /// charger's, because that is the reading a script is bound to.</summary>
    public static string State(ScriptTrigger trigger) => trigger switch
    {
        ScriptTrigger.MainsConnected    => "on mains",
        ScriptTrigger.MainsDisconnected => "on battery",
        ScriptTrigger.LidClosed         => "shut",
        _                               => "open",
    };

    /// <summary>Why an event was ignored.</summary>
    public static ActionCause SettlingCause(ScriptSubject subject) =>
        $"a settling window on the {For(subject)} still running";

    /// <summary>Why a window closed. The quiet is what ends it, so the quiet is the cause; the state
    /// it settled on is on the same line already and is not repeated here.</summary>
    public static ActionCause QuietCause() => "no further event arriving before the window passed";
}

/// <summary>How a settling window ended.</summary>
internal enum SettleEnding
{
    /// <summary>The state that outlasted the window is not the one that ran, so it runs now.</summary>
    TrailingRun,

    /// <summary>The state that outlasted the window is the one that already ran. Nothing runs, and
    /// that is worth one line rather than silence.</summary>
    AlreadyInThatState,

    /// <summary>The state could not be read at all, so nothing is claimed about it.</summary>
    StateUnreadable,
}

/// <summary>What an arriving event should do.</summary>
/// <param name="Run">Whether this event runs its scripts now. True only for the event that opens a
/// window.</param>
/// <param name="AnnounceIgnored">Whether this ignored event is the first of its window, and so the
/// one that writes the line. A burst of twenty must not write twenty near-identical lines.</param>
internal readonly record struct SettleArrival(bool Run, bool AnnounceIgnored);

/// <summary>A window that has passed, and what it decided.</summary>
/// <param name="Ignored">How many events the window swallowed, reported on this one line rather
/// than on a line each.</param>
internal readonly record struct SettleClosure(
    ScriptSubject Subject, ScriptTrigger? State, int Ignored, SettleEnding Ending);

/// <summary>
/// The settling window between script runs: the first event of a burst runs, the ones arriving
/// inside the window are swallowed and each keeps the countdown alive, and when the window passes
/// with nothing further the state that actually applies then is what runs.
/// </summary>
/// <remarks>
/// <para>Pure — no clock, no settings, no I/O — so the flap can be shown without a charger. The
/// state is read through a delegate at the moment the window closes rather than replayed from the
/// last swallowed event: the machine must end in the state that matches reality, not in the state
/// of whichever edge happened to win the race.</para>
/// <para>The countdown is measured from the last event, not from the last run. A run happens on a
/// thread of its own and takes as long as its script takes, so measuring from a run's end would let
/// a slow script widen its own hold-off by an amount nobody chose.</para>
/// <para>The network triggers keep no window here. Leaving a network is about the profile the
/// machine <em>was</em> on, which is not a state that can be read back, and
/// <see cref="NetworkProfileTransitions"/> already holds a settling window of its own for a reading
/// lost while docking. A second mechanism over the first would be two readings of one thing.</para>
/// </remarks>
internal sealed class ScriptSettleWindow
{
    /// <summary>How long the window runs where the document names no length.</summary>
    internal const int DefaultSeconds = 10;

    /// <summary>Which subject a trigger belongs to, or null where the trigger keeps no window.</summary>
    public static ScriptSubject? SubjectOf(ScriptTrigger trigger) => trigger switch
    {
        ScriptTrigger.MainsConnected or ScriptTrigger.MainsDisconnected => ScriptSubject.Charger,
        ScriptTrigger.LidClosed      or ScriptTrigger.LidOpened         => ScriptSubject.Lid,
        _                                                               => null,
    };

    private sealed class Window
    {
        public DateTimeOffset DueAt;
        public ScriptTrigger  LastRun;
        public int            Ignored;
        public bool           Announced;
    }

    private readonly Dictionary<ScriptSubject, Window> _open = [];

    /// <summary>When the earliest open window is due, or null where none is open.</summary>
    public DateTimeOffset? NextDueAt =>
        _open.Count == 0 ? null : _open.Values.Min(w => w.DueAt);

    /// <summary>Takes one arriving event. The first of a burst runs; the rest are swallowed and each
    /// pushes the countdown out afresh.</summary>
    public SettleArrival Observe(ScriptTrigger trigger, TimeSpan window, DateTimeOffset now)
    {
        if (SubjectOf(trigger) is not { } subject) return new SettleArrival(Run: true, AnnounceIgnored: false);

        if (!_open.TryGetValue(subject, out var open))
        {
            _open[subject] = new Window { DueAt = now + window, LastRun = trigger };
            return new SettleArrival(Run: true, AnnounceIgnored: false);
        }

        open.DueAt = now + window;
        open.Ignored++;
        bool announce = !open.Announced;
        open.Announced = true;
        return new SettleArrival(Run: false, AnnounceIgnored: announce);
    }

    /// <summary>
    /// The windows whose time has passed, and what each decided.
    /// </summary>
    /// <param name="readState">The subject's state as it is now. Null where it could not be read —
    /// a refusal is no evidence that nothing changed.</param>
    /// <param name="busy">Whether a run for that state is still in progress. The one-run-at-a-time
    /// gate is keyed on the script's identifier and still applies, so a trailing run that would land
    /// on a run in progress is held for another window rather than dropped: dropping it would leave
    /// the wrong state as the last word, which is the whole failure.</param>
    public IReadOnlyList<SettleClosure> Expire(
        DateTimeOffset now, TimeSpan window,
        Func<ScriptSubject, ScriptTrigger?> readState, Func<ScriptTrigger, bool> busy)
    {
        List<SettleClosure> closed = [];

        foreach (var subject in _open.Keys.ToList())
        {
            var open = _open[subject];
            if (open.DueAt > now) continue;

            // Nothing was swallowed, so nothing can have moved since the run that opened the window.
            // Closing silently is what keeps an ordinary lid close to one line.
            if (open.Ignored == 0) { _open.Remove(subject); continue; }

            if (readState(subject) is not { } state)
            {
                _open.Remove(subject);
                closed.Add(new SettleClosure(subject, null, open.Ignored, SettleEnding.StateUnreadable));
                continue;
            }

            if (state == open.LastRun)
            {
                _open.Remove(subject);
                closed.Add(new SettleClosure(subject, state, open.Ignored, SettleEnding.AlreadyInThatState));
                continue;
            }

            if (busy(state)) { open.DueAt = now + window; continue; }

            // The trailing run is itself a run, so the window reopens around it: a flap resuming a
            // moment later is settled rather than running unhindered.
            closed.Add(new SettleClosure(subject, state, open.Ignored, SettleEnding.TrailingRun));
            _open[subject] = new Window { DueAt = now + window, LastRun = state };
        }

        return closed;
    }
}
