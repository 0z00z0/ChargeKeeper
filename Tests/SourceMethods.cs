using System.Text.RegularExpressions;

namespace ChargeKeeper.Tests;

/// <summary>
/// Pulls a single method body out of a shipped source file. Several guards here are about WHERE a
/// check sits rather than what a pure function returns — the tray methods are on the WinUI
/// application object and cannot be constructed in a test — and each needs the same extraction.
/// </summary>
internal static class SourceMethods
{
    /// <summary>
    /// The body of the first method named <paramref name="methodName"/>, braces excluded. Throws
    /// when the method is absent, so a rename fails the guard rather than passing it vacuously.
    /// </summary>
    public static string Body(string source, string methodName)
    {
        var signature = new Regex($@"\b{Regex.Escape(methodName)}\s*\([^)]*\)\s*(\r?\n\s*)?\{{",
                                  RegexOptions.Singleline);
        var match = signature.Match(source);
        if (!match.Success)
            throw new InvalidOperationException(
                $"No method named '{methodName}' was found in the source under test.");

        int open  = source.IndexOf('{', match.Index + methodName.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[(open + 1)..i];
        }

        throw new InvalidOperationException($"The body of '{methodName}' is not brace-balanced.");
    }
}
