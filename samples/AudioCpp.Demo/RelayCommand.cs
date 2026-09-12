using System.Windows.Input;

namespace AudioCpp.Demo;

/// <summary>A command with no package dependency, since this sample deliberately has none.</summary>
public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter) => await execute();

    /// <summary>Awaitable form, so headless callers can sequence the work.</summary>
    public Task ExecuteAsync() => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
