using System.Runtime.CompilerServices;
using ColorCode;
using ColorCode.Styling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// Draws a PowerShell script as coloured text. The colouring is ColorCode's, the same tokeniser a
/// markdown renderer uses for a code fence; nothing here reads the script itself.
/// </summary>
/// <remarks>
/// ColorCode writes finished text into a <see cref="RichTextBlock"/> and offers no editable surface,
/// so the Scripts page pairs a plain box for editing with this beneath it rather than colouring the
/// text as it is typed. Colouring while editing would need a tokeniser owned here.
/// </remarks>
internal static class PowerShellColouring
{
    /// <summary>Redraws <paramref name="target"/> as <paramref name="script"/>, coloured for the
    /// theme the control is actually rendering in. A failure falls back to the plain text: a preview
    /// that cannot be coloured is still worth reading, and the page must not go down with it.</summary>
    public static void Apply(RichTextBlock target, string script)
    {
        target.Blocks.Clear();
        if (string.IsNullOrEmpty(script)) return;

        try
        {
            Colour(target, script);
        }
        catch (Exception ex)
        {
            AppLog.Error("PowerShellColouring", ex);
            target.Blocks.Clear();
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run { Text = script });
            target.Blocks.Add(paragraph);
        }
    }

    /// <summary>The library call, kept in a method of its own and never inlined: a type the runtime
    /// cannot resolve throws as its method is compiled, which in an inlined body would be thrown at
    /// the caller and past the guard above rather than inside it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Colour(RichTextBlock target, string script)
    {
        var styles = target.ActualTheme == ElementTheme.Dark
            ? StyleDictionary.DefaultDark
            : StyleDictionary.DefaultLight;

        new RichTextBlockFormatter(styles).FormatRichTextBlock(script, Languages.PowerShell, target);
    }
}
