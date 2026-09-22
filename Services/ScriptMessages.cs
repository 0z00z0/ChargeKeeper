namespace ChargeKeeper.Services;

/// <summary>
/// The sentences the log carries about a script: that one started, what it printed, how it ended,
/// and why one that should have run did not. Plain enough to read in a "what happened" list, so a
/// person can tell a script that was skipped from one that failed without reading code. Pure — no
/// clock, no I/O.
/// </summary>
internal static class ScriptMessages
{
    /// <summary>The clock time is written into the sentence as well as the entry's timestamp, so the
    /// line reads whole when copied out of the log on its own.</summary>
    public static string Started(string script, ActionCause cause, DateTime at) =>
        $"The script '{script}' started at {at:HH:mm:ss}, run by {cause}.";

    public static string Succeeded(string script, TimeSpan took) =>
        $"The script '{script}' ran for {Span(took)}: run completed without errors.";

    /// <summary>A run that ended by itself but counts as failed: a non-zero exit code, anything on
    /// the error stream, or both. The messages are the errors' own first lines, not their detail.</summary>
    public static string Failed(string script, TimeSpan took, int exitCode, IReadOnlyList<string> errors)
    {
        var why = new List<string>();
        if (exitCode != 0) why.Add($"it ended with exit code {exitCode}");
        if (errors.Count == 1) why.Add($"it reported an error: {errors[0]}");
        else if (errors.Count > 1) why.Add($"it reported {errors.Count} errors: {string.Join(" | ", errors)}");
        return $"The script '{script}' failed after {Span(took)}: {string.Join("; ", why)}";
    }

    public static string TimedOut(string script, TimeSpan limit) =>
        $"The script '{script}' failed: it was still running after {Span(limit)}, so it was ended. " +
        "Anything it had already started keeps running.";

    public static string DidNotStart(string script, string reason) =>
        $"The script '{script}' failed: it could not be started: {OneLine(reason)}";

    /// <summary>Recorded once per firing that lands on a run already in progress. A script that
    /// quietly swallows the events arriving while it runs is the failure nobody finds.</summary>
    public static string SkippedBecauseItIsRunning(string script, ActionCause cause) =>
        $"The script '{script}' was not run by {cause}: the previous run of it has not finished.";

    // ---- the settling window -------------------------------------------------------------------

    /// <summary>
    /// Written once per window, on the first event it swallows rather than on every one: a charger
    /// flapping for an hour would otherwise bury every other entry under near-identical lines. What
    /// the rest of the window swallowed is counted and reported on the line that closes it.
    /// </summary>
    public static string EventIgnored(ScriptTrigger trigger, ScriptSubject subject) =>
        $"The '{ScriptTriggerLabels.For(trigger)}' event was ignored, and the ones after it in this " +
        $"window are counted rather than listed{ScriptSubjectLabels.SettlingCause(subject).Clause}";

    /// <summary>The window passing on a state that is not the one that ran. What runs is decided by
    /// the reading taken at this moment, never by the events that were swallowed — the machine has
    /// to end in the state that matches reality.</summary>
    public static string SettledOnANewState(ScriptSubject subject, string state, int ignored) =>
        $"The settling window on the {ScriptSubjectLabels.For(subject)} closed after {Ignored(ignored)}; " +
        $"the reading taken now is {state}, and that is what decides what runs" +
        ScriptSubjectLabels.QuietCause().Clause;

    /// <summary>The window passing on the state that already ran. Worth a line rather than silence:
    /// a person who watched a charger flap has to be able to see why no script followed.</summary>
    public static string SettledOnTheSameState(ScriptSubject subject, string state, int ignored) =>
        $"The settling window on the {ScriptSubjectLabels.For(subject)} closed after {Ignored(ignored)}; " +
        $"the reading taken now is {state}, which already ran, so nothing runs" +
        ScriptSubjectLabels.QuietCause().Clause;

    /// <summary>The window passing with no reading to act on. A refusal is no evidence that nothing
    /// changed, so nothing is claimed and nothing runs.</summary>
    public static string SettledOnNoReading(ScriptSubject subject, int ignored) =>
        $"The settling window on the {ScriptSubjectLabels.For(subject)} closed after {Ignored(ignored)}, " +
        $"but the {ScriptSubjectLabels.Reading(subject)} could not be read, so nothing runs" +
        ScriptSubjectLabels.QuietCause().Clause;

    private static string Ignored(int count) =>
        count == 1 ? "1 ignored event" : $"{count} ignored events";

    /// <summary>What the script printed, on the entry that reports how it ended. Empty output says
    /// nothing rather than saying "nothing", which would be a line per run for no reading.</summary>
    public static string Output(string script, string output) =>
        $"What the script '{script}' printed:{Environment.NewLine}{output}";

    /// <summary>Appended where the capture reached its cap, so a short entry is not read as a short
    /// run.</summary>
    public static string Trimmed(int keptCharacters) =>
        $"[the rest is not kept — a script's output is recorded up to {keptCharacters} characters]";

    /// <summary>Why the failure notification says what it says. One line, because a notification
    /// carries a title and a body and nothing to click.</summary>
    public static string FailureNotice(string script, string reason) =>
        $"{script} — {reason}. The application log says what it printed.";

    /// <summary>A span as a sentence reads it: seconds under a minute, minutes above.</summary>
    private static string Span(TimeSpan took) =>
        took.TotalSeconds < 60
            ? $"{took.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} s"
            : $"{took.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} min";

    /// <summary>A Windows failure text can carry newlines, which would break the line into fragments
    /// the log reader cannot attribute.</summary>
    private static string OneLine(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length == 0 ? "no reason given." : flat;
    }
}
