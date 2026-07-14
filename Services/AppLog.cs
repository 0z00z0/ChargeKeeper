using System.Globalization;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Minimal general-purpose event log at <c>%AppData%\ChargeKeeper\app.log</c>. Started life as
/// App's crash-only logger (stowed exceptions bypass Application.UnhandledException, so nothing
/// else could tell what went wrong); extended into a general Info/Error log so major events
/// (history load, prune, time-scale changes) leave a trail too, not just fatal ones.
/// </summary>
/// <remarks>
/// Fix for #34 ("AppLog silently drops lines"): the original implementation wrote via
/// <c>File.AppendAllText</c>, which opens the file with the default share mode
/// (<see cref="FileShare.Read"/> — deny-write). When a sibling ChargeKeeper process (the watchdog
/// probe / self-heal / an OnProcessExit handler racing the startup handoff) held <c>app.log</c> open
/// for write at the same moment, the append threw <see cref="IOException"/> and the bare
/// <c>catch { }</c> swallowed it forever — the line, and every subsequent collision, vanished with
/// no trace. <c>history.csv</c> uses the same API but happened to dodge this because of timing, not
/// because it's actually safe.
/// <para>
/// The fix: open the file explicitly with <see cref="FileMode.Append"/> +
/// <see cref="FileShare.ReadWrite"/>. Windows makes <see cref="FileMode.Append"/> writes atomic at
/// EOF (the handle is opened with FILE_APPEND_DATA rather than GENERIC_WRITE, so each write always
/// lands at the current end of file regardless of what another handle does), so sharing the file for
/// write across concurrent ChargeKeeper processes is safe — no reader ever sees a torn line, and no
/// writer ever clobbers another's line. A short bounded retry absorbs the remaining sliver of
/// transient contention (e.g. an antivirus scan or a rename-in-flight), and if every attempt still
/// fails the line is never just dropped: it's counted (<see cref="DroppedWriteCount"/>) and spilled
/// to a best-effort per-process fallback file so a genuine failure is observable instead of a black
/// hole.
/// </para>
/// </remarks>
internal static class AppLog
{
    private static readonly string _path = AppPaths.DataFile("app.log");

    private static readonly Lock _lock = new();
    private static bool _dirEnsured;

    /// <summary>
    /// Number of lines that could not be written to their target file after exhausting retries
    /// (each such line was spilled to the per-process fallback file instead, best-effort). Exposed
    /// for diagnostics/tests; not persisted across process restarts.
    /// </summary>
    internal static int DroppedWriteCount => _droppedWriteCount;
    private static int _droppedWriteCount;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string source, Exception? ex) =>
        Write("ERROR", ex is null ? source : $"{source}\n{ex}");

    private static void Write(string level, string message)
    {
        EnsureDirectory();
        WriteLine(_path, level, message);
    }

    private static void EnsureDirectory()
    {
        if (_dirEnsured) return;
        lock (_lock)
        {
            if (_dirEnsured) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                _dirEnsured = true;   // latch only on success — a transient failure retries next call
            }
            catch { /* best-effort; SafeFileAppend below fails fast and spills to the fallback file */ }
        }
    }

    /// <summary>
    /// Core write path, factored out of <see cref="Write"/> so tests can drive it against an
    /// arbitrary temp path without touching the process-wide <c>%AppData%\ChargeKeeper\app.log</c>
    /// file or its one-time directory-creation state. Internal + <c>InternalsVisibleTo</c> covers
    /// the Tests project (see ChargeKeeper.csproj). Never throws.
    /// </summary>
    internal static void WriteLine(string path, string level, string message)
    {
        // InvariantCulture: the timestamp is machine-facing log data. The old `DateTime.Now:u` format
        // was always invariant; a custom format string renders under the thread culture by default,
        // which would stamp a non-Gregorian year or native digits on some locales (#34 review).
        var line = $"[{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}] {level} {message}\n\n";

        // Serialise this process's own writers under _lock: .NET's FileMode.Append is NOT atomic
        // across concurrent handles (it seeks to EOF then writes), so two threads appending at once
        // would race and lose a line. FileShare.ReadWrite inside SafeFileAppend still lets a SIBLING
        // ChargeKeeper process append without a sharing-violation throw (the #34 root cause), and the
        // bounded retry rides out that cross-process contention.
        lock (_lock)
        {
            if (SafeFileAppend.TryAppend(path, line))
                return;

            // Every attempt failed: don't let the line vanish silently like the old catch{} did —
            // count it and spill to a best-effort per-process fallback file (share-safe too, not the
            // old deny-write File.AppendAllText) so a genuine failure is observable, not a black hole.
            _droppedWriteCount++;
            System.Diagnostics.Debug.WriteLine($"AppLog: failed to write to '{path}': {line}");
            SafeFileAppend.TryAppend(path + $".fallback-{Environment.ProcessId}.log", line);
        }
    }
}
