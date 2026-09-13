namespace ChargeKeeper.Services;

/// <summary>Single source of truth for the app's per-user data location, <c>%AppData%\ChargeKeeper\</c>.
/// Dependency- and side-effect-free — <see cref="AppLog"/> hits it before anything else is initialised —
/// so it only builds a string. Each writer creates the directory itself before its first write.</summary>
/// <remarks>Settings, the MQTT files and the small state files sit at the top level; logs and crash
/// dumps sit in <see cref="LogsFolderName"/>, and the sampled histories in
/// <see cref="HistoryFolderName"/>. <see cref="DataFolderLayout"/> moves what older versions left at
/// the top level.</remarks>
internal static class AppPaths
{
    internal const string LogsFolderName    = "Logs";
    internal const string HistoryFolderName = "History";

    internal static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChargeKeeper");

    // Declared after DataDir: static initialisers run in textual order.
    internal static string LogsDir    { get; } = Path.Combine(DataDir, LogsFolderName);
    internal static string HistoryDir { get; } = Path.Combine(DataDir, HistoryFolderName);

    /// <summary>Composes a path for a file or subdirectory name; neither creates nor checks for it.</summary>
    internal static string DataFile(string name) => Path.Combine(DataDir, name);

    /// <summary>Composes a path inside the Logs subfolder; neither creates nor checks for it.</summary>
    internal static string LogFile(string name) => Path.Combine(LogsDir, name);

    /// <summary>Composes a path inside the History subfolder; neither creates nor checks for it.</summary>
    internal static string HistoryFile(string name) => Path.Combine(HistoryDir, name);
}
