namespace ChargeKeeper.Services;

/// <summary>
/// The sentences the log carries about a script: that one started, what it printed, how it ended,
/// and why one that should have run did not. Plain enough to read in a "what happened" list, so a
/// person can tell a script that was skipped from one that failed without reading code. Pure — no
/// clock, no I/O.
/// </summary>
internal static class ScriptMessages
{
    public static string Started(string script, string cause) =>
        $"The script '{script}' started, run by {cause}.";

    public static string Succeeded(string script, TimeSpan took) =>
        $"The script '{script}' finished after {Span(took)}.";

    public static string FailedWithExitCode(string script, int exitCode, TimeSpan took) =>
        $"The script '{script}' ended with exit code {exitCode} after {Span(took)}, which counts as a failure.";

    public static string TimedOut(string script, TimeSpan limit) =>
        $"The script '{script}' was still running after {Span(limit)}, so it was ended. Anything it " +
        "had already started keeps running.";

    public static string DidNotStart(string script, string reason) =>
        $"The script '{script}' could not be started: {OneLine(reason)}";

    /// <summary>Recorded once per firing that lands on a run already in progress. A script that
    /// quietly swallows the events arriving while it runs is the failure nobody finds.</summary>
    public static string SkippedBecauseItIsRunning(string script, string cause) =>
        $"The script '{script}' was not run by {cause}: the previous run of it has not finished.";

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
