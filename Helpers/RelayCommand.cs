namespace ChargeKeeper.Helpers;

/// <summary>
/// Minimal <see cref="System.Windows.Input.ICommand"/> implementation that delegates
/// execution to an <see cref="Action"/>. Used to bind tray icon click handlers.
/// </summary>
internal sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    // ICommand requires this event; always enabled so it is intentionally never raised.
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
