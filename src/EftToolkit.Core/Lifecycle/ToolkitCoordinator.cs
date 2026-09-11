using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Display;
using EftToolkit.Core.Modules;
using EftToolkit.Core.Platform;

namespace EftToolkit.Core.Lifecycle;

/// <summary>
/// Decides what runs, in what order, and what happens when one of those things fails. This is the
/// only place that knows the display and audio modules are two halves of one application.
/// </summary>
/// <remarks>
/// <para>
/// The two modules are independent by design: audio being unavailable never removes display
/// enhancement, and the display module never waits on an audio device. What is shared is the
/// lifecycle, and it is shared here rather than by either module knowing about the other.
/// </para>
/// <para>
/// Shutdown runs exactly once, no matter how many callers ask for it or how many of them are the
/// same caller, and it always finishes: each step holds a deadline, and the last two — the stored
/// configuration and the log — run outside it. Being finite matters more than being complete,
/// because the alternative to a shutdown that ends is a process that never does.
/// </para>
/// </remarks>
public sealed class ToolkitCoordinator : IAsyncDisposable
{
    /// <summary>
    /// How long the bounded steps have together before the deadline takes the decision. Long enough
    /// for a device that is merely slow, short enough that a person waiting to log off does not
    /// notice it.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly IDisplayController _display;
    private readonly IAudioController _audio;
    private readonly IHotkeyService _hotkeys;
    private readonly IPlatformEventSource _platformEvents;
    private readonly IOptionsStore _optionsStore;
    private readonly IAppLogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _shutdownTimeout;

    /// <summary>Serializes start-up and the user-facing transitions against each other.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards the once-only shutdown, which must be visible before any of it runs.</summary>
    private readonly object _shutdownGate = new();

    private volatile ToolkitOptions _options = ToolkitOptions.CreateDefault();

    private bool _started;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// Whether each module is switched on, as opposed to merely running. These are read from event
    /// threads that have nothing to do with the transition that set them.
    /// </summary>
    private volatile bool _displayEnabled;
    private volatile bool _audioEnabled;

    /// <summary>Set before any shutdown work starts, so a late command cannot restart a module.</summary>
    private volatile bool _shuttingDown;

    private Task<ShutdownResult>? _shutdown;

    public ToolkitCoordinator(
        IDisplayController display,
        IAudioController audio,
        IHotkeyService hotkeys,
        IPlatformEventSource platformEvents,
        IOptionsStore optionsStore,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? shutdownTimeout = null)
    {
        _display = display;
        _audio = audio;
        _hotkeys = hotkeys;
        _platformEvents = platformEvents;
        _optionsStore = optionsStore;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
    }

    /// <summary>The configuration in force: what was loaded, plus every change that has been stored.</summary>
    public ToolkitOptions Options => _options;

    public bool IsDisplayEnabled => _displayEnabled;

    public bool IsAudioEnabled => _audioEnabled;

    /// <summary>
    /// Loads the configuration, restores anything a previous run left behind, and switches on the
    /// modules the user had on.
    /// </summary>
    /// <remarks>
    /// Calling this twice does nothing the second time. A start that fails part-way leaves the
    /// modules in whatever state they reached and does not mark the toolkit started, so the caller
    /// can report the failure and, if it chooses, try again.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();

            if (_started)
            {
                return;
            }

            // Validated here as well as in the store. The store is required to sanitize what it
            // reads, and this is the guarantee that what is in force is approved whatever the store
            // handed over — a gamma value outside the approved range is a black screen, not a
            // brighter one.
            _options = OptionsValidator.Validate(
                await _optionsStore.LoadAsync(cancellationToken).ConfigureAwait(false));

            // Before anything is switched on. A ramp left behind by a previous run of this
            // application is put back before this one is allowed to write over it, and a ramp this
            // application never wrote is left alone by the recovery itself.
            await _display.RecoverAsync(cancellationToken).ConfigureAwait(false);

            Subscribe();

            await _platformEvents.StartAsync(cancellationToken).ConfigureAwait(false);

