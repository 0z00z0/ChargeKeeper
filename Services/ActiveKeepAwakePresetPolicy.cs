namespace ChargeKeeper.Services;

/// <summary>
/// Which saved preset a running keep-awake session came from. A keep-awake preset starts a timed
/// session rather than setting a lasting value, so "in use" cannot be a value comparison the way
/// <see cref="ActivePresetPolicy"/> is: it means a session is running and its request is this
/// preset. Pure — no service, no clock, no UI types.
/// </summary>
internal static class ActiveKeepAwakePresetPolicy
{
    /// <summary>
    /// Position of the preset the running session was started from, or -1 for none. A request
    /// carries the whole of what a preset is — kind, span and name — so record equality is the
    /// attribution, and every start path passes the preset through unchanged. First match wins, so
    /// two presets carrying identical spans resolve deterministically.
    /// </summary>
    /// <remarks>A session started from the custom box, from a network rule, or from a preset that
    /// has since been edited matches nothing, and every row is left offering activation.</remarks>
    public static int MatchIndex(IReadOnlyList<KeepAwakeRequest>? presets, KeepAwakeSession? session)
    {
        if (presets is null || session is null) return -1;

        for (int i = 0; i < presets.Count; i++)
            if (presets[i] == session.Request) return i;

        return -1;
    }
}
