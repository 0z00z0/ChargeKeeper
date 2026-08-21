using System.Diagnostics;

namespace ChargeKeeper.Helpers;

/// <summary>Opens Windows Explorer at a given file, selecting it where possible.</summary>
internal static class ExplorerLauncher
{
    /// <summary>Builds the <c>explorer.exe</c> arguments that reveal <paramref name="filePath"/>:
    /// <c>/select,"…"</c> when the file exists, or the quoted containing folder when it does not, so
    /// the user still lands in the right directory before the first write.</summary>
    internal static string SelectFileArguments(string filePath, bool fileExists)
    {
        if (fileExists)
            return $"/select,\"{filePath}\"";

        var dir = Path.GetDirectoryName(filePath);
        return $"\"{(string.IsNullOrEmpty(dir) ? filePath : dir)}\"";
    }

    /// <summary>Reveals <paramref name="filePath"/> in Explorer.</summary>
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
