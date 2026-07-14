namespace ChargeKeeper.Helpers;

/// <summary>
/// The single safe file-append primitive behind <see cref="ChargeKeeper.Services.AppLog"/> and the
/// CSV history stores (<see cref="ChargeKeeper.Services.CsvSampleStore"/>). Opens with
/// <see cref="FileMode.Append"/> + <see cref="FileShare.ReadWrite"/>: Windows makes append writes
/// atomic at EOF (the handle uses FILE_APPEND_DATA, so every write lands at the current end of file
/// regardless of what another handle is doing), so concurrent ChargeKeeper processes — the 5-minute
/// watchdog probe, a self-heal relaunch, an OnProcessExit handler racing the startup handoff — can
/// share the file for write without ever clobbering or tearing each other's lines. A short bounded
/// retry absorbs the remaining sliver of transient contention (an AV scan, a rename-in-flight).
///
/// This is the fix for #34 ("AppLog silently drops lines"): the old code used
/// <c>File.AppendAllText</c>, whose default <see cref="FileShare.Read"/> denies concurrent writers,
/// so a sibling process's in-flight append made the call throw a sharing violation — swallowed and
/// lost. <c>history.csv</c> used the same API and only dodged it by timing, so both callers now route
/// through here.
///
/// Only sharing/lock collisions are retried; a non-transient failure (missing directory, access
/// denied, path too long) fails fast — retrying can't help. <see cref="Append"/> rethrows the final
/// failure so a caller that owns its own error policy still sees it; <see cref="TryAppend"/> never
/// throws and reports success as a bool.
/// </summary>
internal static class SafeFileAppend
{
    private const int MaxAttempts = 5;

    /// <summary>Appends <paramref name="content"/>, sharing the file and retrying transient sharing
    /// violations. Throws the final exception if every attempt fails (the caller owns the policy).</summary>
    internal static void Append(string path, string content) => Write(path, content, throwOnFail: true);

    /// <summary>Like <see cref="Append"/> but never throws; returns whether the write ultimately
    /// succeeded.</summary>
    internal static bool TryAppend(string path, string content) => Write(path, content, throwOnFail: false);

    private static bool Write(string path, string content, bool throwOnFail)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write(content);
                return true;
            }
            catch (DirectoryNotFoundException ex) { last = ex; break; } // non-transient — dir is gone
            catch (FileNotFoundException ex)      { last = ex; break; } // non-transient
            catch (IOException ex)
            {
                // Transient sharing/lock collision — a sibling process's append is mid-flight, or AV
                // briefly holds the handle. Worth a short bounded retry.
                last = ex;
                if (attempt < MaxAttempts - 1) Thread.Sleep(15 * (attempt + 1));
            }
            catch (Exception ex) { last = ex; break; } // UnauthorizedAccess / PathTooLong — retry won't help
        }

        if (throwOnFail && last is not null) throw last;
        return false;
    }
}
