namespace ChargeKeeper.Helpers;

/// <summary>Minimal <see cref="System.Windows.Input.ICommand"/> that delegates to an
/// <see cref="Action"/>.</summary>
internal sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    // Required by ICommand; always enabled, so it is deliberately never raised.
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
