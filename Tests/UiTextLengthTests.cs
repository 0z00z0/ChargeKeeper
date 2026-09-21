using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Caps the length of every hover text and every description the application draws. The wording
/// drifts back towards an essay whenever a feature gains a caveat, so the cap is enforced rather
/// than reviewed. Reasoning about why a design behaves as it does belongs in the documentation
/// folder, not in a tooltip.
/// </summary>
/// <remarks>
/// The strings are read out of the shipped markup and source, so a new control is covered the
/// moment it is written. A hand-maintained list would go stale on the first page added.
/// </remarks>
public class UiTextLengthTests
{
    /// <summary>A tooltip or info bubble sits in a 280 DIP panel at 12–13 px, so a line holds
    /// roughly fifty characters. Two lines, with a little room.</summary>
    private const int HoverCap = 110;

    /// <summary>A settings card's description column runs wider than a tooltip, so this is three
    /// short lines at the narrowest window width the fit guarantees.</summary>
    private const int BodyCap = 140;

    /// <summary>The one string that cannot be said shorter: the two firewall rule names are a
    /// published interface a person types into Windows Defender Firewall to lift a focus session's
    /// network block, so the line cannot carry less than the names themselves.</summary>
    private static bool IsAllowed(UiString s) =>
        s.Text.Contains("ChargeKeeper focus session: broker", StringComparison.Ordinal);

    internal readonly record struct UiString(string Kind, string File, string Text);

    [Fact]
    public void EveryHoverTextFitsTwoShortLines() => AssertWithin("hover", HoverCap);

    [Fact]
    public void EveryDescriptionFitsThreeShortLines() => AssertWithin("body", BodyCap);

    /// <summary>A floor under the extraction. Were an attribute renamed, or a folder moved out of
    /// the sweep, the two caps above would pass by finding nothing at all.</summary>
    [Fact]
    public void TheSweepReachesEveryKindOfString()
    {
        UiString[] all = ShippedStrings().ToArray();

        Assert.True(all.Count(s => s.Kind == "hover") >= 45,
                    $"only {all.Count(s => s.Kind == "hover")} hover texts found; the sweep has stopped reaching them.");
        Assert.True(all.Count(s => s.Kind == "body") >= 120,
                    $"only {all.Count(s => s.Kind == "body")} descriptions found; the sweep has stopped reaching them.");
    }

    // The three checks below prove the caps can fail. Each runs the same extractor over synthetic
    // markup rather than over the tree, so the guard is exercised without lengthening a real string.

    [Fact]
    public void AnOverlongTooltipAttributeIsCaught() =>
        Assert.Contains(FromMarkup($"<Button ToolTipService.ToolTip=\"{new string('x', HoverCap + 1)}\"/>"),
                        s => s.Kind == "hover" && s.Text.Length > HoverCap);

    [Fact]
    public void AnOverlongInfoBubbleIsCaught() =>
        Assert.Contains(FromMarkup($"<zz:InfoIcon Subject=\"a thing\" Info=\"{new string('x', HoverCap + 1)}\"/>"),
                        s => s.Kind == "hover" && s.Text.Length > HoverCap);

    [Fact]
    public void AnOverlongCardDescriptionIsCaught() =>
        Assert.Contains(FromMarkup($"<SettingsCard Header=\"A\" Description=\"{new string('x', BodyCap + 1)}\"/>"),
                        s => s.Kind == "body" && s.Text.Length > BodyCap);

    /// <summary>A tooltip's headline and its body are one hover text, so they are measured together
    /// rather than each slipping under the cap on its own.</summary>
    [Fact]
    public void AToolTipsHeadlineAndBodyAreMeasuredAsOneString()
    {
        string half = new('x', (HoverCap / 2) + 1);
        UiString[] found = FromMarkup(
            $"<TextBlock Text=\"Heading\"><ToolTipService.ToolTip><ToolTip><StackPanel>" +
            $"<TextBlock Text=\"{half}\"/><TextBlock TextWrapping=\"Wrap\">{half}</TextBlock>" +
            $"</StackPanel></ToolTip></ToolTipService.ToolTip></TextBlock>").ToArray();

        Assert.Contains(found, s => s.Kind == "hover" && s.Text.Length > HoverCap);
    }

    private static void AssertWithin(string kind, int cap)
    {
        string[] over = ShippedStrings()
            .Where(s => s.Kind == kind && s.Text.Length > cap && !IsAllowed(s))
            .Select(s => $"{s.File}: {s.Text.Length} characters, cap {cap} — {s.Text}")
            .ToArray();

        Assert.True(over.Length == 0, string.Join(Environment.NewLine, over));
    }

