namespace ChargeKeeper.Services;

/// <summary>Single source of truth for the app's per-user data location, <c>%AppData%\ChargeKeeper\</c>.
/// Dependency- and side-effect-free — <see cref="AppLog"/> hits it before anything else is initialised —
/// so it only builds a string. Each writer creates the directory itself before its first write.</summary>
internal static class AppPaths
{
    internal static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChargeKeeper");

    /// <summary>Composes a path for a file or subdirectory name; neither creates nor checks for it.</summary>
    internal static string DataFile(string name) => Path.Combine(DataDir, name);
}
