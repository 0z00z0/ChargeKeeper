using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Low-level, append-only CSV file store shared by the two history services
/// (<see cref="BatteryHistoryService"/> and <see cref="BatteryCapacityHistoryService"/>). It owns
/// ONLY the file plumbing both previously cloned verbatim — the <c>%AppData%\ChargeKeeper</c> path
/// build (via <see cref="AppPaths"/>), a create-the-directory-once guard, a single-line append, a
/// read-all-lines, and a read-last-line — and knows nothing about either service's row FORMAT,
/// pruning, windowing, or in-memory caches. Those stay in the services themselves, so this type is
/// pure persistence with zero domain logic.
/// <para>
/// Deliberately does NO locking of its own. Each history service keeps its own single
/// <see cref="System.Threading.Lock"/> and calls this store only from inside that lock — the same
/// lock that also guards the service's in-memory state — so the file op and the cache update stay
/// one atomic critical section, exactly as the pre-extraction code was. Adding a second lock in here
/// would either be redundant or (worse) split what used to be one critical section into two, so it
/// is intentionally omitted. Not safe to call concurrently on its own.
/// </para>
/// <para>
/// Each service constructs its OWN instance with its OWN default filename, so the two histories
/// remain entirely independent files.
/// </para>
/// </summary>
internal sealed class CsvSampleStore
{
    private string _path;
    private bool _dirEnsured;
    private readonly string? _header;

    /// <param name="fileName">
    /// File name inside <c>%AppData%\ChargeKeeper</c> (e.g. <c>"battery-level-history.csv"</c>). The
    /// full default path is resolved through <see cref="AppPaths"/>; <see cref="UseTestPath"/> can
    /// repoint it.
    /// </param>
    /// <param name="header">
    /// Optional descriptive header block written verbatim the first time the file is CREATED — a
    /// leading <c>#</c> comment line describing the file/units plus a column-name row, the two joined
    /// by a single <c>\n</c>. Null (the default) writes no header, matching the store's original
    /// behaviour. Both header lines fail every service's <c>TryParse</c>, so readers skip them for
    /// free; a whole-file rewrite that must preserve the header (e.g.
    /// <see cref="BatteryHistoryService"/>'s prune) reads it back from <see cref="Header"/>.
    /// </param>
    internal CsvSampleStore(string fileName, string? header = null)
    {
        _path = AppPaths.DataFile(fileName);
        _header = header;
    }

    /// <summary>Path to the backing CSV file — surfaced by each service's <c>FilePath</c>.</summary>
    internal string FilePath => _path;

    /// <summary>
    /// The descriptive header block (<c>#</c> comment + column row joined by <c>\n</c>) this store
    /// prepends on file creation, or <c>null</c> if none was configured. Exposed so a service that
    /// rewrites the whole file (prune) can re-emit it at the top.
    /// </summary>
    internal string? Header => _header;

    /// <summary>
    /// TEST-ONLY seam: repoints the store at an isolated file and forgets the dir-ensured flag, so the
    /// next <see cref="AppendLine"/> re-creates the (temp) directory. Mirrors the per-service
    /// <c>UseTestPath</c> seams that call it; those also reset their own in-memory caches. Must be
    /// called under the owning service's lock, like every other member.
    /// </summary>
    internal void UseTestPath(string path)
    {
        _path = path;
        _dirEnsured = false;
    }

    /// <summary>
    /// Ensures the containing directory exists (once per path) then appends a single line plus a
    /// trailing newline. Uses <see cref="SafeFileAppend"/> (FileShare.ReadWrite + bounded retry) so a
    /// concurrent ChargeKeeper process appending to the same history file can't cause the
    /// sharing-violation loss that #34 diagnosed for app.log — the two history writers used the same
    /// unsafe <c>File.AppendAllText</c> and only dodged it by timing. Still does not swallow I/O
    /// errors: a persistent failure rethrows so the caller owns the "logging must never crash the app"
    /// policy and its own <see cref="AppLog"/> line, exactly as before.
    /// <para>
    /// When a <see cref="Header"/> was configured and the file does not yet exist, the header block is
    /// written ahead of the first row (in the same append, so header + first row land atomically).
    /// </para>
    /// </summary>
    internal void AppendLine(string line)
    {
        if (!_dirEnsured)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _dirEnsured = true;
        }
        // Emit the descriptive header the first time we CREATE the file (single append with the first
        // row so a reader never sees a header-only file). Header lines fail TryParse, so they're
        // skipped on read.
        if (_header is not null && !File.Exists(_path))
            SafeFileAppend.Append(_path, _header + "\n" + line + "\n");
        else
            SafeFileAppend.Append(_path, line + "\n");
    }

    /// <summary>
    /// Enumerates every raw line in the file, oldest → newest (rows are appended in order), or an
    /// empty sequence if the file doesn't exist yet. Lazily streamed via <see cref="File.ReadLines"/>.
    /// Does not swallow I/O errors — the caller wraps this in its own try/catch as before.
    /// </summary>
    internal IEnumerable<string> ReadAllLines() =>
        File.Exists(_path) ? File.ReadLines(_path) : [];

    /// <summary>
    /// The last raw line in the file, or <c>null</c> if the file is missing or empty. Note this is the
    /// last LINE, not the last parseable row — a caller that needs the last valid record still parses
    /// as it goes (see <see cref="BatteryHistoryService"/>'s last-sample scan).
    /// </summary>
    internal string? ReadLastLine() =>
        File.Exists(_path) ? File.ReadLines(_path).LastOrDefault() : null;
}
