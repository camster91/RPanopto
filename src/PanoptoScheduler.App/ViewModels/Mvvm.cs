using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Command that runs an async operation and blocks re-entry while it runs.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            // The last line of defence, and it has to be here. This method is
            // async void, so anything that leaves it reaches
            // DispatcherUnhandledException — and that handler closes the app,
            // because there is no way to know how much state a stray exception
            // left consistent. An operation that failed is not a reason to close
            // the app, so the exception stops here instead.
            //
            // The operation's own handler is still the right place to say what
            // happened, in the operation's own words, on the panel's own line —
            // every view model has one, and several say "never let this escape"
            // about exactly this. This catches only what got past one, which is
            // why it says so on screen rather than logging quietly: an
            // unexpected failure that the operator never sees is a failure
            // nobody can report.
            AppLog.Error("A command failed without handling its own error.", ex);

            MessageBox.Show(
                "That operation failed unexpectedly, so it did not run.\n\n"
                + $"A log of what happened was written to:\n{AppLog.Directory}\n\n"
                + "Please send that file on, along with what you were doing at the time.\n\n"
                + $"({ex.GetType().Name}: {ex.Message})",
                "Panopto Scheduler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The one place a caught exception becomes something on screen.
///
/// <para><b>Three things happen to every exception, and they have to happen
/// together.</b> The whole chain is written to the log, because the one-line
/// summary is allowed to be vague and the log is what makes that safe. The
/// summary goes on the status line, because a SOAP fault body is not a sentence
/// for a recording-desk operator. And the full text goes on that line's tooltip,
/// because otherwise the friendly version would be a dead end — an operator who
/// wants to send the detail on has to have it to hand.</para>
///
/// <para>Each view model sets its own <c>Status</c> — the sentence above the
/// detail line differs by operation, and it is the part that says what was being
/// attempted — so this only covers the two that are the same everywhere.</para>
/// </summary>
internal static class Problem
{
    /// <summary>
    /// Logs <paramref name="error"/> under <paramref name="context"/> and returns
    /// the pair to put on screen: the one-line summary, and the full text for its
    /// tooltip.
    /// </summary>
    internal static (string Summary, string Full) Describe(string context, Exception error)
    {
        AppLog.Error(context, error);
        return (ProblemText.Summarise(error), AppLog.Full(error));
    }
}
