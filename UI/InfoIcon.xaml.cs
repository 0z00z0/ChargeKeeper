using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ChargeKeeper.UI;

/// <summary>
/// A small "(i)" button that opens its explanation in a flyout. The one place a Settings page puts
/// the how-it-works detail that would otherwise sit in the visible copy: a card header and its
/// description say WHAT a control does, and everything longer moves in here.
/// </summary>
/// <remarks>
/// Plain CLR properties rather than DependencyProperties — every use site is a literal string set
/// once in XAML, so there is nothing to bind, animate or style against.
/// </remarks>
public sealed partial class InfoIcon : UserControl
{
    private string _info    = "";
    private string _subject = "";

    public InfoIcon()
    {
        InitializeComponent();
        ApplyAutomationName();
    }

    /// <summary>The explanation shown in the flyout.</summary>
    public string Info
    {
        get => _info;
        set { _info = value ?? ""; InfoText.Text = _info; }
    }

    /// <summary>
    /// What the explanation is about, e.g. "Lid delay". Screen readers meet several of these on one
    /// page, so the accessible name has to name the setting rather than repeat "more information".
    /// </summary>
    public string Subject
    {
        get => _subject;
        set { _subject = value ?? ""; ApplyAutomationName(); }
    }

    private void ApplyAutomationName() =>
        AutomationProperties.SetName(Root,
            _subject.Length == 0 ? "More information" : $"More information about {_subject}");
}
