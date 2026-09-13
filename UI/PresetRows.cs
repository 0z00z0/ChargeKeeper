using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.UI;

/// <summary>
/// The saved-value list every Settings page shows: a host panel, an empty state, one row per saved
/// value, the marking on the row in use, and the interlock with the feature's own switch. Each
/// section supplies its item type and its key, the editor cards, the validation, the commit tail,
/// whether activation can be offered at all, and what "in use" means for it — a device read, a
/// running session and a stored value are three different truths, and none of them can be chosen
/// here. Everything around those is built once.
/// </summary>
internal static class PresetRows
{
    /// <summary>The parts of a built row its page still drives.</summary>
    internal sealed record Parts(SettingsExpander Expander, TextBlock Header, Button Activate,
                                 TextBlock Error, Button Delete);

    /// <summary>
    /// Builds the shell around <paramref name="cards"/>. <paramref name="tag"/> identifies the row
    /// to <see cref="Section.RefreshActivation"/> — a name where the items carry a unique one, a list
    /// index where they do not. The description is left unset when blank, so a row without one keeps
    /// the header's own height.
    /// </summary>
    public static Parts Build(string header, string description, object tag,
                              IList<SettingsCard> cards, Brush? errorBrush,
                              string deleteLabel = "Delete preset")
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
        var delete = new Button { Content = deleteLabel };
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

    /// <summary>The placeholder for an empty list. One builder, so the lists cannot drift apart.</summary>
    public static TextBlock EmptyListText(string text) => new()
    {
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity      = 0.7,
        Margin       = new Thickness(0, 4, 0, 4),
    };

    /// <summary>
    /// One saved-value list. <see cref="ActiveTag"/> answers what is in use and <see cref="FeatureOn"/>
    /// whether the feature acts at all: switched off, the marking goes but the stored choice stays, so
    /// switching back on marks the same row again without re-activating it.
    /// </summary>
    internal sealed class Section
    {
        /// <summary>The panel the rows are hosted in.</summary>
        public required StackPanel Panel { get; init; }

        /// <summary>What the list says when it holds nothing.</summary>
        public required string EmptyText { get; init; }

        /// <summary>How many saved values there are, read afresh on every rebuild.</summary>
        public required Func<int> Count { get; init; }

        /// <summary>One row, by position in the list.</summary>
        public required Func<int, SettingsExpander> BuildRow { get; init; }

        /// <summary>The tag of the row in use, or null when none is. The section's own truth: the
        /// thresholds the firmware is running, the session that is going, the stored level.</summary>
        public required Func<object?> ActiveTag { get; init; }

        /// <summary>Whether a row may offer activation at all. Default every row; threshold rows
        /// refuse where the hardware will not accept a write, and a network profile only offers it on
        /// the network it is for — an affordance that cannot work is worse than none.</summary>
        public Func<object, bool> OffersActivation { get; init; } = _ => true;

        /// <summary>The feature's own switch, where it has one. Off, no row is marked.</summary>
        public Func<bool> FeatureOn { get; init; } = () => true;

        public required string ActiveTip { get; init; }
        public required string IdleTip   { get; init; }

        /// <summary>Discards every row and builds the list again from the stored values.</summary>
        public void Rebuild()
        {
            Panel.Children.Clear();
            ApplyActiveResources(Panel);

            int count = Count();
            if (count == 0)
            {
                Panel.Children.Add(EmptyListText(EmptyText));
                return;
            }

            for (int i = 0; i < count; i++) Panel.Children.Add(BuildRow(i));
            RefreshActivation();
        }

        /// <summary>Moves the marking to whatever is in use now. Cheap enough to call from any path
        /// that can change the answer — an edit, an activation, the feature's switch, a network
        /// change.</summary>
        public void RefreshActivation()
        {
            object? active = FeatureOn() ? ActiveTag() : null;

            foreach (var row in Panel.Children.OfType<SettingsExpander>())
            {
                if (row.Content is not Button button || button.Tag is not { } tag) continue;

                bool isActive  = active is not null && Equals(tag, active);
                bool offered   = isActive || OffersActivation(tag);
                button.Content = isActive ? "In use" : "Activate";
                button.IsEnabled  = !isActive;
                button.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
                ToolTipService.SetToolTip(button, isActive ? ActiveTip : IdleTip);

                // The name carries the accent too, so the row reads as active without hunting for the
                // button. Suppressed where the marker itself is hidden, since colour alone would then
                // be the only cue left.
                if (row.Header is TextBlock header) StyleActiveName(header, isActive && offered);
            }
        }
    }

    /// <summary>
    /// The three template resources the in-use marker paints from. The active row disables its own
    /// button, so the DISABLED visual state is what shows the marker — it overrides any Background
    /// or Foreground set on the button itself. Set before the rows are parented, or their templates
    /// never resolve them; activation buttons are the only ones in these panels ever disabled.
    /// </summary>
    private static void ApplyActiveResources(StackPanel panel)
    {
        panel.Resources["ButtonBackgroundDisabled"]  = AppColors.AccentBrush;
        panel.Resources["ButtonBorderBrushDisabled"] = AppColors.AccentBrush;
        panel.Resources["ButtonForegroundDisabled"]  = AppColors.OnAccentBrush;
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
