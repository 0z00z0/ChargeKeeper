using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChargeKeeper.UI;

/// <summary>
/// The rule and sub-heading that open a section on a Settings page, with an optional explanation
/// beside the heading. Every sub-headed group on the Smart Charge, Keep Awake and Lid close pages
/// starts with one, so the chrome is declared once and the three pages cannot drift apart.
/// </summary>
/// <remarks>
/// Layout only: a section supplies its own cards as ordinary siblings after the header, so nothing
/// here knows what a section contains. Plain CLR properties rather than DependencyProperties,
/// matching <see cref="InfoIcon"/> — every use site is a literal string set once in XAML.
/// </remarks>
public sealed partial class SettingsSectionHeader : UserControl
{
    private string _heading = "";

    public SettingsSectionHeader() => InitializeComponent();

    /// <summary>The sub-heading text.</summary>
    public string Heading
    {
        get => _heading;
        set { _heading = value ?? ""; HeadingText.Text = _heading; }
    }

    /// <summary>
    /// The explanation behind the heading's info icon. The icon stays hidden while this is unset, so
    /// a section with nothing to explain carries no affordance.
    /// </summary>
    public string Info
    {
        get => Explanation.Info;
        set
        {
            Explanation.Info       = value ?? "";
            Explanation.Visibility = Explanation.Info.Length == 0 ? Visibility.Collapsed
                                                                 : Visibility.Visible;
        }
    }

    /// <summary>What the explanation is about, passed on as the info icon's accessible name.</summary>
    public string Subject
    {
        get => Explanation.Subject;
        set => Explanation.Subject = value ?? "";
    }
}
