using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>How a focus session ended. The three ways a session can leave the engine, and the words
/// the history file holds.</summary>
/// <remarks>The words are written into a file a person reads, so they are a stored vocabulary rather
/// than a display choice: renaming one makes every earlier row unreadable.</remarks>
internal enum FocusSessionOutcome
{
    /// <summary>The session reached the end time it was armed for.</summary>
    RanToTime,

    /// <summary>Home Assistant's staged cancel was confirmed inside its window.</summary>
    EndedEarly,

    /// <summary>The end time had already passed when the application next started, so nothing was
    /// running to notice it go.</summary>
    FoundStale,
}

/// <summary>One finished session, as the history file holds it.</summary>
/// <param name="DueAt">The end time the session was armed for, which a session ended early never
/// reached.</param>
/// <param name="EndedAt">When the session actually left the engine.</param>
internal readonly record struct FocusHistoryEntry(
    DateTimeOffset StartedAt, DateTimeOffset DueAt, DateTimeOffset EndedAt,
    bool BlockedNetwork, bool DimmedScreen, bool CoveredScreen, bool BlockedInput,
    FocusSessionOutcome Outcome);

/// <summary>
/// The record of finished focus sessions, one row per session, beside the three sampled histories in
/// the data folder's History subfolder.
/// </summary>
/// <remarks>
/// The same <see cref="CsvSampleStore"/> the sampled histories use, so the file reads the same way
/// and needs no reader of its own. A session is at most four hours, so the rate is a handful of rows
/// a day at the very most and the file stays small at any plausible use.
/// </remarks>
internal static class FocusHistoryService
{
    /// <summary>A year of sessions is long enough to look back over a project, and the row cap holds
    /// the file to a few tens of kilobytes whatever the rate.</summary>
    internal const int RetentionDays = 365;

    internal const int MaxRows = 500;

    internal const string FileName = "focus-history.csv";

    internal static readonly string HeaderComment =
        "# ChargeKeeper focus-session history — one row per finished session, kept for " +
        RetentionDays.ToString(CultureInfo.InvariantCulture) + " days. " +
        "started/due/ended = ISO 8601 with local UTC offset; " +
        "due = the end time the session was armed for; " +
        "levers = network, screen and cover, joined with +, or none; " +
        "outcome = ran-to-time, ended-early or found-stale.";
    internal const string HeaderColumns = "started,due,ended,levers,outcome";
    internal static readonly string Header = HeaderComment + "\n" + HeaderColumns;

    private static readonly CsvSampleStore _store = new(FileName, Header);

    private static readonly Lock _lock = new();

    private static DateTime? _prunedOnLocalDate;

    public static string FilePath => _store.FilePath;

    /// <summary>Test-only seam: an isolated file, and a reset of the once-a-day prune that is
    /// otherwise static and would leak from one test to the next.</summary>
    internal static void UseTestPath(string path)
    {
        lock (_lock)
        {
            _store.UseTestPath(path);
            _prunedOnLocalDate = null;
        }
    }

    /// <summary>Appends one finished session. Safe to call from the engine's own lock; never
    /// throws.</summary>
    public static void Record(FocusHistoryEntry entry)
    {
        lock (_lock)
        {
            try
            {
                _store.AppendLine(Format(entry));
                Prune();
            }
            catch (Exception ex)
            {
                // A lost row costs one session's record, which is never worth taking the
                // application down for.
                AppLog.Error("FocusHistoryService.Record", ex);
            }
        }
    }

    /// <summary>The most recent sessions, newest first. Nothing when the file does not exist
    /// yet.</summary>
    public static IReadOnlyList<FocusHistoryEntry> Recent(int count)
    {
        if (count <= 0) return [];

        lock (_lock)
        {
            var found = new List<FocusHistoryEntry>();
            try
            {
                foreach (string line in _store.ReadAllLines())
                    if (TryParse(line, out var entry)) found.Add(entry);
            }
            catch (Exception ex)
            {
                AppLog.Error("FocusHistoryService.Recent", ex);
            }

            found.Reverse();
            return found.Count > count ? found.GetRange(0, count) : found;
        }
    }

