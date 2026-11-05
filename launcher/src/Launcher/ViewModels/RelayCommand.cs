using System;
using System.Windows.Input;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>Minimal ICommand for button bindings (no external MVVM toolkit).</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>Raise <see cref="CanExecuteChanged"/> after state the gate depends on changes.</summary>
    public void RefreshCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
