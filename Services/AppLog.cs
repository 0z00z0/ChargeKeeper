namespace ChargeKeeper.Services;

/// <summary>
/// Minimal general-purpose event log at <c>%AppData%\ChargeKeeper\app.log</c>. Started life as
/// App's crash-only logger (stowed exceptions bypass Application.UnhandledException, so nothing
/// else could tell what went wrong); extended into a general Info/Error log so major events
/// (history load, prune, time-scale changes) leave a trail too, not just fatal ones.
/// </summary>
internal static class AppLog
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChargeKeeper", "app.log");

    private static readonly Lock _lock = new();
    private static bool _dirEnsured;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string source, Exception? ex) =>
        Write("ERROR", ex is null ? source : $"{source}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                if (!_dirEnsured)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    _dirEnsured = true;
                }
                File.AppendAllText(_path, $"[{DateTime.Now:u}] {level} {message}\n\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
