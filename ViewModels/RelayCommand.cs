using System;
using System.Windows.Input;

namespace PDFEditor.ViewModels;

/// <summary>
/// Implementation legere de ICommand pilotee par des delegues.
/// L'etat « executable » est reevalue automatiquement par le CommandManager
/// de WPF (a chaque evenement clavier / souris), comme les commandes routees.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
            LocalCanExecuteChanged += value;
        }
        remove
        {
            CommandManager.RequerySuggested -= value;
            LocalCanExecuteChanged -= value;
        }
    }

    private event EventHandler? LocalCanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_canExecute is null)
        {
            return true;
        }

        try
        {
            return _canExecute(parameter);
        }
        catch
        {
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _execute(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        LocalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
