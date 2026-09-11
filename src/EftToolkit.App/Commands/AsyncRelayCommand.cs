using System.Windows.Input;

namespace EftToolkit.App.Commands;

/// <summary>
/// Runs one asynchronous operation for a button, and refuses to start a second one while the first
/// is still running.
/// </summary>
/// <remarks>
/// <para>
/// Every switch in the panel starts a transition that takes real time: a preset reaches every
/// selected monitor through a call that can block, and enabling audio opens a WASAPI stream. A
/// second click during that would queue a second transition behind the first, and the two would
/// fight over the same module, so the button reports itself unexecutable until the operation it
/// started has finished.
/// </para>
/// <para>
/// A failure is handed to the reporter the view model supplied rather than thrown.
/// <see cref="ICommand.Execute"/> is a <see langword="void"/> member: a fault escaping it belongs to
/// no task, so the panel would show a switch that snapped back with nothing to explain why. The
/// view model's reporter writes it into the panel's own message area.
/// </para>
/// <para>
/// The command is bound to a control and is therefore expected to be created and executed on the
/// thread that owns that control. Nothing here marshals: the continuations that resume it after the
/// awaited operation come back to the captured context, which for a command built by a view model
/// on the UI thread is the dispatcher.
/// </para>
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Action<Exception> _onError;
    private readonly Func<bool>? _canExecute;

    private volatile bool _running;

    /// <param name="execute">The operation.</param>
    /// <param name="onError">
    /// Where a failure goes. Required: a command whose failure has nowhere to go is a bug, and a
    /// default here would let one be written by accident.
    /// </param>
    /// <param name="canExecute">An additional condition, on top of "not already running".</param>
    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _canExecute = canExecute;
    }

    /// <summary>Whether an operation started by this command is still running.</summary>
    public bool IsRunning => _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    /// <summary>
    /// Starts the operation. The parameter is ignored: every command in the panel acts on something
    /// the view model already holds.
    /// </summary>
    public void Execute(object? parameter) => _ = ExecuteAsync();

    /// <summary>
    /// Tells the bound control to ask again. Needed because a condition this command was given can
    /// change without the command being touched: a field whose text stopped being valid has to
    /// disable the button that would send it, and nothing else will tell the button to look.
    /// </summary>
    public void RaiseCanExecuteChanged() => OnCanExecuteChanged();

    /// <summary>Starts the operation and waits for it. What the view model uses when it needs to know.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _running = true;
        OnCanExecuteChanged();

        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
        finally
        {
            _running = false;
            OnCanExecuteChanged();
        }
    }

    private void OnCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
