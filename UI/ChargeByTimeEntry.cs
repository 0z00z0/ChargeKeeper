using System.Globalization;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>The "Full by" field's list and its reading of an entry. A list entry is shown in the
/// current culture and handed on as 24-hour text, so the Keep Awake parser reads it whatever the
/// culture's own short-time pattern is.</summary>
internal static class ChargeByTimeEntry
{
    public sealed record Choice(TimeOnly Time, string Label);

    /// <summary>Every quarter hour over a day, from the next one after <paramref name="now"/>.</summary>
    public static IReadOnlyList<Choice> QuarterHours(DateTime now)
    {
        int first = (now.Hour * 60 + now.Minute) / 15 + 1;
        var list = new List<Choice>(96);
        for (int i = 0; i < 96; i++)
        {
            var t = new TimeOnly(0, 0).AddMinutes((first + i) % 96 * 15);
            list.Add(new Choice(t, t.ToString("t", CultureInfo.CurrentCulture)));
        }
        return list;
    }

    /// <summary>What reaches the parser: a list label as 24-hour text, anything typed as it stands.</summary>
    public static string? ParserText(string? text, IEnumerable<Choice> choices) =>
        choices.FirstOrDefault(c => string.Equals(c.Label, text?.Trim(), StringComparison.Ordinal)) is { } c
            ? c.Time.ToString("HH\\:mm", CultureInfo.InvariantCulture)
            : text;

    /// <summary>"Today" or "Tomorrow" for a clock time, or null for anything else.</summary>
    public static string? Day(string? parserText, DateTimeOffset now)
    {
        if (!KeepAwakeInputParser.TryParse(parserText, out var request) || request.Kind != KeepAwakeKind.UntilTime ||
            KeepAwakePolicy.ExpiryFor(request, now) is not { } at)
            return null;
        return at.Date == now.Date ? "Today" : "Tomorrow";
    }
}
