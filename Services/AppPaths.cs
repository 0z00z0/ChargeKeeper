namespace ChargeKeeper.Services;

/// <summary>
/// Single source of truth for the app's per-user data location, <c>%AppData%\ChargeKeeper\</c>.
/// Centralises the folder build that was previously hand-rolled at ~8 call sites (each a
/// <c>Path.Combine(GetFolderPath(ApplicationData), "ChargeKeeper", …)</c>), so the folder name and
/// its parent live in exactly one place instead of being retyped — and independently drift-able —
/// everywhere a file under it is opened.
/// <para>
/// Intentionally dependency-free: it touches nothing but <see cref="Environment"/> and
/// <see cref="Path"/>. Some callers (notably <see cref="AppLog"/>) run at the very start of startup,
/// before anything else is initialised, so this must be safe to hit that early with no side effects
/// beyond a string build — in particular it does NOT create the directory (each writer still ensures
/// that itself, lazily, right before its first write).
/// </para>
/// </summary>
internal static class AppPaths
{
    /// <summary>
    /// The app's roaming data directory: <c>%AppData%\ChargeKeeper</c>. Built once. Not created here —
    /// callers create it on demand before writing (see <see cref="CsvSampleStore.AppendLine"/>,
    /// <see cref="AppLog"/>, etc.).
    /// </summary>
    internal static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChargeKeeper");

    /// <summary>
    /// A path inside <see cref="DataDir"/>: <c>%AppData%\ChargeKeeper\{name}</c>. <paramref name="name"/>
    /// is a file name (e.g. <c>"history.csv"</c>) or a subdirectory name (e.g. <c>"dumps"</c>) — this
    /// only composes the path; it neither creates nor checks for it.
    /// </summary>
    internal static string DataFile(string name) => Path.Combine(DataDir, name);
}
