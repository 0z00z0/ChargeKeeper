namespace ChargeKeeper.Helpers;

/// <summary>
/// The single safe file-append primitive behind <see cref="ChargeKeeper.Services.AppLog"/> and the
/// CSV history stores. <see cref="FileMode.Append"/> + <see cref="FileShare.ReadWrite"/> is what
/// lets concurrent ChargeKeeper processes share the file for write: the handle uses
/// FILE_APPEND_DATA, so every write lands at the current end of file whatever another handle is
/// doing, and lines can neither clobber nor tear each other. <c>File.AppendAllText</c> cannot be
/// used — its default <see cref="FileShare.Read"/> denies concurrent writers.
/// <para>Only sharing and lock collisions are retried; a missing directory, access denial or long
/// path fails fast. <see cref="Append"/> rethrows the final failure, <see cref="TryAppend"/> reports
/// it as a bool.</para>
/// </summary>
internal static class SafeFileAppend
{
    private const int MaxAttempts = 5;

    /// <summary>Appends <paramref name="content"/>, retrying transient sharing violations. Throws the
    /// final exception if every attempt fails.</summary>
    internal static void Append(string path, string content) => Write(path, content, throwOnFail: true);

    /// <summary>Like <see cref="Append"/> but never throws.</summary>
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
                // Transient: a sibling's append is mid-flight, or AV briefly holds the handle.
                last = ex;
                if (attempt < MaxAttempts - 1) Thread.Sleep(15 * (attempt + 1));
            }
            catch (Exception ex) { last = ex; break; } // UnauthorizedAccess / PathTooLong — retry won't help
        }

        if (throwOnFail && last is not null) throw last;
        return false;
    }
}
