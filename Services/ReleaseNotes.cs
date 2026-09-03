using System.Reflection;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>One released version and what it changed, as the notes file states it.</summary>
/// <param name="Version">The heading, which is the version exactly as the project declares it.</param>
/// <param name="Lines">The entries beneath it, each already unwrapped to one line.</param>
internal sealed record ReleaseNote(string Version, IReadOnlyList<string> Lines);

/// <summary>
/// What each release changed, read from the notes file the repository carries. The same file is the
/// release workflow's source for the published body, so the report in the application and the notes
/// on the releases page cannot drift: there is one text, not two.
/// </summary>
/// <remarks>The file is embedded in the assembly rather than fetched, because the requirement is
/// that the report is always available and an "always" that depends on a network is not one.</remarks>
internal static class ReleaseNotes
{
    /// <summary>The embedded resource's name. Stated rather than searched for, so a file that
    /// stopped being embedded fails a test instead of silently reporting nothing.</summary>
    internal const string ResourceName = "ChargeKeeper.RELEASE-NOTES.md";

    private static readonly Lazy<IReadOnlyList<ReleaseNote>> _all = new(() => Parse(ReadDocument()));

    /// <summary>Every version in the file, newest first — the order the file itself is written in,
    /// never re-sorted: a version string is not reliably comparable and the file's own order is the
    /// author's.</summary>
    internal static IReadOnlyList<ReleaseNote> All => _all.Value;

    /// <summary>The entry for <paramref name="version"/>, or null where the file has none — which is
    /// the ordinary state for a build made between releases.</summary>
    internal static ReleaseNote? For(string version) =>
        All.FirstOrDefault(n => string.Equals(n.Version, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>The file as it ships. Empty when it is not embedded, so nothing throws on a build
    /// that lost the resource; the report is then empty rather than absent.</summary>
    internal static string ReadDocument()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return string.Empty;

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AppLog.Error("ReleaseNotes.ReadDocument", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Splits the document into one entry per version. A version section opens at a level-two
    /// heading whose whole text is the version; everything above the first one is the file's own
    /// preamble and is dropped. A bullet wrapped over several lines is rejoined, because the file is
    /// wrapped for reading and the report is not.
    /// </summary>
    internal static IReadOnlyList<ReleaseNote> Parse(string document)
    {
        var notes   = new List<ReleaseNote>();
        var lines   = new List<string>();
        string? version = null;

        void Close()
        {
            if (version is { } v) notes.Add(new ReleaseNote(v, lines.ToArray()));
            lines.Clear();
        }

        foreach (string raw in (document ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                version = line[3..].Trim();
                continue;
            }

            if (version is null) continue;                     // still in the preamble

            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                lines.Add(trimmed[2..].Trim());
            else if (lines.Count > 0)
                // A continuation of the entry above: the file wraps at a column, the report does not.
                lines[^1] = $"{lines[^1]} {trimmed}";
        }

        Close();
        return notes;
    }
}
