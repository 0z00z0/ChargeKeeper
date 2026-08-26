using Microsoft.UI.Xaml.Controls;

namespace ChargeKeeper.UI;

/// <summary>
/// The "Current network" row, shown on both the Smart Charge and the Keep Awake page. The two
/// describe the same network from the same service, so the card and its explanation are declared
/// once and each page keeps its own instance to write into.
/// </summary>
/// <remarks>
/// Layout only: the value is pushed in by the page, and nothing here reads the network service.
/// </remarks>
public sealed partial class CurrentNetworkCard : UserControl
{
    public CurrentNetworkCard() => InitializeComponent();

    /// <summary>The line describing the network in force. The em dash placeholder stands until a
    /// page first writes one.</summary>
    public string Value
    {
        get => ValueText.Text;
        set => ValueText.Text = value ?? "";
    }
}
