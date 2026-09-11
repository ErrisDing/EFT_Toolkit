using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Audio.Processes;

/// <summary>
/// Watches for the target executable by reading the process list on a timer.
/// </summary>
/// <remarks>
/// <para>
/// Polling rather than a change notification, because Windows offers none for process creation that
/// does not amount to a kernel callback or a WMI subscription. At one poll a second over a process
/// list that takes well under a millisecond to read, the cost is a rounding error, and it needs no
/// privilege the application does not already have.
/// </para>
/// <para>
/// The observation is published as a whole set rather than a stream of arrivals and departures, so
/// a poll that missed nothing tells subscribers nothing.
/// </para>
/// </remarks>
public sealed class PollingProcessMonitor : IProcessMonitor
{
    /// <summary>
    /// One second. The answer changes when the user starts or stops the game, which is not something
    /// that happens between two consecutive frames.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private static readonly IReadOnlySet<int> NoProcesses = new HashSet<int>();

    private readonly IProcessSnapshotProvider _snapshotProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly IAppLogger? _logger;

    /// <summary>Serializes starting and stopping so a loop cannot be left behind by a lost race.</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private CancellationTokenSource? _cancellation;
    private Task? _pollingLoop;
    private string? _executableName;

    private IReadOnlySet<int> _processIds = NoProcesses;

    public PollingProcessMonitor(
        IProcessSnapshotProvider snapshotProvider,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);

        _snapshotProvider = snapshotProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _logger = logger;
    }

    public bool IsRunning => ProcessIds.Count > 0;

    public IReadOnlySet<int> ProcessIds => Volatile.Read(ref _processIds);

    public event EventHandler? Changed;

    public async Task StartAsync(string executableName, CancellationToken cancellationToken)
    {
        if (!ProcessNames.TryNormalize(executableName, out string? normalized))
        {
            throw new ArgumentException(
                "An executable name must be a bare file name: no directory, and not empty.",
                nameof(executableName));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cancellation is not null &&
                string.Equals(_executableName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                // Already watching this one. Restarting would clear the set and re-announce a
                // process that never stopped.
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);

            _executableName = normalized;
            _cancellation = new CancellationTokenSource();

            // Deliberately not awaited, and deliberately stored: StopCoreAsync is what waits for it.
            _pollingLoop = PollAsync(normalized, _cancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    /// <summary>Ends the polling loop and forgets the executable it was watching.</summary>
    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? loop = _pollingLoop;

        _cancellation = null;
        _pollingLoop = null;
        _executableName = null;

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);

            if (loop is not null)
            {
                // The loop returns rather than throws on cancellation, so this only observes it
                // finishing and never surfaces a cancellation as a failure.
                await loop.ConfigureAwait(false);
            }

            cancellation.Dispose();
        }

        Publish(NoProcesses);
    }

    private async Task PollAsync(string executableName, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(_pollInterval, _timeProvider);

        while (true)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Publish(_snapshotProvider.GetProcessIds(executableName));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // One unreadable process must not end monitoring for the rest of the session: the
                // game starting later is exactly the event this loop exists to notice.
                _logger?.Write(
                    LogLevel.Warning,
                    "audio.process.snapshotFailed",
                    new Dictionary<string, object?> { ["executableName"] = executableName },
                    exception);
            }
        }
    }

    /// <summary>Publishes an observation, raising <see cref="Changed"/> only if it differs.</summary>
    private void Publish(IReadOnlySet<int> processIds)
    {
        IReadOnlySet<int> previous = Interlocked.Exchange(ref _processIds, processIds);

        if (previous.SetEquals(processIds))
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
