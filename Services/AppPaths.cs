using ChargeKeeper.Helpers;
using ZeroZero.Lifecycle;

namespace ChargeKeeper.Services;

/// <summary>Single source of truth for the app's per-user data location, <c>%AppData%\ChargeKeeper\</c>,
/// taken from the shared library's product data path.</summary>
/// <remarks>
/// <para>The shared path CREATES the folder the first time <see cref="DataDir"/> is read, so nothing
/// may touch this class before <c>Program.MigrateLegacyAppDataFolder</c> has run: the move of the
/// pre-rename folder refuses an existing destination. <c>ProgramStartupOrderTests</c> pins that.</para>
/// <para>Settings, the MQTT files and the small state files sit at the top level; logs and crash
/// dumps sit in <see cref="LogsFolderName"/>, and the sampled histories in
/// <see cref="HistoryFolderName"/>. <see cref="DataFolderLayout"/> moves what older versions left at
/// the top level.</para>
/// </remarks>
internal static class AppPaths
{
    internal const string LogsFolderName    = "Logs";
    internal const string HistoryFolderName = "History";

    internal static string DataDir { get; } = ProductDataPath.Root(AppInfo.Name);

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
