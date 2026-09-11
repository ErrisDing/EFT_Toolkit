using EftToolkit.App.Commands;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.App;

/// <summary>
/// The panel's switches start transitions that take real time — writing gamma ramps to several
/// monitors, opening a WASAPI stream — so the command that started one refuses to start another
/// until it finishes.
/// </summary>
public class AsyncRelayCommandTests
{
    private readonly List<Exception> _reported = [];

    [Fact]
    public async Task Executing_runs_the_operation()
    {
        int runs = 0;
        AsyncRelayCommand command = new(() => { runs++; return Task.CompletedTask; }, _reported.Add);

        await command.ExecuteAsync();

        Assert.Equal(1, runs);
        Assert.False(command.IsRunning);
        Assert.Empty(_reported);
    }

    [Fact]
    public async Task A_second_execution_while_one_is_running_is_ignored()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int runs = 0;
        AsyncRelayCommand command = new(
            async () =>
            {
                runs++;
                await release.Task;
            },
            _reported.Add);

        Task first = command.ExecuteAsync();

        await AsyncWait.UntilAsync(() => command.IsRunning, "the operation started");
        Assert.False(command.CanExecute(null));

        // A second click is a second transition queued behind the first, and the two would fight
        // over the same module.
        await command.ExecuteAsync();
        Assert.Equal(1, runs);

        release.SetResult();
        await first;

        Assert.Equal(1, runs);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task A_failure_is_handed_to_the_caller_rather_than_thrown_at_the_binding()
    {
        InvalidOperationException failure = new("the ramp was refused");
        AsyncRelayCommand command = new(() => Task.FromException(failure), _reported.Add);

        // ICommand.Execute is a void member: a fault that escapes it has nowhere to go, so the
        // exception comes back through the reporter the view model gave the command.
        await command.ExecuteAsync();

        Assert.Equal(failure, Assert.Single(_reported));
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task A_command_that_reports_it_cannot_execute_does_nothing()
    {
        int runs = 0;
        bool allowed = false;

        AsyncRelayCommand command = new(
            () => { runs++; return Task.CompletedTask; },
            _reported.Add,
            () => allowed);

        await command.ExecuteAsync();
        Assert.Equal(0, runs);
        Assert.False(command.CanExecute(null));

        allowed = true;
        Assert.True(command.CanExecute(null));

        await command.ExecuteAsync();
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task The_button_is_told_to_re_evaluate_while_the_operation_runs_and_after_it()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int raised = 0;

        AsyncRelayCommand command = new(() => release.Task, _reported.Add);
        command.CanExecuteChanged += (_, _) => raised++;

        Task execution = command.ExecuteAsync();

        await AsyncWait.UntilAsync(() => Volatile.Read(ref raised) >= 1, "the button was disabled");
        Assert.Equal(1, Volatile.Read(ref raised));

        release.SetResult();
        await execution;

        await AsyncWait.UntilAsync(() => Volatile.Read(ref raised) >= 2, "the button was enabled again");
    }

    [Fact]
    public void Executing_through_the_command_interface_runs_the_operation()
    {
        int runs = 0;
        System.Windows.Input.ICommand command =
            new AsyncRelayCommand(() => { runs++; return Task.CompletedTask; }, _reported.Add);

        // This is the path a button's click takes.
        command.Execute(null);

        Assert.Equal(1, runs);
    }
}