            if (_options.Display.Enabled)
            {
                await EnableDisplayAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_options.Audio.Enabled)
            {
                await EnableAudioAsync(cancellationToken).ConfigureAwait(false);
            }

            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Switches display enhancement on or off and stores the choice.</summary>
    public async Task SetDisplayEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();

            if (enabled != _displayEnabled)
            {
                if (enabled)
                {
                    await EnableDisplayAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DisableDisplayAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveOptionsAsync(
                _options with { Display = _options.Display with { Enabled = enabled } },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Switches the audio enhancement on or off and stores the choice.</summary>
    /// <remarks>
    /// Nothing here touches a display service, and nothing in the display transition touches an
    /// audio one. The two can be switched independently, in either order, and a failure in one
    /// leaves the other where it was.
    /// </remarks>
    public async Task SetAudioEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfShuttingDown();

            if (enabled != _audioEnabled)
            {
                if (enabled)
                {
                    await EnableAudioAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await BypassAndDisableAudioAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveOptionsAsync(
                _options with { Audio = _options.Audio with { Enabled = enabled } },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tears the toolkit down in the documented order, once, and returns what happened.
    /// </summary>
    /// <remarks>
    /// Every call after the first returns the same task, so a shutdown started by a tray command and
    /// a shutdown started by the process exiting are one shutdown, not two that race.
    /// </remarks>
    public Task<ShutdownResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownGate)
        {
            if (_shutdown is not null)
            {
                return _shutdown;
            }

            // Set here, on the caller's thread, rather than inside the shutdown task: by the time
            // ShutdownAsync returns, a command that arrives must already be refused.
            _shuttingDown = true;

            TaskCompletionSource<ShutdownResult> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            _shutdown = completion.Task;

            // Off the caller's thread. The caller is usually a UI thread on its way out, and a
            // device that has stopped answering must not be able to block a message pump.
            _ = Task.Run(
                () => RunShutdownAsync(cancellationToken, completion),
                CancellationToken.None);

            return _shutdown;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- transitions

    private async Task EnableDisplayAsync(CancellationToken cancellationToken)
    {
        await _display.EnableAsync(cancellationToken).ConfigureAwait(false);
        _displayEnabled = true;

        // The shortcuts come after the module, never before: a preset that arrived while the module
        // was still starting would have nothing to apply to.
        await _hotkeys.RegisterAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DisableDisplayAsync(CancellationToken cancellationToken)
    {
        // The shortcuts go first, so no preset can arrive while the ramps are being put back.
        await _hotkeys.UnregisterAsync(cancellationToken).ConfigureAwait(false);
        _displayEnabled = false;
        await _display.DisableAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnableAudioAsync(CancellationToken cancellationToken)
    {
        await _audio.EnableAsync(cancellationToken).ConfigureAwait(false);
        _audioEnabled = true;
    }

    private async Task BypassAndDisableAudioAsync(CancellationToken cancellationToken)
    {
        // Bypass first: the limiter stops processing before the stream closes, so stopping it is not
        // heard as a click.
        await _audio.SetBypassAsync(true, cancellationToken).ConfigureAwait(false);
        _audioEnabled = false;
        await _audio.DisableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the configuration and only then adopts it. The file is the record of what the user
    /// asked for, so a write that failed must not leave the running application believing it
    /// succeeded.
    /// </summary>
    private async Task SaveOptionsAsync(ToolkitOptions options, CancellationToken cancellationToken)
    {
        await _optionsStore.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        _options = options;
    }

    // ---------------------------------------------------------------- events

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _platformEvents.DisplayEnvironmentChanged += OnDisplayEnvironmentChanged;
        _hotkeys.PresetRequested += OnPresetRequested;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _platformEvents.DisplayEnvironmentChanged -= OnDisplayEnvironmentChanged;
        _hotkeys.PresetRequested -= OnPresetRequested;
        _subscribed = false;
    }

    /// <summary>
    /// Handles a display shortcut. It returns immediately: the caller is the message window's
    /// thread, and waiting there for a gamma write would stall every other message that window
    /// carries, including the next shortcut.
    /// </summary>
    private void OnPresetRequested(object? sender, DisplayPresetKind preset)
    {
        if (!_displayEnabled)
        {
            return;
        }

        _ = ApplyPresetDetachedAsync(preset);
    }

    private async Task ApplyPresetDetachedAsync(DisplayPresetKind preset)
    {
        try
        {
            await _display.ApplyPresetAsync(preset, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Nothing is waiting for this task, so a fault left unlogged would be a fault nobody
            // ever saw.
            TryLog(LogLevel.Error, "lifecycle.presetFailed", preset.ToString(), exception);
        }
    }

    /// <summary>
    /// Handles a Windows event that invalidates every gamma ramp. Windows reports one monitor
    /// change as several messages; the platform event source collapses them, and this still returns
    /// immediately rather than doing the re-enumeration on whichever thread raised it.
    /// </summary>
    private void OnDisplayEnvironmentChanged(object? sender, EventArgs e)
    {
        if (!_displayEnabled)
        {
            return;
        }

        _ = RefreshDetachedAsync();
    }

    private async Task RefreshDetachedAsync()
    {
        try
        {
            await _display.RefreshAndReapplyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLog(LogLevel.Error, "lifecycle.refreshFailed", DisplayPresetKind.Original.ToString(), exception);
        }
    }

    // ---------------------------------------------------------------- shutdown

    private async Task RunShutdownAsync(
        CancellationToken callerCancellation,
        TaskCompletionSource<ShutdownResult> completion)
    {
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        List<ShutdownFailure> failures = [];

        try
        {
            using ShutdownBudget budget = new(callerCancellation, _shutdownTimeout);

            // The first step: from here on the toolkit accepts no new commands, so a toggle that
            // arrives mid-shutdown cannot restart a module the next step is about to tear down.
            TryLog(LogLevel.Information, "lifecycle.step", ShutdownSteps.RejectCommands);

            Unsubscribe();
            _displayEnabled = false;
            _audioEnabled = false;

            // A transition that is already running is allowed to finish before the teardown starts.
            await WaitForRunningTransitionAsync(budget).ConfigureAwait(false);

            await RunStepAsync(
                ShutdownSteps.AudioBypass,
                budget,
                failures,
                token => _audio.SetBypassAsync(true, token)).ConfigureAwait(false);

            await RunStepAsync(
                ShutdownSteps.AudioDisable,
                budget,
                failures,
                token => _audio.DisableAsync(token)).ConfigureAwait(false);

            await RunStepAsync(
                ShutdownSteps.DisplayDisable,
                budget,
                failures,
                token => _display.DisableAsync(token)).ConfigureAwait(false);

            await RunStepAsync(
                ShutdownSteps.HotkeysUnregister,
                budget,
                failures,
                token => _hotkeys.UnregisterAsync(token)).ConfigureAwait(false);

            await RunStepAsync(
                ShutdownSteps.PlatformEventsStop,
                budget,
                failures,
                token => _platformEvents.StopAsync(token)).ConfigureAwait(false);

            // Outside the deadline. These two decide what the next launch sees, and a shutdown that
            // ran out of time is exactly when the stored configuration and the log matter most.
            await RunFinalStepAsync(
                ShutdownSteps.SettingsSave,
                failures,
                () => _optionsStore.SaveAsync(_options, CancellationToken.None)).ConfigureAwait(false);

            await RunFinalStepAsync(
                ShutdownSteps.LoggerFlush,
                failures,
                () => _logger is { } logger ? logger.DisposeAsync().AsTask() : Task.CompletedTask)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A shutdown that never completes takes the application with it, so nothing at all is
            // allowed to escape this method.
            failures.Add(new ShutdownFailure("<coordinator>", exception.Message, Exception: exception));
        }
        finally
        {
            completion.TrySetResult(new ShutdownResult(startedAt, _timeProvider.GetUtcNow(), failures));
        }
    }

    /// <summary>
    /// Waits for a transition that is already in progress. The wait is bounded by the shutdown
    /// deadline and teardown happens either way: waiting for a device that may never answer is the
    /// one thing a shutdown must not do.
    /// </summary>
    private async Task WaitForRunningTransitionAsync(ShutdownBudget budget)
    {
        try
        {
            await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _gate.Release();
    }

    private async Task RunStepAsync(
        string step,
        ShutdownBudget budget,
        List<ShutdownFailure> failures,
        Func<CancellationToken, Task> action)
    {
        TryLog(LogLevel.Information, "lifecycle.step", step);

        if (budget.Token.IsCancellationRequested)
        {
            failures.Add(new ShutdownFailure(
                step,
                "The shutdown deadline passed before this step ran.",
                TimedOut: true));
            return;
        }

        Task stepTask = InvokeAsync(action, budget.Token);

        if (await Task.WhenAny(stepTask, budget.Expired).ConfigureAwait(false) != stepTask)
        {
            // The step is not abandoned mid-call — the token it holds is cancelled — but shutdown
            // stops waiting for it.
            Observe(stepTask);

            failures.Add(new ShutdownFailure(
                step,
                "The step did not finish inside the shutdown deadline.",
                TimedOut: true));
            return;
        }

        try
        {
            await stepTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLog(LogLevel.Error, "lifecycle.stepFailed", step, exception);
            failures.Add(new ShutdownFailure(step, exception.Message, Exception: exception));
        }
    }

    /// <summary>Runs a step that the deadline does not apply to.</summary>
    private async Task RunFinalStepAsync(
        string step,
        List<ShutdownFailure> failures,
        Func<Task> action)
    {
        TryLog(LogLevel.Information, "lifecycle.step", step);

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLog(LogLevel.Error, "lifecycle.stepFailed", step, exception);
            failures.Add(new ShutdownFailure(step, exception.Message, Exception: exception));
        }
    }

    /// <summary>
    /// Calls a step. Reached through an async method so that a step that throws before it returns a
    /// task fails the same way as one that throws inside it.
    /// </summary>
    private static async Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        await action(cancellationToken).ConfigureAwait(false);

    /// <summary>Keeps a step that was left running from surfacing its fault somewhere unrelated.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void ThrowIfShuttingDown()
    {
        if (_shuttingDown)
        {
            throw new InvalidOperationException(
                "The toolkit is shutting down and no longer accepts changes.");
        }
    }

    /// <summary>
    /// Writes a line about the shutdown. The last step disposes the logger, so a step that fails
    /// afterwards is a step whose report cannot be written; a log that refuses a write is not
    /// allowed to be the thing that ends a shutdown.
    /// </summary>
    private void TryLog(LogLevel level, string eventName, string step, Exception? exception = null)
    {
        if (_logger is not { } logger)
        {
            return;
        }

        try
        {
            logger.Write(
                level,
                eventName,
                new Dictionary<string, object?> { ["step"] = step },
                exception);
        }
        catch (Exception)
        {
            // Nothing useful can be done about a logger that will not log.
        }
    }

    /// <summary>
    /// The one deadline a shutdown runs against.
    /// </summary>
    /// <remarks>
    /// The toolkit's own teardown deliberately ignores cancellation, so that a half-released device
    /// is never abandoned. That makes a device which has stopped answering able to hold a step open
    /// indefinitely, which is why the deadline is enforced here by racing the step rather than by
    /// trusting every participant to honour a token.
    /// </remarks>
    private sealed class ShutdownBudget : IDisposable
    {
        private readonly CancellationTokenSource _source;

        internal ShutdownBudget(CancellationToken callerCancellation, TimeSpan timeout)
        {
            // Linked, so a caller that is already giving up ends the shutdown sooner than the
            // deadline would.
            _source = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
            _source.CancelAfter(timeout);

            Expired = Task.Delay(Timeout.Infinite, _source.Token);
        }

        internal CancellationToken Token => _source.Token;

        /// <summary>Completes when the deadline passes, and never otherwise.</summary>
        internal Task Expired { get; }

        public void Dispose() => _source.Dispose();
    }
}
