using System.Diagnostics;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>One program a person can choose: where it is, what to call it, and whether it is open
/// right now.</summary>
/// <param name="Path">The executable's full path. This is the only thing stored, and the only thing
/// a firewall rule can be keyed on.</param>
/// <param name="Name">What the row shows. Never stored anywhere.</param>
internal sealed record ProgramChoice(string Path, string Name, bool Running)
{
    /// <summary>What the row shows to mark a program that is open, or nothing. A string rather than
    /// a visibility so the row binds straight to it and this file stays clear of the markup
    /// types.</summary>
    public string OpenMark => Running ? "open" : "";
}

/// <summary>
/// The list of programs the picker offers: what is open now, and what the Start menu holds.
/// </summary>
/// <remarks>
/// <para>Measured on one machine: 12 windowed processes in 217 ms, 159 Start menu shortcuts found in
/// 56 ms and resolved in 395 ms, which is about 670 ms in total. Only two of the ten running
/// programs were also in the Start menu, so neither source stands in for the other.</para>
/// <para><b>A packaged application is left out.</b> The firewall keys such a rule on an application
/// container security identifier paired with the owning account, not on a path, and
/// <see cref="FirewallAllowRule"/> carries a path alone — so a packaged application offered here
/// would be chosen and then blocked anyway. Windows Store executables and the execution aliases
/// beside them both sit under a folder named <c>WindowsApps</c>, which is what
/// <see cref="IsPackaged"/> reads.</para>
/// <para>Reading the version resource for a friendlier name was measured at 7.7 s over 103 files —
/// an order of magnitude more than the whole rest of the list — so the shortcut's own name is used,
/// which is what the Start menu shows in any case.</para>
/// </remarks>
internal static class ProgramCatalogue
{
    /// <summary>The folder name every packaged application's executable sits under, whether it is
    /// the installed package or the execution alias that stands in for it.</summary>
    private const string PackagedFolder = "WindowsApps";

    /// <summary>Whether a path belongs to a packaged application, which no rule this application
    /// writes can reach.</summary>
    public static bool IsPackaged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        foreach (string segment in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            if (segment.Equals(PackagedFolder, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>Whether a found file is worth offering at all: a program the firewall can name, on
    /// disk, and not packaged.</summary>
    public static bool IsOfferable(string? path)
    {
        string stored = FocusAllowedPrograms.Normalise(path);
        return FocusAllowedPrograms.IsProgram(stored) && !IsPackaged(stored);
    }

    /// <summary>The two sources as one list: what is open now first, then the rest, each in
    /// alphabetical order. A program in both keeps the Start menu's friendlier name and is still
    /// marked as running.</summary>
    public static IReadOnlyList<ProgramChoice> Merge(
        IEnumerable<ProgramChoice>? running, IEnumerable<ProgramChoice>? installed)
    {
        var byPath = new Dictionary<string, ProgramChoice>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in running ?? [])
        {
            if (!IsOfferable(entry.Path)) continue;
            string path = FocusAllowedPrograms.Normalise(entry.Path);
            byPath[path] = entry with { Path = path, Running = true };
        }

        foreach (var entry in installed ?? [])
        {
            if (!IsOfferable(entry.Path)) continue;
            string path = FocusAllowedPrograms.Normalise(entry.Path);

            byPath[path] = byPath.TryGetValue(path, out var open)
                ? entry with { Path = path, Running = open.Running }
                : entry with { Path = path, Running = false };
        }

        return [.. byPath.Values
                         .OrderByDescending(e => e.Running)
                         .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What typing narrows the list to: every entry whose name or path carries all of the
    /// words typed, in any order. An empty query matches everything.</summary>
    public static IReadOnlyList<ProgramChoice> Match(
        IReadOnlyList<ProgramChoice>? all, string? query)
    {
        if (all is null || all.Count == 0) return [];

        string[] words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries
                                                | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return all;

        return [.. all.Where(e => words.All(w =>
            e.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || e.Path.Contains(w, StringComparison.CurrentCultureIgnoreCase)))];
    }

    // ── Reading the machine ─────────────────────────────────────────────────────────────────────

    /// <summary>Every program with a window a person would recognise. A process whose path cannot be
    /// read is skipped rather than shown without one.</summary>
    public static IReadOnlyList<ProgramChoice> Running()
    {
        var found = new List<ProgramChoice>();

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex) { AppLog.Error("ProgramCatalogue.Running", ex); return found; }

        foreach (var process in processes)
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;

                string? path = process.MainModule?.FileName;
                if (!IsOfferable(path)) continue;

                found.Add(new ProgramChoice(path!, FocusAllowedPrograms.DisplayName(path!), true));
            }
            catch
            {
                // A process at a higher integrity level refuses its module list. Nothing to report:
                // it is one row missing from a list, not a failure.
            }
            finally { process.Dispose(); }
        }

        return found;
    }

    /// <summary>Every program the Start menu holds, named by its shortcut. A shortcut pointing at
    /// anything but a program on disk is dropped.</summary>
    public static IReadOnlyList<ProgramChoice> Installed()
    {
        var found = new List<ProgramChoice>();

        foreach (string root in StartMenuRoots())
            foreach (string shortcut in Shortcuts(root))
            {
                string target = ShellLink.Target(shortcut);
                if (!IsOfferable(target)) continue;
                if (!File.Exists(target)) continue;

                found.Add(new ProgramChoice(target, Path.GetFileNameWithoutExtension(shortcut), false));
            }

        return found;
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Programs,
                                       Environment.SpecialFolder.CommonPrograms })
        {
            string root = "";
            try { root = Environment.GetFolderPath(folder); } catch { }
            if (root.Length > 0 && Directory.Exists(root)) yield return root;
        }
    }

    private static IEnumerable<string> Shortcuts(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible    = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error($"ProgramCatalogue.Shortcuts({root})", ex);
            return [];
        }
    }

    /// <summary>Both sources, read off a single-threaded-apartment thread of its own so the shell
    /// link calls run in their own apartment and nothing blocks the thread drawing the window.
    /// </summary>
    public static Task<IReadOnlyList<ProgramChoice>> BuildAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<ProgramChoice>>();

        var thread = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var merged = Merge(Running(), Installed());
                AppLog.Info($"ProgramCatalogue built {merged.Count} programs in {clock.ElapsedMilliseconds} ms");
                done.SetResult(merged);
            }
            catch (Exception ex)
            {
                AppLog.Error("ProgramCatalogue.BuildAsync", ex);
                done.SetResult([]);
            }
        })
        { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return done.Task;
    }
}
