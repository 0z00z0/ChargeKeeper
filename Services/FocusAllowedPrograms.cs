namespace ChargeKeeper.Services;

/// <summary>What happened to a request to change the allow-list. <see cref="Added"/> and
/// <see cref="Removed"/> are the only outcomes that changed anything.</summary>
internal enum FocusAllowVerdict
{
    Added,
    Removed,

    /// <summary>A session is running, so the list may not move. Changing it mid-session would either
    /// leave a program with a rule the session never wrote, or take away one it did.</summary>
    SessionRunning,

    /// <summary>Not a program this can name to the firewall.</summary>
    NotAProgram,

    /// <summary>Already on the list.</summary>
    AlreadyAllowed,

    /// <summary>Not on the list, so there was nothing to take off it.</summary>
    NotAllowed,
}

/// <summary>
/// The allow-list on the network lever: the programs that keep the network while a session blocks
/// everything else.
/// </summary>
/// <remarks>
/// A program is named by its executable's full path, which is what a firewall rule keys on. A
/// display name or a window title is neither unique nor stable and cannot be written into a rule at
/// all.
/// </remarks>
internal static class FocusAllowedPrograms
{
    /// <summary>Enough that a session can keep a working set reachable, small enough that the rules
    /// written at the start of one stay countable.</summary>
    public const int Maximum = 20;

    /// <summary>The stored form of a chosen path: trimmed, with the separators the platform uses, so
    /// two spellings of one file do not both reach the list.</summary>
    /// <remarks>A path that is not already rooted comes back empty rather than resolved. Resolving
    /// one against the working directory would turn a bare file name into a rooted path to a file
    /// that is very probably not the program meant, and the rule written from it would name that
    /// file.</remarks>
    public static string Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        string trimmed = path.Trim().Trim('"');
        if (!Path.IsPathRooted(trimmed)) return "";

        try { return Path.GetFullPath(trimmed); }
        catch { return ""; }
    }

    /// <summary>Whether a path can be written into a firewall rule at all.</summary>
    public static bool IsProgram(string path) =>
        path.Length > 0
        && Path.IsPathRooted(path)
        && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether two stored paths name the same file. Windows paths compare without
    /// case.</summary>
    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the row shows: the file's own name, with the path beside it.</summary>
    public static string DisplayName(string path) =>
        Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name ? name : path;

    /// <summary>Adds one program, or says why it was not added. <paramref name="list"/> is changed
    /// only on <see cref="FocusAllowVerdict.Added"/>.</summary>
    public static FocusAllowVerdict Add(IList<string> list, string? path, bool sessionRunning)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (sessionRunning) return FocusAllowVerdict.SessionRunning;

        string stored = Normalise(path);
        if (!IsProgram(stored)) return FocusAllowVerdict.NotAProgram;
        if (list.Any(p => Same(p, stored))) return FocusAllowVerdict.AlreadyAllowed;
        if (list.Count >= Maximum) return FocusAllowVerdict.NotAProgram;

        list.Add(stored);
        return FocusAllowVerdict.Added;
    }

    /// <summary>Takes one program off, or says why it was not taken off.</summary>
    public static FocusAllowVerdict Remove(IList<string> list, string? path, bool sessionRunning)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (sessionRunning) return FocusAllowVerdict.SessionRunning;

        string stored = Normalise(path);
        int at = -1;
        for (int i = 0; i < list.Count; i++)
            if (Same(list[i], stored)) { at = i; break; }

        if (at < 0) return FocusAllowVerdict.NotAllowed;

        list.RemoveAt(at);
        return FocusAllowVerdict.Removed;
    }

    /// <summary>The list as the lever writes its rules from: every entry that still names a program,
    /// in the order it was added, with duplicates dropped.</summary>
    public static IReadOnlyList<string> Usable(IEnumerable<string>? stored)
    {
        if (stored is null) return [];

        var usable = new List<string>();
        foreach (string entry in stored)
        {
            string path = Normalise(entry);
            if (IsProgram(path) && !usable.Any(p => Same(p, path))) usable.Add(path);
        }
        return usable;
    }
}
