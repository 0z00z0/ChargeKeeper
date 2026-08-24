using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.UI;

/// <summary>
/// The row shell both Settings preset lists share: a header that can carry the in-use accent, an
/// activation button on the header row, the editor cards, and a footer holding the inline error and
/// Delete. Threshold presets and keep-awake presets differ only in their cards and in what "in use"
/// means, so the shell is built once here rather than twice.
/// </summary>
internal static class PresetRows
{
    /// <summary>The parts of a built row its page still drives.</summary>
    internal sealed record Parts(SettingsExpander Expander, TextBlock Header, Button Activate,
                                 TextBlock Error, Button Delete);

    /// <summary>
    /// Builds the shell around <paramref name="cards"/>. <paramref name="tag"/> identifies the row
    /// to <see cref="RefreshActivation"/> — a name where presets carry a unique one, a list index
    /// where they do not. The description is left unset when blank, so a row without one keeps the
    /// header's own height.
    /// </summary>
    public static Parts Build(string header, string description, object tag,
                              IList<SettingsCard> cards, Brush? errorBrush)
    {
        // A TextBlock rather than the plain string, so RefreshActivation has something whose
        // Foreground and FontWeight it can set when this row is the one in use.
        var headerText = new TextBlock { Text = header };

        // In the header row, so activation is one click from the list without opening the editor.
        // Its label, enabled state and visibility all come from RefreshActivation.
        var activate = new Button { Tag = tag, MinWidth = 88 };

        var error = new TextBlock
        {
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility   = Visibility.Collapsed,
            Foreground   = errorBrush,
        };
        var delete = new Button { Content = "Delete preset" };
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 2) };
        footer.Children.Add(error);
        footer.Children.Add(delete);

        var expander = new SettingsExpander
        {
            Header      = headerText,
            Content     = activate,
            ItemsSource = cards,
            ItemsFooter = footer,
        };
        if (description.Length > 0) expander.Description = description;

        return new Parts(expander, headerText, activate, error, delete);
    }

    /// <summary>
    /// The three template resources the in-use marker paints from. The active row disables its own
    /// button, so the DISABLED visual state is what shows the marker — it overrides any Background
    /// or Foreground set on the button itself. Set before the rows are parented, or their templates
    /// never resolve them; activation buttons are the only ones in these panels ever disabled.
    /// </summary>
    public static void ApplyActiveResources(StackPanel panel)
    {
        panel.Resources["ButtonBackgroundDisabled"]  = AppColors.AccentBrush;
        panel.Resources["ButtonBorderBrushDisabled"] = AppColors.AccentBrush;
        panel.Resources["ButtonForegroundDisabled"]  = AppColors.OnAccentBrush;
    }

    /// <summary>Marks the row tagged <paramref name="activeTag"/> as the one in use and leaves the
    /// rest offering activation. <paramref name="visibility"/> hides the marker where activation
    /// cannot work at all — an affordance that cannot work is worse than none.</summary>
    public static void RefreshActivation(StackPanel panel, object? activeTag, Visibility visibility,
                                         string activeTip, string idleTip)
    {
        foreach (var row in panel.Children.OfType<SettingsExpander>())
        {
            if (row.Content is not Button button) continue;
            bool isActive     = activeTag is not null && Equals(button.Tag, activeTag);
            button.Content    = isActive ? "In use" : "Activate";
            button.IsEnabled  = !isActive;
            button.Visibility = visibility;
            ToolTipService.SetToolTip(button, isActive ? activeTip : idleTip);

            // The name carries the accent too, so the row reads as active without hunting for the
            // button. Suppressed where the marker itself is hidden, since colour alone would then be
            // the only cue left.
            if (row.Header is TextBlock header) StyleActiveName(header, isActive && visibility == Visibility.Visible);
        }
    }

    /// <summary>Accent plus weight on the active row's name; both cleared back to the row's
    /// inherited values otherwise, so nothing has to remember what the default was.</summary>
    private static void StyleActiveName(TextBlock header, bool active)
    {
        if (active)
        {
            header.Foreground = AppColors.AccentBrush;
            header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        else
        {
            header.ClearValue(TextBlock.ForegroundProperty);
            header.ClearValue(TextBlock.FontWeightProperty);
        }
    }
}
