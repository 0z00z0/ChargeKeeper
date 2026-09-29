namespace ChargeKeeper.Helpers;

/// <summary>
/// Where an installation lives and how to recognise one. The installer script states the same folder
/// name, so the two must agree.
/// </summary>
internal static class InstallLocations
{
    internal const string ExeName = "ChargeKeeper.exe";

    /// <summary>The folder a fresh install lands in, under the per-user programs directory.</summary>
    internal const string ProductFolderName = "ChargeKeeper";

    internal static bool IsProductInstallDir(string? dir) => LeafIs(dir, ProductFolderName);

    /// <summary>True for the executable sitting in the install folder. A build output, or a copy
    /// anywhere else, is not one.</summary>
    internal static bool IsInstalledExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        if (!string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase)) return false;

        return IsProductInstallDir(Path.GetDirectoryName(exe));
    }

    /// <summary>A trailing separator would otherwise make the final component read as empty.</summary>
    private static bool LeafIs(string? dir, string name) =>
        !string.IsNullOrWhiteSpace(dir)
        && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir!)), name,
                         StringComparison.OrdinalIgnoreCase);
}
