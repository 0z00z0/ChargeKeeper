using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;

namespace ChargeKeeper.Services;

/// <summary>
/// The Windows System event log, read newest first. Reading it needs no administrator rights —
/// measured against the two providers this application asks for — which is why the readings built
/// on it work whether or not the application happens to be elevated.
/// </summary>
internal static class SystemEventLog
{
    /// <summary>
    /// The newest entries matching <paramref name="xpath"/>, at most <paramref name="take"/> of them.
    /// Empty where the log cannot be read at all, which must never be shown as "nothing happened":
    /// every caller reports an unreadable log as no reading rather than as an empty one.
    /// </summary>
    internal static IReadOnlyList<EventRecord> Newest(string xpath, int take)
    {
        var found = new List<EventRecord>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (int i = 0; i < take; i++)
            {
                if (reader.ReadEvent() is not { } record) break;
                found.Add(record);
            }
        }
        catch (Exception ex)
        {
            foreach (var record in found) record.Dispose();
            found.Clear();
            AppLog.Error($"{nameof(SystemEventLog)}.{nameof(Newest)}", ex);
        }
        return found;
    }

    /// <summary>
    /// The last non-empty line of Windows' own rendering of an entry — "Wake Source: Unknown",
    /// "Reason: Input Keyboard". Windows already puts the numeric reason into words, and it does so
    /// in the machine's own language, so the line is taken whole rather than split on a colon or
    /// mapped from the number here: a table written here would be a guess at an undocumented
    /// enumeration, and a split would assume the punctuation of one language.
    /// </summary>
    internal static string? LastRenderedLine(EventRecord record)
    {
        string? description;
        try { description = record.FormatDescription(); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(description)) return null;

        return description
            .Split('\n')
            .Select(line => line.Trim().TrimEnd('.'))
            .LastOrDefault(line => line.Length > 0);
    }

    /// <summary>
    /// An entry's own named data fields. Read from the entry's XML rather than by position, because
    /// a provider adds fields between versions and every reading keyed on an index would then move
    /// to the wrong value silently. Empty where the entry carries none or cannot be rendered.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Data(EventRecord record)
    {
        try
        {
            string? xml = record.ToXml();
            if (string.IsNullOrEmpty(xml)) return ReadOnlyDictionary<string, string>.Empty;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in System.Xml.Linq.XDocument.Parse(xml).Descendants()
                                         .Where(e => e.Name.LocalName == "Data"))
            {
                if (element.Attribute("Name")?.Value is { Length: > 0 } name)
                    fields[name] = element.Value;
            }
            return fields;
        }
        catch { return ReadOnlyDictionary<string, string>.Empty; }
    }

    /// <summary>One named field parsed as a whole number, or null where it is absent or not one.</summary>
    internal static long? Number(IReadOnlyDictionary<string, string> data, string name) =>
        data.TryGetValue(name, out string? raw) &&
        long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                      System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
}
