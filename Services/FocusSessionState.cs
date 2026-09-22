namespace ChargeKeeper.Services;

/// <summary>What a focus session is doing right now, as a reader of the published surface needs it:
/// whether one is running at all, and how far a cancel attempt has got.</summary>
internal enum FocusSessionStage
{
    /// <summary>No session is running.</summary>
    Off,

    /// <summary>A session is running and no cancel attempt is in flight.</summary>
    Active,

    /// <summary>A cancel was asked for and the wait before it can be confirmed is running.</summary>
    Ending,

    /// <summary>The wait has run out; a second cancel request inside this window ends the session.</summary>
    Confirm,
}

/// <summary>The word each stage is published as, and the timing the staged cancel runs on.</summary>
/// <remarks>The words are part of the published contract: a receiver holds the state against the
/// declared list and every automation compares against these literals.</remarks>
internal static class FocusSessionStages
{
    /// <summary>How long a cancel request waits before it can be confirmed. A delay rather than a
    /// repeated prompt: a prompt becomes an autopilot tap, a wait does not.</summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromMinutes(5);

    /// <summary>How long the confirm window stands after the wait runs out. Missing it leaves the
    /// session running and the next attempt starts the wait afresh.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    public static string Label(FocusSessionStage stage) => stage switch
    {
        FocusSessionStage.Active  => "Active",
        FocusSessionStage.Ending  => "Ending",
        FocusSessionStage.Confirm => "Confirm",
        _                         => "Off",
    };

    /// <summary>Every word the entity can publish, in the order the stages are declared.</summary>
    public static IReadOnlyList<string> Words { get; } =
        [.. Enum.GetValues<FocusSessionStage>().Select(Label)];

    /// <summary>The session in one line, for the tray menu and the Settings page. Both say the same
    /// thing, and neither offers anything to act on: nothing local ends a session.</summary>
    public static string Describe(FocusSnapshot session, DateTimeOffset now) =>
        session.IsRunning ? $"Focus session: {Detail(session, now)}" : "No focus session is running.";

    /// <summary>The same line without its subject, for a surface whose heading already names the
    /// session — the dashboard's own row. One composition, so the two can never disagree.</summary>
    public static string Detail(FocusSnapshot session, DateTimeOffset now)
    {
        if (!session.IsRunning) return "not running";

        int minutes = SurfaceReader.MinutesUntil(session.EndsAt, now) ?? 0;
        var levers = new List<string>(4);
        if (session.BlocksNetwork) levers.Add("network blocked");
        if (session.DimsScreen)    levers.Add("screen dimmed");
        if (session.CoversScreen)  levers.Add("screen covered");
        if (session.BlocksInput)   levers.Add("input blocked");

        string stage = session.Stage switch
        {
            FocusSessionStage.Ending  => ", cancel asked for",
            FocusSessionStage.Confirm => ", waiting for the second request",
            _                         => "",
        };

        return $"{minutes} min left — {string.Join(", ", levers)}{stage}";
    }
}

/// <summary>The session as the published surface and the tray report it. Composed under the
/// engine's own lock, so the stage and the levers cannot disagree.</summary>
/// <param name="StartedAt">Null when no session is running. When the session was armed — the
/// reference a full countdown ring is drawn against, and nothing else.</param>
/// <param name="EndsAt">Null when no session is running. The instant the session ends, never a
/// countdown: the system clock keeps time whether or not the machine is awake to watch it.</param>
internal readonly record struct FocusSnapshot(
    FocusSessionStage Stage, DateTimeOffset? StartedAt, DateTimeOffset? EndsAt,
    bool BlocksNetwork, bool DimsScreen, bool CoversScreen, bool BlocksInput)
{
    public static readonly FocusSnapshot None =
        new(FocusSessionStage.Off, null, null, false, false, false, false);

    public bool IsRunning => Stage != FocusSessionStage.Off;
}

/// <summary>Why a session was not armed. <see cref="Armed"/> is the only outcome that changed
/// anything.</summary>
internal enum FocusArmOutcome
{
    Armed,

    /// <summary>A session is already running, so there is nothing to arm.</summary>
    AlreadyRunning,

    /// <summary>No lever at all was chosen. A switch that counts down and does nothing is
    /// indistinguishable from a broken one, so nothing is armed.</summary>
    NoLeverChosen,

    /// <summary>A chosen lever refused — the broker port is Automatic, or the display accepts no
    /// brightness. Nothing is armed, and nothing is half-engaged.</summary>
    LeverRefused,

    /// <summary>Every lever agreed and one then failed to engage. Whatever did engage is lifted
    /// again before this is returned.</summary>
    LeverFailed,
}
