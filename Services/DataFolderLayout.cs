using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Moves what earlier versions wrote at the top of the data folder into its Logs and History
/// subfolders. Runs at every start, before logging initialises, so it returns what it did rather than
/// logging it; once the files have moved there is nothing left for it to find.
/// </summary>
/// <remarks>Each move is a rename inside one folder, so a file is always at its old path or its new
/// one. Nothing is overwritten: a file whose destination already exists, or that another process
/// holds, stays where it is and is reported.</remarks>
internal static class DataFolderLayout
{
    /// <summary>One file found at the top level, and whether it reached its subfolder.</summary>
    internal sealed record Outcome(string Source, string Destination, Exception? Failure)
    {
        internal bool Moved => Failure is null;
    }

    /// <summary>The power trail an earlier version wrote beside app.log. Nothing writes it any more;
    /// a copy left at the top level still moves with the other logs.</summary>
    internal const string LegacyPowerLogFileName = "power.log";

    /// <summary>The top-level files that move, and the subfolder each belongs in.</summary>
    private static readonly (string FileName, string Folder)[] Rules =
    [
        (AppLog.FileName,                        AppPaths.LogsFolderName),
        (LegacyPowerLogFileName,                 AppPaths.LogsFolderName),
        (UnattendedUpdate.InstallerLogFileName,  AppPaths.LogsFolderName),
        (BatteryHistoryService.FileName,         AppPaths.HistoryFolderName),
        (BatteryCapacityHistoryService.FileName, AppPaths.HistoryFolderName),
        (PerformanceHistoryService.FileName,     AppPaths.HistoryFolderName),
    ];

    /// <summary>Moves every recognised file, the log archives and the crash-dump folder under
    /// <paramref name="dataDir"/>. Never throws.</summary>
    internal static IReadOnlyList<Outcome> MoveIntoSubfolders(string dataDir)
    {
        var outcomes = new List<Outcome>();
        try
        {
            if (!Directory.Exists(dataDir)) return outcomes;

            foreach (string source in Directory.GetFiles(dataDir))
            {
                string name = Path.GetFileName(source);
                if (FolderFor(name) is { } folder)
                    outcomes.Add(MoveFile(source, Path.Combine(dataDir, folder, name)));
            }

            string dumps = Path.Combine(dataDir, CrashDumps.DumpFolderName);
            if (Directory.Exists(dumps))
                MoveTree(dumps, Path.Combine(dataDir, AppPaths.LogsFolderName, CrashDumps.DumpFolderName), outcomes);
        }
        catch (Exception ex)
        {
            outcomes.Add(new Outcome(dataDir, dataDir, ex));
        }
        return outcomes;
    }

    /// <summary>The subfolder a top-level file belongs in, or null for one that stays.</summary>
    internal static string? FolderFor(string name)
    {
        foreach (var (fileName, folder) in Rules)
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) || IsLogArchive(name, fileName))
                return folder;
        return null;
    }

    // nlog.config's archiveSuffixFormat puts _date_sequence between a log's name and its extension.
    private static bool IsLogArchive(string name, string logFileName)
    {
        string extension = Path.GetExtension(logFileName);
        if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase)) return false;

        string stem = Path.GetFileNameWithoutExtension(logFileName) + "_";
        return name.Length > stem.Length + extension.Length
            && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    private static Outcome MoveFile(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // An existing destination was written by a newer run; replacing it would lose that file.
            File.Move(source, destination, overwrite: false);
            return new Outcome(source, destination, null);
        }
        catch (Exception ex)
        {
            return new Outcome(source, destination, ex);
        }
    }

    // File by file rather than one folder rename, so a destination folder that already exists merges
    // instead of blocking every dump behind it.
    private static void MoveTree(string source, string destination, List<Outcome> outcomes)
    {
        foreach (string file in Directory.GetFiles(source))
            outcomes.Add(MoveFile(file, Path.Combine(destination, Path.GetFileName(file))));
        foreach (string directory in Directory.GetDirectories(source))
            MoveTree(directory, Path.Combine(destination, Path.GetFileName(directory)), outcomes);

        // Only an emptied folder goes; a file left behind keeps its folder.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Logs what <see cref="MoveIntoSubfolders"/> returned. Moves happen once and are always
    /// logged; a file left in place is logged only when <paramref name="includeLeftInPlace"/>, because
    /// it is found again at every start.</summary>
    internal static void Report(IReadOnlyList<Outcome> outcomes, bool includeLeftInPlace)
    {
        var moved = outcomes.Where(o => o.Moved).ToList();
        if (moved.Count > 0)
            AppLog.Info($"Data folder: moved {moved.Count} file(s) into the {AppPaths.LogsFolderName} and " +
                        $"{AppPaths.HistoryFolderName} subfolders: " +
                        string.Join(", ", moved.Select(o => Path.GetRelativePath(AppPaths.DataDir, o.Destination))) + ".");

        if (!includeLeftInPlace) return;
        foreach (var o in outcomes.Where(o => !o.Moved))
            AppLog.Info($"Data folder: {o.Source} stays where it is rather than moving to {o.Destination} " +
                        $"({o.Failure!.GetType().Name}: {o.Failure.Message}).");
    }
}
