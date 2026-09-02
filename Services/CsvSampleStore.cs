using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>What one line of a sample file is, as its owning service reads it: not a row at all
/// (header, blank, corrupt), a row still inside retention, or a row past it.</summary>
internal enum CsvRowVerdict { NotARow, Keep, Expired }

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

    /// <summary>Appends one row. See <see cref="AppendLines"/>, which does the work.</summary>
    internal void AppendLine(string line) => AppendLines([line]);

    /// <summary>
    /// Ensures the containing directory exists, then appends every row in ONE file open. Goes
    /// through <see cref="SafeFileAppend"/> (FileShare.ReadWrite + bounded retry) because a second
    /// ChargeKeeper process can hold the same file open for write. A persistent I/O failure
    /// rethrows: the caller owns the "logging must never crash the app" policy.
    /// </summary>
    /// <remarks>Batched because a sampler running at 10 Hz would otherwise open, write and close the
    /// file ten times a second. An empty list writes nothing at all, so a flush with nothing to say
    /// does not create the file.</remarks>
    internal void AppendLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return;

        if (!_dirEnsured)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _dirEnsured = true;
        }

        var block = string.Join("\n", lines) + "\n";
        // Header and first rows in one append, so a reader never sees a header-only file.
        if (_header is not null && !File.Exists(_path))
            SafeFileAppend.Append(_path, _header + "\n" + block);
        else
            SafeFileAppend.Append(_path, block);
    }

    /// <summary>
    /// Rewrites the file keeping only the rows <paramref name="classify"/> votes to keep, then, when
    /// <paramref name="maxRows"/> is given, drops the oldest of those until no more than that many
    /// remain. Returns how many rows were dropped for being past retention; lines that are not rows
    /// at all (the header, blanks, corruption) are dropped silently and are not counted, so a file
    /// holding nothing but those is left alone rather than rewritten on every pass.
    /// </summary>
    /// <remarks>Temp file plus an atomic move, and the header is re-emitted because header lines are
    /// never rows. Called under the owning service's lock, like every other member.</remarks>
    internal int Prune(Func<string, CsvRowVerdict> classify, int? maxRows = null)
    {
        ArgumentNullException.ThrowIfNull(classify);

        var kept = new List<string>();
        int dropped = 0;
        foreach (var line in ReadAllLines())   // empty when the file doesn't exist yet
        {
            switch (classify(line))
            {
                case CsvRowVerdict.Keep:    kept.Add(line); break;
                case CsvRowVerdict.Expired: dropped++;      break;
                default:                    break;          // not a row — dropped, not counted
            }
        }

        // Oldest first in the file, so the surplus comes off the front.
        if (maxRows is { } cap && kept.Count > cap)
        {
            int surplus = kept.Count - cap;
            kept.RemoveRange(0, surplus);
            dropped += surplus;
        }

        if (dropped == 0) return 0;

        var tmp = _path + ".tmp";
        var output = new List<string>();
        if (_header is { } h) output.AddRange(h.Split('\n'));
        output.AddRange(kept);
        File.WriteAllLines(tmp, output);
        File.Move(tmp, _path, overwrite: true);
        return dropped;
    }

    /// <summary>Streams every raw line, oldest first, or nothing when the file does not exist.</summary>
    internal IEnumerable<string> ReadAllLines() =>
        File.Exists(_path) ? File.ReadLines(_path) : [];

    /// <summary>The last raw LINE — not the last parseable row. Null when the file is missing.</summary>
    internal string? ReadLastLine() =>
        File.Exists(_path) ? File.ReadLines(_path).LastOrDefault() : null;
}
