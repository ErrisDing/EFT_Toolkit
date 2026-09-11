using EftToolkit.Audio.Processes;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Audio;

public class PollingProcessMonitorTests
{
    /// <summary>
    /// Short enough that a test observes several polls, long enough that the loop is not the thing
    /// under measurement.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly FakeProcessSnapshotProvider _provider = new();
    private readonly RecordingLogger _logger = new();

    private PollingProcessMonitor CreateMonitor() =>
        new(_provider, TimeProvider.System, PollInterval, _logger);

    // ---------------------------------------------------------------- observing processes

    [Fact]
    public async Task Processes_that_are_already_running_are_reported_when_the_monitor_starts()
    {
        _provider.ProcessIds = new HashSet<int> { 10, 11 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);

        await WaitUntilAsync(() => monitor.IsRunning, "the running processes were never reported");

        Assert.True(monitor.ProcessIds.SetEquals([10, 11]));
    }

    [Fact]
    public async Task A_game_that_is_not_running_is_reported_as_not_running()
    {
        _provider.ProcessIds = new HashSet<int>();

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);

        await WaitUntilAsync(() => _provider.Queries.Count >= 3, "the monitor never polled");

        Assert.False(monitor.IsRunning);
        Assert.Empty(monitor.ProcessIds);
    }

    [Fact]
    public async Task The_first_observation_of_a_running_process_raises_changed()
    {
        _provider.ProcessIds = new HashSet<int> { 10 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        int raised = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref raised);

        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the first observation was never reported");

        // One observation, one notification: the arrival of the game must not cost a notification
        // per poll for as long as it stays open.
        await Task.Delay(PollInterval * 4);

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task A_process_that_appears_while_the_monitor_runs_is_reported()
    {
        _provider.ProcessIds = new HashSet<int>();

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);

        int raised = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref raised);

        _provider.ProcessIds = new HashSet<int> { 42 };

        // Waiting on the reported state rather than on the notification would race: the monitor
        // publishes the new set before it raises Changed, so the state can be visible for a moment
        // while the handler has not run yet.
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the new process was never reported");

        Assert.True(monitor.IsRunning);
        Assert.True(monitor.ProcessIds.SetEquals([42]));
    }

    [Fact]
    public async Task A_process_that_exits_while_the_monitor_runs_is_reported()
    {
        _provider.ProcessIds = new HashSet<int> { 42 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => monitor.IsRunning, "the running process was never reported");

        int raised = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref raised);

        _provider.ProcessIds = new HashSet<int>();

        // As above: the notification is what this test is about, so it is what is waited on.
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the exit was never reported");

        Assert.False(monitor.IsRunning);
        Assert.Empty(monitor.ProcessIds);
    }

    [Fact]
    public async Task Nothing_is_reported_while_the_process_set_is_unchanged()
    {
        _provider.ProcessIds = new HashSet<int> { 7 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        int raised = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref raised);

        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the process was never reported");

        await Task.Delay(PollInterval * 6);

        // Polling does not mean announcing: a monitor that raised on every poll would spend the
        // session re-evaluating a route that has not changed.
        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.True(monitor.ProcessIds.SetEquals([7]));
    }

    [Fact]
    public async Task The_process_set_is_replaced_rather_than_accumulated()
    {
        _provider.ProcessIds = new HashSet<int> { 1, 2 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => monitor.ProcessIds.Count == 2, "the first set was never reported");

        _provider.ProcessIds = new HashSet<int> { 3 };

        await WaitUntilAsync(() => monitor.ProcessIds.SetEquals([3]), "the second set was never reported");

        Assert.DoesNotContain(1, monitor.ProcessIds);
        Assert.DoesNotContain(2, monitor.ProcessIds);
    }

    [Fact]
    public async Task The_process_set_is_compared_without_regard_to_order()
    {
        _provider.ProcessIds = new HashSet<int> { 4, 5, 6 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        int raised = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref raised);

        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the process was never reported");

        // The same processes reported in a different order are the same processes.
        _provider.ProcessIds = new HashSet<int> { 6, 4, 5 };
        await Task.Delay(PollInterval * 6);

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    // ---------------------------------------------------------------- the name that is matched

    [Fact]
    public async Task The_executable_name_is_reduced_to_its_bare_form_before_the_snapshot_is_taken()
    {
        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov.exe", CancellationToken.None);

        await WaitUntilAsync(() => _provider.Queries.Count >= 1, "the monitor never polled");

        Assert.All(_provider.Queries, query => Assert.Equal("EscapeFromTarkov", query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"sub\EscapeFromTarkov")]
    [InlineData(@"C:\Games\EscapeFromTarkov.exe")]
    public async Task An_executable_name_that_is_not_a_bare_file_name_is_refused(string executableName)
    {
        await using PollingProcessMonitor monitor = CreateMonitor();

        await Assert.ThrowsAsync<ArgumentException>(
            () => monitor.StartAsync(executableName, CancellationToken.None));

        Assert.False(monitor.IsRunning);
    }

    // ---------------------------------------------------------------- failure and lifecycle

    [Fact]
    public async Task A_failing_snapshot_is_logged_and_the_monitor_keeps_polling()
    {
        _provider.Throw = new InvalidOperationException("the process table could not be read");

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);

        await WaitUntilAsync(() => _logger.Entries.Count >= 1, "the snapshot failure was never logged");

        _provider.Throw = null;
        _provider.ProcessIds = new HashSet<int> { 9 };

        // A transient failure must not silently end monitoring for the rest of the session.
        await WaitUntilAsync(() => monitor.IsRunning, "the monitor never recovered from the failure");

        Assert.Contains(_logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Empty(monitor.ProcessIds.Except([9]));
    }

    [Fact]
    public async Task Stopping_clears_the_process_set_and_stops_the_polling()
    {
        _provider.ProcessIds = new HashSet<int> { 3 };

        PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => monitor.IsRunning, "the process was never reported");

        await monitor.StopAsync(CancellationToken.None);

        Assert.False(monitor.IsRunning);
        Assert.Empty(monitor.ProcessIds);

        int pollsAfterStop = _provider.Queries.Count;
        await Task.Delay(PollInterval * 5);

        Assert.Equal(pollsAfterStop, _provider.Queries.Count);

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task Starting_twice_for_the_same_executable_keeps_one_polling_loop()
    {
        _provider.ProcessIds = new HashSet<int> { 8 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => monitor.IsRunning, "the process was never reported");

        int raised = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref raised);

        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await Task.Delay(PollInterval * 6);

        // The second call must not have cleared the set and re-announced it.
        Assert.True(monitor.IsRunning);
        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Starting_a_different_executable_switches_the_monitor_over()
    {
        _provider.ProcessIds = new HashSet<int> { 8 };

        await using PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => monitor.IsRunning, "the first process was never reported");

        _provider.ProcessIds = new HashSet<int> { 12 };
        await monitor.StartAsync("EscapeFromTarkovArena", CancellationToken.None);

        await WaitUntilAsync(
            () => _provider.Queries.Contains("EscapeFromTarkovArena"),
            "the monitor never queried the new executable");

        await WaitUntilAsync(() => monitor.ProcessIds.SetEquals([12]), "the new process was never reported");
    }

    [Fact]
    public async Task Stopping_before_starting_is_harmless()
    {
        await using PollingProcessMonitor monitor = CreateMonitor();

        await monitor.StopAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task Disposing_stops_the_monitor()
    {
        _provider.ProcessIds = new HashSet<int> { 3 };

        PollingProcessMonitor monitor = CreateMonitor();
        await monitor.StartAsync("EscapeFromTarkov", CancellationToken.None);
        await WaitUntilAsync(() => monitor.IsRunning, "the process was never reported");

        await monitor.DisposeAsync();

        Assert.False(monitor.IsRunning);

        int pollsAfterDispose = _provider.Queries.Count;
        await Task.Delay(PollInterval * 5);

        Assert.Equal(pollsAfterDispose, _provider.Queries.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting until {because}.");
    }

    private sealed class FakeProcessSnapshotProvider : IProcessSnapshotProvider
    {
        private readonly object _gate = new();
        private readonly List<string> _queries = [];

        internal IReadOnlySet<int> ProcessIds { get; set; } = new HashSet<int>();

        internal Exception? Throw { get; set; }

        internal IReadOnlyList<string> Queries
        {
            get
            {
                lock (_gate)
                {
                    return _queries.ToArray();
                }
            }
        }

        public IReadOnlySet<int> GetProcessIds(string executableName)
        {
            lock (_gate)
            {
                _queries.Add(executableName);
            }

            if (Throw is { } failure)
            {
                throw failure;
            }

            return ProcessIds;
        }
    }

}
