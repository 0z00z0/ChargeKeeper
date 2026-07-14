using System.Diagnostics;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Opens Windows Explorer at (and, when possible, with pre-selected) a given file — the "Open
/// settings folder" action moved out of the tray menu into the Settings window's Advanced footer
/// (TODO #28). The argument-building half (<see cref="SelectFileArguments"/>) is a pure function so
/// it can be unit-tested without spawning Explorer.
/// </summary>
internal static class ExplorerLauncher
{
    /// <summary>
    /// Builds the <c>explorer.exe</c> argument string that reveals <paramref name="filePath"/>:
    /// <c>/select,"…"</c> to open its folder with the file highlighted when it exists, or just the
    /// quoted containing folder when it does not (so the user still lands in the right directory even
    /// before the file has been written for the first time). Pure — no I/O, no process launch.
    /// </summary>
    internal static string SelectFileArguments(string filePath, bool fileExists)
    {
        if (fileExists)
            return $"/select,\"{filePath}\"";

        var dir = Path.GetDirectoryName(filePath);
        return $"\"{(string.IsNullOrEmpty(dir) ? filePath : dir)}\"";
    }

    /// <summary>
    /// Reveals <paramref name="filePath"/> in Explorer, selecting the file when it exists (see
    /// <see cref="SelectFileArguments"/>). Reads <see cref="File.Exists"/> once and launches Explorer.
    /// </summary>
    internal static void Reveal(string filePath)
    {
        var args = SelectFileArguments(filePath, File.Exists(filePath));
        Process.Start(new ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = args,
            UseShellExecute = true,
        });
    }
}
