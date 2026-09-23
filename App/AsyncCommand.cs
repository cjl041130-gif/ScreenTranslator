using System.Windows.Input;

namespace ScreenTranslator;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public event EventHandler? CanExecuteChanged;
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; Notify();
        try { await execute(); }
        catch (Exception ex) { onError(ex); }
        finally { _running = false; Notify(); }
    }
}
