using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Append-only CSV file store under <c>%AppData%\ChargeKeeper</c>, shared by the two history
/// services. Pure file plumbing — row format, pruning and windowing stay in the services.
/// </summary>
/// <remarks>
/// Holds no lock of its own: each service calls it from inside the lock that also guards that
/// service's in-memory state, so the file op and the cache update stay one critical section. Not safe
/// to call concurrently on its own.
/// </remarks>
internal sealed class CsvSampleStore
{
    private string _path;
    private bool _dirEnsured;
    private readonly string? _header;

    /// <param name="fileName">File name inside <c>%AppData%\ChargeKeeper</c>.</param>
    /// <param name="header">Header block written when the file is created; its lines fail every
    /// service's <c>TryParse</c>, so readers skip them for free.</param>
    internal CsvSampleStore(string fileName, string? header = null)
    {
        _path = AppPaths.DataFile(fileName);
        _header = header;
    }

    internal string FilePath => _path;

    /// <summary>Exposed so a service that rewrites the whole file can re-emit the header.</summary>
    internal string? Header => _header;

    /// <summary>Test-only seam. Called under the owning service's lock, like every other member.</summary>
    internal void UseTestPath(string path)
    {
        _path = path;
        _dirEnsured = false;
    }

    /// <summary>
    /// Ensures the containing directory exists, then appends one line. Goes through
    /// <see cref="SafeFileAppend"/> (FileShare.ReadWrite + bounded retry) because a second
    /// ChargeKeeper process can hold the same file open for write. A persistent I/O failure rethrows:
    /// the caller owns the "logging must never crash the app" policy.
    /// </summary>
    internal void AppendLine(string line)
    {
        if (!_dirEnsured)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _dirEnsured = true;
        }
        // Header and first row in one append, so a reader never sees a header-only file.
        if (_header is not null && !File.Exists(_path))
            SafeFileAppend.Append(_path, _header + "\n" + line + "\n");
        else
            SafeFileAppend.Append(_path, line + "\n");
    }

    /// <summary>Streams every raw line, oldest first, or nothing when the file does not exist.</summary>
    internal IEnumerable<string> ReadAllLines() =>
        File.Exists(_path) ? File.ReadLines(_path) : [];

    /// <summary>The last raw LINE — not the last parseable row. Null when the file is missing.</summary>
    internal string? ReadLastLine() =>
        File.Exists(_path) ? File.ReadLines(_path).LastOrDefault() : null;
}