    private static readonly Regex TipBlock =
        new(@"<ToolTipService\.ToolTip>.*?</ToolTipService\.ToolTip>", RegexOptions.Singleline);
    private static readonly Regex TipAttribute = new(@"ToolTipService\.ToolTip\s*=\s*""([^""]*)""");
    private static readonly Regex InfoAttribute = new(@"\bInfo\s*=\s*""([^""]*)""");
    private static readonly Regex TextAttribute = new(@"\bText\s*=\s*""([^""]*)""");
    private static readonly Regex TextBlockInner =
        new(@"<TextBlock\b[^>]*(?<!/)>(?<inner>.*?)</TextBlock>", RegexOptions.Singleline);
    private static readonly Regex DescriptionAttribute = new(@"\bDescription\s*=\s*""([^""]*)""");
    private static readonly Regex TextBlockWithText = new(@"<TextBlock\b[^>]*?\bText\s*=\s*""([^""]*)""");
    private static readonly Regex TextBlockContent = new(@"<TextBlock\b[^>]*(?<!/)>([^<]*)</TextBlock>");
    private static readonly Regex Markup = new(@"<[^>]+>");
    private static readonly Regex Spaces = new(@"\s+");
    private static readonly Regex SetToolTipCall =
        new(@"SetToolTip\s*\([^,]+,\s*""((?:[^""\\]|\\.)*)""\s*\)");
    private static readonly Regex DescriptionAssignment =
        new(@"\.(?:Description|Info)\s*=\s*""((?:[^""\\]|\\.)*)""");

    /// <summary>Every hover text and description in one file of markup.</summary>
    internal static IEnumerable<UiString> FromMarkup(string text, string file = "test")
    {
        foreach (Match m in TipAttribute.Matches(text))
            yield return new UiString("hover", file, Decode(m.Groups[1].Value));

        foreach (Match m in InfoAttribute.Matches(text))
            yield return new UiString("hover", file, Decode(m.Groups[1].Value));

        // A tooltip's headline and its body are one hover text, so the pieces are joined before
        // being measured.
        foreach (Match block in TipBlock.Matches(text))
        {
            var pieces = new List<string>();
            foreach (Match t in TextAttribute.Matches(block.Value))
                pieces.Add(Decode(t.Groups[1].Value));
            foreach (Match t in TextBlockInner.Matches(block.Value))
            {
                string inner = Decode(Spaces.Replace(Markup.Replace(t.Groups["inner"].Value, " "), " ")).Trim();
                if (inner.Length > 0) pieces.Add(inner);
            }

            if (pieces.Count > 0)
                yield return new UiString("hover", file, string.Join(" ", pieces));
        }

        // Blanked rather than removed, so what remains keeps its offsets and a tooltip's own pieces
        // are not counted a second time as body text.
        string outsideTips = TipBlock.Replace(text, m => new string(' ', m.Length));

        foreach (Match m in DescriptionAttribute.Matches(outsideTips))
            yield return new UiString("body", file, Decode(m.Groups[1].Value));

        foreach (Match m in TextBlockWithText.Matches(outsideTips))
            yield return new UiString("body", file, Decode(m.Groups[1].Value));

        foreach (Match m in TextBlockContent.Matches(outsideTips))
        {
            string inner = Decode(Spaces.Replace(m.Groups[1].Value, " ")).Trim();
            if (inner.Length > 0) yield return new UiString("body", file, inner);
        }
    }

    private static IEnumerable<UiString> FromCode(string text, string file)
    {
        foreach (Match m in SetToolTipCall.Matches(text))
            yield return new UiString("hover", file, m.Groups[1].Value);

        foreach (Match m in DescriptionAssignment.Matches(text))
            yield return new UiString("body", file, m.Groups[1].Value);
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value);

    /// <summary>Everything the application itself ships. The shared library's own strings are not
    /// this repository's to shorten.</summary>
    private static IEnumerable<UiString> ShippedStrings()
    {
        string root = Path.GetDirectoryName(RepoFiles.Find("ChargeKeeper.csproj"))!;

        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".xaml" or ".cs")) continue;

            string relative = Path.GetRelativePath(root, file);
            string top      = relative.Split(Path.DirectorySeparatorChar)[0];
            if (top is "Tests" or "bin" or "obj" or "publish" or "brand" or "Vendors"
                    or "installer" or "native" or "scripts") continue;

            string text = File.ReadAllText(file);
            IEnumerable<UiString> found = extension == ".xaml"
                ? FromMarkup(text, relative)
                : FromCode(text, relative);

            foreach (UiString s in found)
                if (s.Text.Trim().Length > 0) yield return s;
        }
    }
}
