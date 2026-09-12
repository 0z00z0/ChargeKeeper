using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The one contract behind "One line until it matters": the helper that applies it hides a badge's
/// extra content and never shows it again, so every badge must write its own extra content visible
/// on each refresh. A badge that does not keeps its chips, sliders or preset groups hidden for the
/// life of the window once the setting has collapsed it even once — including after the feature is
/// switched back on and after the setting itself is turned off, which is how two rows silently
/// disappeared from the dashboard.
///
/// Source text rather than behaviour: the writes live in WinUI code-behind that cannot be driven
/// without a UI thread, and the property that broke is structural. The same approach is used
/// elsewhere in this suite to pin things that only exist as source.
/// </summary>
public class DashboardCollapseContractTests
{
    private static string DashboardSource =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "DashboardWindow.xaml.cs")));

    [Fact]
    public void EveryBadgeWritesItsOwnExtraContentVisible()
    {
        string source = DashboardSource;

        var missing = new List<string>();
        foreach (string element in ExtraContentElements(source))
        {
            // The element is made visible somewhere in this window, rather than only ever collapsed.
            bool shown = Regex.IsMatch(
                source,
                $@"\b{Regex.Escape(element)}\.Visibility\s*=\s*[^;]*Visibility\.Visible");
            if (!shown) missing.Add(element);
        }

        Assert.True(missing.Count == 0,
            "Passed to ApplyOneLineCollapse as extra content but never written visible: " +
            string.Join(", ", missing));
    }

    /// <summary>Guards the guard: the extra-content arguments are actually being found, so a parse
    /// that silently matched nothing cannot report success.</summary>
    [Fact]
    public void TheContractCoversEveryBadgeThatCarriesExtraContent()
    {
        var elements = ExtraContentElements(DashboardSource).ToList();

        Assert.Contains("KeepAwakePresetPanel", elements);
        Assert.Contains("LidPresetGroups", elements);
        Assert.Contains("PresetButtonPanel", elements);
    }

    /// <summary>
    /// The element names each call passes beyond the eight fixed parameters. The last fixed one is
    /// the chevron glyph, so everything after it is extra content.
    /// </summary>
    private static IEnumerable<string> ExtraContentElements(string source)
    {
        foreach (Match call in Regex.Matches(source, @"(?<!void )ApplyOneLineCollapse\("))
        {
            string arguments = BalancedArguments(source, call.Index + call.Length - 1);
            int afterGlyph = arguments.IndexOf("ChevronGlyph", StringComparison.Ordinal);
            if (afterGlyph < 0) continue;   // a call that passes no extra content

            int comma = arguments.IndexOf(',', afterGlyph);
            if (comma < 0) continue;

            foreach (string argument in arguments[(comma + 1)..].Split(','))
            {
                string name = argument.Trim();
                if (name.Length > 0) yield return name;
            }
        }
    }

    /// <summary>The text between the opening bracket at <paramref name="open"/> and its match.</summary>
    private static string BalancedArguments(string source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[(open + 1)..i];
        }

        throw new InvalidOperationException("Unbalanced argument list in DashboardWindow.xaml.cs.");
    }
}
