namespace ChargeKeeper.Services;

/// <summary>
/// Which of the application's own state changes runs a script, and which scripts a change runs.
/// Pure — no clock, no settings, no I/O — so what fires is decided in one place and can be shown
/// without closing a lid or unplugging a charger.
/// </summary>
internal static class ScriptTriggerPolicy
{
    /// <summary>
    /// The trigger one lid notification carries, or null where it carries none. Only a real movement
    /// counts: Windows repeats itself and sends the current state again whenever listening starts, so
    /// a repeat and the value delivered at registration would each run a script for something that
    /// did not happen.
    /// </summary>
    public static ScriptTrigger? ForLid(LidEventKind kind) => kind switch
    {
        LidEventKind.Closed => ScriptTrigger.LidClosed,
        LidEventKind.Opened => ScriptTrigger.LidOpened,
        _                   => null,
    };

    /// <summary>
    /// The trigger a power-source reading carries. <paramref name="wentOnToMains"/> is the edge the
    /// battery report already worked out — true for a charger connected, false for one disconnected,
    /// null where the reading changed nothing. A reading that is not an edge arrives every few
    /// seconds, so treating one as a trigger would run a script continuously.
    /// </summary>
    public static ScriptTrigger? ForPowerSource(bool? wentOnToMains) => wentOnToMains switch
    {
        true  => ScriptTrigger.MainsConnected,
        false => ScriptTrigger.MainsDisconnected,
        null  => null,
    };

    /// <summary>
    /// The scripts a trigger runs, in the order they are held. A script with nothing in it is left
    /// out: running PowerShell on an empty body succeeds, which would report a script as working
    /// when it does nothing at all.
    /// </summary>
    public static IReadOnlyList<ScriptDefinition> Matching(
        IEnumerable<ScriptDefinition> scripts, ScriptTrigger trigger) =>
        scripts.Where(s => s.Trigger == trigger && !s.IsEmpty).ToList();
}
