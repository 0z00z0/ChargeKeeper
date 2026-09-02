namespace ChargeKeeper.Helpers;

/// <summary>
/// Where an installation lives and how to recognise one. The product was renamed after 1.1.x while
/// the installer's AppId was not, so a machine upgraded across the rename keeps the retired folder
/// name until the installer moves it. The three literals are stated once here because the installer
/// script states the same three, and a test pins the two sets together.
/// </summary>
internal static class InstallLocations
{
    internal const string ExeName = "ChargeKeeper.exe";

    /// <summary>The folder a fresh install lands in, under the per-user programs directory.</summary>
    internal const string ProductFolderName = "ChargeKeeper";

    /// <summary>The retired product's folder. Still in use on any machine installed before the
    /// rename that the installer has not yet moved.</summary>
    internal const string LegacyFolderName = "Lenovo Power Tray";

    internal static bool IsProductInstallDir(string? dir) => LeafIs(dir, ProductFolderName);

    internal static bool IsLegacyInstallDir(string? dir) => LeafIs(dir, LegacyFolderName);

    /// <summary>True for the executable sitting in either accepted install folder. A build output,
    /// or a copy anywhere else, is not one.</summary>
    internal static bool IsInstalledExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        if (!string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase)) return false;

        string? dir = Path.GetDirectoryName(exe);
        return IsProductInstallDir(dir) || IsLegacyInstallDir(dir);
    }

    /// <summary>The retired folder beside <paramref name="productDir"/>. Composed from a directory
    /// already known to be the current install folder, never searched for, so it can only ever name
    /// that folder's own sibling. Null for anything else.</summary>
    internal static string? LegacySiblingOf(string? productDir)
    {
        if (!IsProductInstallDir(productDir)) return null;

        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(productDir!));
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, LegacyFolderName);
    }

    /// <summary>A trailing separator would otherwise make the final component read as empty.</summary>
    private static bool LeafIs(string? dir, string name) =>
        !string.IsNullOrWhiteSpace(dir)
        && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir!)), name,
                         StringComparison.OrdinalIgnoreCase);
}
