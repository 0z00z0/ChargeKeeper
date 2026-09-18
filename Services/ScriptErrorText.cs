namespace ChargeKeeper.Services;

/// <summary>
/// Reads what Windows PowerShell wrote to its error stream as plain messages. Each error record is a
/// message, then its location ("At …"), then "+" lines with the command and the category, and a blank
/// line before the next record; only the message is kept, its wrapped lines joined. Text that is not
/// shaped like a record — a program writing to the stream directly — is kept whole. Pure.
/// </summary>
internal static class ScriptErrorText
{
    /// <summary>How long one message may be in a log line or a notification.</summary>
    internal const int MessageCap = 300;

    public static IReadOnlyList<string> Messages(string? errorOutput)
    {
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(errorOutput)) return messages;

        var message = new List<string>();
        bool inDetail = false;
        foreach (string raw in errorOutput.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                inDetail = false;
                continue;
            }

            if (inDetail) continue;
            if (line.StartsWith("At ", StringComparison.Ordinal) || line.StartsWith('+'))
            {
                inDetail = true;
                continue;
            }

            message.Add(line);
        }
        Flush();
        return messages;

        void Flush()
        {
            if (message.Count == 0) return;
            string joined = string.Join(' ', message);
            messages.Add(joined.Length <= MessageCap ? joined : joined[..MessageCap] + "…");
            message.Clear();
        }
    }
}
