using System;
using System.Windows;
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

    // Back the event with WPF's CommandManager.RequerySuggested so a button
    // bound to this command auto-updates on every mouse click, keyboard event
    // and focus change. Without this hook the button only re-checks CanExecute
    // when we call RefreshCanExecute manually, which meant e.g. the Browse
    // "Install / Update selected" button stayed greyed out after the user
    // clicked a ListBox row (nothing in the setter poked the command).
    private EventHandler? _canExecuteChanged;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Ask WPF to re-query every command's <see cref="CanExecute"/> on the
    /// next dispatcher turn. Marshalled to the UI thread when called from a
    /// worker: WPF's CommandManager asserts thread affinity, so a stray
    /// <c>ConfigureAwait(false)</c> in an async command body would otherwise
    /// crash the process. Keeping that check here makes that class of bug
    /// non-fatal.
    /// </summary>
    public void RefreshCanExecute()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess())
        {
            disp.BeginInvoke(new Action(RefreshCanExecute));
            return;
        }
        CommandManager.InvalidateRequerySuggested();
        // Also raise our own event, in case someone subscribed to it directly
        // without going through the CommandManager pipe (tests, toolkits).
        _canExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
