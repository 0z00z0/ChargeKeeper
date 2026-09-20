namespace ChargeKeeper.Services;

/// <summary>One outstanding request to keep something on.</summary>
/// <param name="Category">What is being held on — DISPLAY, SYSTEM and the rest, as Windows groups
/// them.</param>
/// <param name="Kind">PROCESS, SERVICE or DRIVER.</param>
/// <param name="Holder">What Windows named as holding it: a program's path, a service name or a
/// driver's description.</param>
/// <param name="Reason">What the holder said it is for, where it said anything.</param>
/// <param name="IsThisApplication">Whether this is one of ChargeKeeper's own holds.</param>
internal readonly record struct PowerRequestEntry(
    string Category, string Kind, string Holder, string? Reason, bool IsThisApplication)
{
    /// <summary>The holder as a person would name it: the file's own name for a program, and the
    /// whole thing for a service or a driver, whose names are already short.</summary>
    internal string ShortHolder
    {
        get
        {
            int cut = Holder.LastIndexOf('\\');
            return cut >= 0 && cut < Holder.Length - 1 ? Holder[(cut + 1)..] : Holder;
        }
    }
}

/// <summary>
/// Reads the list of outstanding power requests out of the text <c>powercfg /requests</c> prints.
/// </summary>
/// <remarks>
/// There is no documented interface that enumerates power requests, and the undocumented power
/// information level for it refused with an invalid-parameter status on the Windows build measured,
/// at every buffer size tried. The console command is what remains, and it needs administrator
/// rights, which this application has.
/// <para>The tokens below were measured out of the command's own resources rather than assumed: the
/// six category headings, the empty-category line, and the three bracketed caller types.</para>
/// </remarks>
internal static class PowerRequestText
{
    /// <summary>What an empty category prints.</summary>
    private const string Empty = "None.";

    /// <summary>
    /// Parses the command's output. <paramref name="thisApplicationExe"/> is the file name of this
    /// application's own executable, passed in rather than read here so the recognition can be
    /// exercised without a live process.
    /// <para>An unrecognised line is kept as the previous entry's reason rather than dropped, and a
    /// text carrying no category heading at all yields nothing — a shape this does not understand
    /// must read as "could not be read", never as "nothing is holding the machine awake".</para>
    /// </summary>
    internal static IReadOnlyList<PowerRequestEntry>? Parse(string? output, string thisApplicationExe)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var entries = new List<PowerRequestEntry>();
        string? category = null;
        bool sawCategory = false;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (IsCategory(line))
            {
                category = line[..^1];
                sawCategory = true;
                continue;
            }

            if (category is null || line == Empty) continue;

            if (Bracketed(line) is { } entry)
            {
                entries.Add(new PowerRequestEntry(category, entry.Kind, entry.Holder, null,
                                                  IsThisApplication(entry.Holder, thisApplicationExe)));
                continue;
            }

            // A continuation line: the holder's own stated reason, which may run to several lines.
            if (entries.Count == 0) continue;
            var last = entries[^1];
            entries[^1] = last with { Reason = last.Reason is { Length: > 0 } had ? $"{had} {line}" : line };
        }

        return sawCategory ? entries : null;
    }

    /// <summary>A heading is a word in capitals followed by a colon and nothing else. Matched by
    /// shape rather than against a list of the six known ones, so a category a later Windows adds is
    /// still shown rather than silently swallowing the entries under it.</summary>
    private static bool IsCategory(string line) =>
        line.Length > 1 && line[^1] == ':' &&
        line[..^1].All(c => char.IsAsciiLetterUpper(c) || c == ' ');

    private static (string Kind, string Holder)? Bracketed(string line)
    {
        if (line.Length == 0 || line[0] != '[') return null;
        int close = line.IndexOf(']');
        if (close <= 1) return null;

        string kind   = line[1..close].Trim();
        string holder = line[(close + 1)..].Trim();
        return kind.Length == 0 ? null : (kind, holder);
    }

    /// <summary>
    /// Whether a holder is this application. Compared on the executable's own file name, because
    /// Windows names a process here by a device path rather than by the drive letter the same file
    /// has everywhere else, and the two cannot be compared as written.
    /// </summary>
    internal static bool IsThisApplication(string holder, string thisApplicationExe)
    {
        if (thisApplicationExe.Length == 0 || holder.Length == 0) return false;
        int cut = holder.LastIndexOf('\\');
        string name = cut >= 0 && cut < holder.Length - 1 ? holder[(cut + 1)..] : holder;
        return name.Equals(thisApplicationExe, StringComparison.OrdinalIgnoreCase);
    }
}