    /// <summary>Drops rows past retention, at most once per local day. Called under the
    /// lock.</summary>
    private static void Prune()
    {
        var today = DateTime.Now.Date;
        if (_prunedOnLocalDate == today) return;
        _prunedOnLocalDate = today;

        var cutoff = DateTimeOffset.Now - TimeSpan.FromDays(RetentionDays);
        _store.Prune(
            line => !TryParse(line, out var entry) ? CsvRowVerdict.NotARow
                  : entry.EndedAt < cutoff          ? CsvRowVerdict.Expired
                                                    : CsvRowVerdict.Keep,
            MaxRows);
    }

    /// <summary>The word each outcome is stored as.</summary>
    internal static string Word(FocusSessionOutcome outcome) => outcome switch
    {
        FocusSessionOutcome.RanToTime  => "ran-to-time",
        FocusSessionOutcome.EndedEarly => "ended-early",
        _                              => "found-stale",
    };

    /// <summary>The outcome a stored word names, or null for a word from no version of this
    /// file.</summary>
    internal static FocusSessionOutcome? Outcome(string word) => word switch
    {
        "ran-to-time"  => FocusSessionOutcome.RanToTime,
        "ended-early"  => FocusSessionOutcome.EndedEarly,
        "found-stale"  => FocusSessionOutcome.FoundStale,
        _              => null,
    };

    /// <summary>The levers a session owned, joined with a plus because a comma separates the
    /// columns.</summary>
    internal static string Levers(bool network, bool screen, bool cover, bool input)
    {
        var levers = new List<string>(4);
        if (network) levers.Add("network");
        if (screen)  levers.Add("screen");
        if (cover)   levers.Add("cover");
        if (input)   levers.Add("input");
        return levers.Count > 0 ? string.Join('+', levers) : "none";
    }

    internal static string Format(FocusHistoryEntry entry) => string.Join(',',
        Stamp(entry.StartedAt), Stamp(entry.DueAt), Stamp(entry.EndedAt),
        Levers(entry.BlockedNetwork, entry.DimmedScreen, entry.CoveredScreen, entry.BlockedInput),
        Word(entry.Outcome));

    private static string Stamp(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    internal static bool TryParse(string line, out FocusHistoryEntry entry)
    {
        entry = default;
        string[] parts = line.Split(',');
        if (parts.Length < 5) return false;

        var ci = CultureInfo.InvariantCulture;
        if (!DateTimeOffset.TryParse(parts[0], ci, DateTimeStyles.RoundtripKind, out var started)) return false;
        if (!DateTimeOffset.TryParse(parts[1], ci, DateTimeStyles.RoundtripKind, out var due)) return false;
        if (!DateTimeOffset.TryParse(parts[2], ci, DateTimeStyles.RoundtripKind, out var ended)) return false;
        if (Outcome(parts[4]) is not { } outcome) return false;

        string[] levers = parts[3].Split('+');
        entry = new FocusHistoryEntry(
            started, due, ended,
            levers.Contains("network"), levers.Contains("screen"), levers.Contains("cover"),
            levers.Contains("input"), outcome);
        return true;
    }

    /// <summary>One finished session in a line, for the Settings page. The date, how long it ran,
    /// which levers it used and how it ended.</summary>
    public static string Describe(FocusHistoryEntry entry)
    {
        var ran = entry.EndedAt - entry.StartedAt;
        string levers = Levers(entry.BlockedNetwork, entry.DimmedScreen, entry.CoveredScreen,
                               entry.BlockedInput).Replace('+', ',');
        return string.Create(CultureInfo.CurrentCulture,
            $"{entry.StartedAt.ToLocalTime():d MMM HH:mm} — {Math.Max(0, (int)ran.TotalMinutes)} min, " +
            $"{levers}, {Ending(entry.Outcome)}");
    }

    /// <summary>How the ending reads on the page, in the words a person would use for it.</summary>
    private static string Ending(FocusSessionOutcome outcome) => outcome switch
    {
        FocusSessionOutcome.RanToTime  => "ran to time",
        FocusSessionOutcome.EndedEarly => "ended early",
        _                              => "found finished at a later start",
    };
}
