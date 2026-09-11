using System.Globalization;
using System.Threading.Channels;
using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Processes;
using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Modules;

namespace EftToolkit.Audio;

/// <summary>
/// Owns the optional audio enhancement: it decides whether the configured route can be opened,
/// opens and closes the stream as the machine changes around it, and reports what it is doing.
/// </summary>
/// <remarks>
/// <para>
/// The module is a follower, not a driver. It never installs, configures, or updates the virtual
/// cable, never changes per-application routing, and never enumerates or opens anything but the two
/// endpoints the profile names — both in WASAPI shared mode.
/// </para>
/// <para>
/// Everything that changes state runs through one transition gate, and no public event is raised
/// while that gate is held. A device or process notification only queues a re-evaluation; the work
/// happens on a worker that debounces, so a cable being re-seated produces one re-evaluation rather
/// than one per notification.
/// </para>
/// <para>
/// Faulted means the machine cannot support the route the user configured — an endpoint is gone, the
/// format is not one the toolkit can carry, or the stream would not open. Degraded means the route
/// is fine but there is nothing to do yet, which today is only the game not being open. The
/// distinction is what the panel acts on: a fault asks the user to change something, a degraded
/// state resolves on its own.
/// </para>
/// </remarks>
public sealed class AudioModule : IAudioController
{
    /// <summary>
    /// How long to wait after a device or process notification before re-evaluating. Long enough to
    /// collapse the burst of notifications a single change produces, short enough that plugging a
    /// cable back in feels immediate.
    /// </summary>
    public static readonly TimeSpan DefaultReEvaluationDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The delay past which the toolkit says so. The buffers the toolkit opens add this much on top
    /// of whatever the endpoints already contributed.
    /// </summary>
    public const double LatencyWarningThresholdMilliseconds = 50.0;

    private readonly AudioOptions _options;
    private readonly IAudioDeviceCatalog _deviceCatalog;
    private readonly IProcessMonitor _processMonitor;
    private readonly Func<IAudioStreamSession> _sessionFactory;
    private readonly IOptionsStore? _optionsStore;
    private readonly IAppLogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _reEvaluationDebounce;

    /// <summary>Serializes enable, disable, bypass, retry, and re-evaluation against each other.</summary>
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    /// <summary>Guards the state read from other threads, and the pending stream fault.</summary>
    private readonly object _stateGate = new();

    private ModuleStatus _status = new(ModuleState.Disabled);
    private bool _targetRunning;
    private bool _routeOpen;
    private bool _hasUnrelatedSessions;

    /// <summary>
    /// The open stream. Volatile because the panel reads the live metrics from a different thread
    /// than the one that opens and closes it.
    /// </summary>
    private volatile IAudioStreamSession? _session;

    /// <summary>Bypass is a user preference that survives the stream being closed and reopened.</summary>
    private bool _bypass;

    /// <summary>The executable the monitor was last started for, so it is not restarted for nothing.</summary>
    private string? _watchedName;

    /// <summary>A fault reported by the stream, waiting for the next transition to act on it.</summary>
    private Exception? _streamFault;
    private IAudioStreamSession? _faultedSession;

    private volatile Channel<byte>? _reEvaluationChannel;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private bool _enabled;
    private volatile bool _disposed;

    /// <param name="options">
    /// The audio configuration to work from. <see cref="AudioOptions.Enabled"/> is not consulted
    /// here: whether the module runs at all is the composition root's decision, made before it calls
    /// <see cref="EnableAsync"/>.
    /// </param>
    /// <param name="optionsStore">
    /// When present, the configuration is re-read before each evaluation, so a profile or endpoint
    /// changed in the panel takes effect on the next re-evaluation rather than at the next launch.
    /// </param>
    /// <param name="reEvaluationDebounce">How long to coalesce notifications; defaults to 500 ms.</param>
    public AudioModule(
        AudioOptions options,
        IAudioDeviceCatalog deviceCatalog,
        IProcessMonitor processMonitor,
        Func<IAudioStreamSession> sessionFactory,
        IOptionsStore? optionsStore = null,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? reEvaluationDebounce = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _deviceCatalog = deviceCatalog ?? throw new ArgumentNullException(nameof(deviceCatalog));
        _processMonitor = processMonitor ?? throw new ArgumentNullException(nameof(processMonitor));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _optionsStore = optionsStore;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reEvaluationDebounce = reEvaluationDebounce ?? DefaultReEvaluationDebounce;
    }

    public ModuleStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    /// <summary>A consistent snapshot of the module and what the route is doing.</summary>
    public AudioModuleStatus Detail
    {
        get
        {
            ModuleStatus status;
            bool targetRunning;
            bool routeOpen;
            bool unrelated;

            lock (_stateGate)
            {
                status = _status;
                targetRunning = _targetRunning;
                routeOpen = _routeOpen;
                unrelated = _hasUnrelatedSessions;
            }

            IAudioStreamSession? session = _session;
            (string? code, string? message) = DescribeWarning(routeOpen, unrelated, session?.Metrics);

            return new AudioModuleStatus(status, targetRunning, routeOpen, unrelated, code, message);
        }
    }

    /// <summary>The live counters, or null while no stream is open.</summary>
    public AudioStreamMetrics? Metrics => _session?.Metrics;

    public bool IsTargetRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _targetRunning;
            }
        }
    }

    public bool IsRouteOpen
    {
        get
        {
            lock (_stateGate)
            {
                return _routeOpen;
            }
        }
    }

    public bool HasUnrelatedSessions
    {
        get
        {
            lock (_stateGate)
            {
                return _hasUnrelatedSessions;
            }
        }
    }

    public event EventHandler<ModuleStatus>? StatusChanged;

    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await RunTransitionAsync(
            async (notifications, token) =>
            {
                if (_enabled)
                {
                    // Idempotent by design: the panel's switch can be wired to something that fires
                    // twice, and that must not become two streams holding the same endpoint.
                    return;
                }

                SetProgress(new ModuleStatus(ModuleState.Starting, "Checking the audio route."));

                Subscribe();
                _enabled = true;
                StartWorker();

                await EvaluateAsync(notifications, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? workerCancellation = null;
        Task? worker = null;

        await RunTransitionAsync(
            async (notifications, token) =>
            {
                if (!_enabled)
                {
                    return;
                }

                SetProgress(new ModuleStatus(ModuleState.Stopping, "Stopping the audio route."));

                // The stop is signalled here but awaited after the gate is released: the worker needs
                // the gate to finish whatever it was doing, so waiting for it while holding the gate
                // would wait forever.
                (workerCancellation, worker) = StopWorkerSignalling();

                _enabled = false;
                Unsubscribe();

                // Teardown runs to completion regardless of the caller's token. The endpoints are
                // being held; abandoning the release would leave the device busy until the process
                // exits, which is worse than a disable that takes a moment.
                await CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);

                await StopMonitoringAsync().ConfigureAwait(false);
                SetTargetRunning(false);

                Settle(notifications, new ModuleStatus(ModuleState.Disabled));
            },
            cancellationToken).ConfigureAwait(false);

        await AwaitWorkerAsync(worker).ConfigureAwait(false);
        workerCancellation?.Dispose();
    }

    public async Task SetBypassAsync(bool bypass, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await RunTransitionAsync(
            async (notifications, token) =>
            {
                _bypass = bypass;

                IAudioStreamSession? session = _session;

                if (session is null || !session.IsRunning)
                {
                    // The preference is kept and applied when a stream next opens. There is nothing
                    // open to switch, and reporting Bypass with no stream would be a lie.
                    return;
                }

                await session.SetBypassAsync(bypass, token).ConfigureAwait(false);
                Settle(notifications, OpenRouteStatus());
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-evaluates now rather than waiting for the next notification. This is what the panel's
    /// retry button calls, and it is why a module that faulted does not need to be disabled and
    /// enabled again.
    /// </summary>
    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await RunTransitionAsync(
            async (notifications, token) =>
            {
                if (!_enabled)
                {
                    return;
                }

                await EvaluateAsync(notifications, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await DisableAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Write(LogLevel.Error, "audio.module.disposeFailed", exception: exception);
        }

        _disposed = true;

        // The catalog, the process monitor, and the options store belong to the composition root,
        // which disposes them after every module has stopped. The transition gate is deliberately
        // left undisposed so a concurrent disable stays a clean no-op rather than throwing.
    }

    // ---------------------------------------------------------------- transitions

    /// <summary>
    /// Runs a transition with the gate held and publishes the status it settled on afterwards.
    /// </summary>
    /// <remarks>
    /// The gate is what keeps one enable, one disable, and one re-evaluation from interleaving. It
    /// is never held while a public event is raised, so a handler is free to call back into the
    /// module, read its state, or be slow without holding anything else up.
    /// </remarks>
    private async Task RunTransitionAsync(
        Func<List<ModuleStatus>, CancellationToken, Task> transition,
        CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        List<ModuleStatus> notifications = [];

        try
        {
            await transition(notifications, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Error, "audio.module.transitionFailed", exception: exception);

            try
            {
                await FaultAsync(
                    AudioModuleErrorCodes.TransitionFailed,
                    "The audio route could not be evaluated. See the log for the reason.",
                    notifications,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                _logger?.Write(LogLevel.Error, "audio.module.transitionRecoveryFailed", exception: cleanupFailure);
            }
        }
        finally
        {
            _transitionGate.Release();
        }

        Publish(notifications);
    }

    private void Publish(IReadOnlyList<ModuleStatus> notifications)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        EventHandler<ModuleStatus>? handler = StatusChanged;

        if (handler is null)
        {
            return;
        }

        foreach (ModuleStatus status in notifications)
        {
            try
            {
                handler(this, status);
            }
            catch (Exception exception)
            {
                // A panel that throws while rendering a status is a panel bug. It must not take the
                // audio path down with it.
                _logger?.Write(LogLevel.Error, "audio.status.handlerFailed", exception: exception);
            }
        }
    }

    /// <summary>
    /// Brings the module in line with the machine. Called while the transition gate is held.
    /// </summary>
    private async Task EvaluateAsync(List<ModuleStatus> notifications, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return;
        }

        if (await ReportPendingStreamFaultAsync(notifications).ConfigureAwait(false))
        {
            return;
        }

        AudioOptions options = await ReadOptionsAsync(cancellationToken).ConfigureAwait(false);

        AudioProfileOptions? profile = FindProfile(options);

        if (profile is null)
        {
            await FaultAsync(
                AudioModuleErrorCodes.MissingProfile,
                "The selected audio profile is not in the configuration.",
                notifications,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        IReadOnlyList<AudioEndpointDescriptor> endpoints = await _deviceCatalog
            .GetEndpointsAsync(cancellationToken)
            .ConfigureAwait(false);

        AudioRouteValidation validation = AudioRouteValidator.Validate(profile, endpoints);

        if (!validation.IsValid)
        {
            await FaultAsync(
                DescribeRouteFailure(validation.ErrorCode, profile, endpoints),
                DescribeRouteFailureMessage(validation.ErrorCode, profile),
                notifications,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        AudioRoute route = validation.Route!;

        await EnsureMonitoringAsync(route.ExecutableName, cancellationToken).ConfigureAwait(false);

        bool targetRunning = _processMonitor.IsRunning;
        SetTargetRunning(targetRunning);

        if (!targetRunning)
        {
            // The route is fine; the game just is not open. Nothing to open a stream for, and
            // nothing for the user to fix.
            await CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);

            Settle(
                notifications,
                new ModuleStatus(
                    ModuleState.Degraded,
                    $"{route.ExecutableName} is not running.",
                    AudioModuleErrorCodes.TargetNotRunning));

            return;
        }

        if (!await EnsureSessionAsync(route, options.Limiter, notifications).ConfigureAwait(false))
        {
            return;
        }

        SetHasUnrelatedSessions(
            await LookForUnrelatedSessionsAsync(route.VirtualRenderEndpointId, cancellationToken).ConfigureAwait(false));

        Settle(notifications, OpenRouteStatus());
    }

    /// <summary>
    /// Reports a fault the stream raised since the last evaluation. Returns whether it did, because
    /// a stream that just failed says nothing about whether the route behind it is still good.
    /// </summary>
    private async Task<bool> ReportPendingStreamFaultAsync(List<ModuleStatus> notifications)
    {
        Exception? fault;
        IAudioStreamSession? faulted;

        lock (_stateGate)
        {
            fault = _streamFault;
            faulted = _faultedSession;
            _streamFault = null;
            _faultedSession = null;
        }

        if (fault is null)
        {
            return false;
        }

        if (!ReferenceEquals(faulted, _session))
        {
            // The stream that failed has already been replaced, so there is nothing to report beyond
            // the log line it wrote when it failed.
            return false;
        }

        _logger?.Write(LogLevel.Error, "audio.stream.reportedFault", exception: fault);

        await FaultAsync(
            AudioModuleErrorCodes.StreamFaulted,
            "The audio stream stopped unexpectedly. Use retry to open it again.",
            notifications,
            CancellationToken.None).ConfigureAwait(false);

        return true;
    }

    // ---------------------------------------------------------------- the stream

    /// <summary>Opens the stream if it is not already open. Returns whether it is open afterwards.</summary>
    private async Task<bool> EnsureSessionAsync(
        AudioRoute route,
        AudioLimiterOptions limiter,
        List<ModuleStatus> notifications)
    {
        IAudioStreamSession? existing = _session;

        if (existing is not null && existing.IsRunning)
        {
            // Already open and the machine still supports it, so it is left alone: reopening would
            // drop the audio the user is listening to in order to arrive at the same place.
            return true;
        }

        if (existing is not null)
        {
            await CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }

        IAudioStreamSession session = _sessionFactory();

        try
        {
            await session.StartAsync(route, limiter, CancellationToken.None).ConfigureAwait(false);

            if (_bypass)
            {
                await session.SetBypassAsync(true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The half-opened session is holding whatever it managed to open, so it is released
            // before the failure is reported.
            await DisposeQuietlyAsync(session).ConfigureAwait(false);

            _logger?.Write(
                LogLevel.Error,
                "audio.stream.openFailed",
                new Dictionary<string, object?>
                {
                    ["virtualCaptureEndpoint"] = route.VirtualCaptureEndpointId,
                    ["physicalRenderEndpoint"] = route.PhysicalRenderEndpointId,
                    ["hresult"] = exception.HResult,
                },
                exception);

            await FaultAsync(
                DescribeOpenFailureCode(exception),
                DescribeOpenFailureMessage(exception),
                notifications,
                CancellationToken.None).ConfigureAwait(false);

            return false;
        }

        session.Faulted += OnSessionFaulted;
        _session = session;
        SetRouteOpen(true);

        return true;
    }

    /// <summary>
    /// Stops and releases the open stream, if there is one. Called while the transition gate is held.
    /// </summary>
    private async Task CloseSessionAsync(CancellationToken cancellationToken)
    {
        IAudioStreamSession? session = _session;
        _session = null;

        SetRouteOpen(false);
        SetHasUnrelatedSessions(false);

        if (session is null)
        {
            return;
        }

        session.Faulted -= OnSessionFaulted;

        lock (_stateGate)
        {
            if (ReferenceEquals(_faultedSession, session))
            {
                _streamFault = null;
                _faultedSession = null;
            }
        }

        try
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "audio.stream.stopFailed", exception: exception);
        }

        await DisposeQuietlyAsync(session).ConfigureAwait(false);
    }

    private async ValueTask DisposeQuietlyAsync(IAudioStreamSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "audio.stream.disposeFailed", exception: exception);
        }
    }

    private void OnSessionFaulted(object? sender, Exception exception)
    {
        if (!_enabled)
        {
            // The session was on its way out when it failed; there is nothing left to do about it.
            return;
        }

        lock (_stateGate)
        {
            _streamFault = exception;
            _faultedSession = sender as IAudioStreamSession;
        }

        Signal();
    }

    // ---------------------------------------------------------------- monitoring

    private async Task EnsureMonitoringAsync(string executableName, CancellationToken cancellationToken)
    {
        if (!ProcessNames.TryNormalize(executableName, out string? normalized))
        {
            // The validator has already refused anything that does not normalize, so this is a
            // guard rather than a path.
            return;
        }

        if (string.Equals(_watchedName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _processMonitor.StartAsync(normalized, cancellationToken).ConfigureAwait(false);
        _watchedName = normalized;
    }

    private async Task StopMonitoringAsync()
    {
        _watchedName = null;

        try
        {
            await _processMonitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "audio.monitor.stopFailed", exception: exception);
        }
    }

    /// <summary>
    /// Looks for anything playing into the virtual render endpoint that is not the routed
    /// application. Their audio goes through the limiter too, so the user is told.
    /// </summary>
    private async Task<bool> LookForUnrelatedSessionsAsync(
        string virtualRenderEndpointId,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlySet<int> sessions = await _deviceCatalog
                .GetActiveProcessIdsAsync(virtualRenderEndpointId, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlySet<int> target = _processMonitor.ProcessIds;

            return sessions.Any(processId => !target.Contains(processId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A warning that cannot be evaluated is not a fault. The session enumeration is the
            // least important thing here and must never be the reason audio stops.
            _logger?.Write(LogLevel.Debug, "audio.sessions.enumerationFailed", exception: exception);
            return false;
        }
    }

    // ---------------------------------------------------------------- the re-evaluation worker

    private void StartWorker()
    {
        Channel<byte> channel = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        CancellationTokenSource cancellation = new();

        _reEvaluationChannel = channel;
        _workerCancellation = cancellation;
        _worker = Task.Run(() => RunWorkerAsync(channel.Reader, cancellation.Token), CancellationToken.None);
    }

    /// <summary>
    /// Cancels the worker and hands back its task to await once the transition gate is free.
    /// </summary>
    private (CancellationTokenSource? Cancellation, Task? Worker) StopWorkerSignalling()
    {
        CancellationTokenSource? cancellation = _workerCancellation;
        Task? worker = _worker;

        _workerCancellation = null;
        _worker = null;
        _reEvaluationChannel = null;

        cancellation?.Cancel();

        return (cancellation, worker);
    }

    private static async Task AwaitWorkerAsync(Task? worker)
    {
        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The expected way for the loop to end.
        }
    }

    private async Task RunWorkerAsync(ChannelReader<byte> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out _))
                {
                    // Drain what is already queued so the debounce covers the whole burst.
                }

                await Task.Delay(_reEvaluationDebounce, _timeProvider, cancellationToken).ConfigureAwait(false);

                while (reader.TryRead(out _))
                {
                    // Anything that arrived during the debounce is part of the same change.
                }

                await RunTransitionAsync(EvaluateAsync, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by disable or dispose.
        }
    }

    /// <summary>Queues a re-evaluation. Safe to call from any thread, including an audio callback.</summary>
    private void Signal() => _reEvaluationChannel?.Writer.TryWrite(0);

    // ---------------------------------------------------------------- configuration

    private async Task<AudioOptions> ReadOptionsAsync(CancellationToken cancellationToken)
    {
        if (_optionsStore is null)
        {
            return _options;
        }

        try
        {
            ToolkitOptions stored = await _optionsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return stored.Audio;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The stored configuration could not be read. The configuration this module was built
            // with is still perfectly usable, and the store has already logged the reason.
            _logger?.Write(LogLevel.Warning, "audio.options.loadFailed", exception: exception);
            return _options;
        }
    }

    private static AudioProfileOptions? FindProfile(AudioOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveProfileId))
        {
            return null;
        }

        foreach (AudioProfileOptions profile in options.Profiles)
        {
            if (string.Equals(profile.Id, options.ActiveProfileId, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Names the part of the route that cannot be used. The validator says a route is unroutable;
    /// what the user needs is which of the three pickers to fix.
    /// </summary>
    private static string DescribeRouteFailure(
        string? validationErrorCode,
        AudioProfileOptions profile,
        IReadOnlyList<AudioEndpointDescriptor> endpoints)
    {
        switch (validationErrorCode)
        {
            case AudioRouteErrorCodes.InvalidExecutableName:
            case AudioRouteErrorCodes.SameRenderEndpoint:
                return AudioModuleErrorCodes.InvalidRoute;

            case AudioRouteErrorCodes.UnsupportedChannelCount:
            case AudioRouteErrorCodes.UnsupportedSampleRate:
                return AudioModuleErrorCodes.UnsupportedFormat;

            default:
                return FirstUnusableEndpointCode(profile, endpoints) ?? AudioModuleErrorCodes.InvalidRoute;
        }
    }

    private static string? FirstUnusableEndpointCode(
        AudioProfileOptions profile,
        IReadOnlyList<AudioEndpointDescriptor> endpoints)
    {
        if (IsUnusable(profile.VirtualRenderEndpointId, AudioDataFlow.Render, endpoints))
        {
            return AudioModuleErrorCodes.MissingVirtualRender;
        }

        if (IsUnusable(profile.VirtualCaptureEndpointId, AudioDataFlow.Capture, endpoints))
        {
            return AudioModuleErrorCodes.MissingVirtualCapture;
        }

        if (IsUnusable(profile.PhysicalRenderEndpointId, AudioDataFlow.Render, endpoints))
        {
            return AudioModuleErrorCodes.MissingPhysicalRender;
        }

        return null;
    }

    private static bool IsUnusable(
        string? endpointId,
        AudioDataFlow expectedFlow,
        IReadOnlyList<AudioEndpointDescriptor> endpoints)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return true;
        }

        foreach (AudioEndpointDescriptor endpoint in endpoints)
        {
            if (string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                // The right device in the wrong direction is still the wrong device: it is the
                // picker that has to change, which is what the code tells the panel.
                return endpoint.Flow != expectedFlow || !endpoint.IsActive;
            }
        }

        return true;
    }

    private static string DescribeRouteFailureMessage(string? validationErrorCode, AudioProfileOptions profile)
    {
        string name = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;

        return validationErrorCode switch
        {
            AudioRouteErrorCodes.InvalidExecutableName =>
                $"The executable name for '{name}' is not a plain file name.",

            AudioRouteErrorCodes.SameRenderEndpoint =>
                $"The virtual output and the playback endpoint for '{name}' are the same device, which would loop.",

            AudioRouteErrorCodes.UnsupportedChannelCount or AudioRouteErrorCodes.UnsupportedSampleRate =>
                $"An endpoint for '{name}' is not a stereo format at 44.1 or 48 kHz, which is what this release carries.",

            AudioRouteErrorCodes.WrongDataFlow =>
                $"One of the endpoints for '{name}' is the wrong kind: a playback endpoint was chosen where a recording endpoint is needed, or the reverse. The toolkit does not install or configure the virtual cable.",

            _ =>
                $"One of the endpoints for '{name}' is not available. The toolkit does not install or configure the virtual cable.",
        };
    }

    /// <summary>
    /// A format the stream refused is a format the route validator approved, which means the
    /// endpoint's mix format changed between being enumerated and being opened.
    /// </summary>
    private static string DescribeOpenFailureCode(Exception exception) => exception switch
    {
        ArgumentException or NotSupportedException => AudioModuleErrorCodes.UnsupportedFormat,
        _ => AudioModuleErrorCodes.SharedModeOpenFailed,
    };

    private static string DescribeOpenFailureMessage(Exception exception) => exception switch
    {
        ArgumentException or NotSupportedException =>
            "An endpoint's format changed to one the toolkit does not carry. Set it to a stereo 44.1 or 48 kHz format.",

        _ =>
            "The audio endpoints could not be opened in shared mode. Another application may be holding them, or the device may have been removed.",
    };

    // ---------------------------------------------------------------- status plumbing

    /// <summary>
    /// Records progress that is not worth announcing. The transition settles on a status the user
    /// can act on, and that is the one the panel hears about.
    /// </summary>
    private void SetProgress(ModuleStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }
    }

    /// <summary>
    /// Sets the status the transition settled on and queues it for publication, which happens once
    /// the transition gate has been released.
    /// </summary>
    private void Settle(List<ModuleStatus> notifications, ModuleStatus status)
    {
        SetProgress(status);
        notifications.Add(status);
    }

    private ModuleStatus OpenRouteStatus() => _bypass
        ? new ModuleStatus(ModuleState.Bypass, "The limiter is off; audio is passing through unchanged.")
        : new ModuleStatus(ModuleState.Active, "The limiter is processing the routed application's audio.");

    private async Task FaultAsync(
        string errorCode,
        string message,
        List<ModuleStatus> notifications,
        CancellationToken cancellationToken)
    {
        await CloseSessionAsync(cancellationToken).ConfigureAwait(false);

        // Reported from the monitor rather than assumed: a stream can fail while the game is
        // perfectly healthy, and the panel's target indicator should not flicker off because of it.
        SetTargetRunning(_processMonitor.IsRunning);

        Settle(notifications, new ModuleStatus(ModuleState.Faulted, message, errorCode));
    }

    private void SetTargetRunning(bool value)
    {
        lock (_stateGate)
        {
            _targetRunning = value;
        }
    }

    private void SetRouteOpen(bool value)
    {
        lock (_stateGate)
        {
            _routeOpen = value;
        }
    }

    private void SetHasUnrelatedSessions(bool value)
    {
        lock (_stateGate)
        {
            _hasUnrelatedSessions = value;
        }
    }

    /// <summary>
    /// Builds the warning shown next to the status, if there is one. Computed rather than stored so
    /// that a change in the stream's own metrics is visible without another transition.
    /// </summary>
    private static (string? Code, string? Message) DescribeWarning(
        bool routeOpen,
        bool unrelatedSessions,
        AudioStreamMetrics? metrics)
    {
        if (!routeOpen)
        {
            return (null, null);
        }

        if (unrelatedSessions)
        {
            return (
                AudioModuleWarningCodes.UnrelatedSessions,
                "Another application is also playing into the routed output, so its audio is being limited too.");
        }

        if (metrics is { } current &&
            current.EstimatedAdditionalLatencyMilliseconds > LatencyWarningThresholdMilliseconds)
        {
            // Formatted with the invariant culture so the number in the log reads the same on a
            // machine whose decimal separator is a comma.
            string measured = current.EstimatedAdditionalLatencyMilliseconds
                .ToString("0.#", CultureInfo.InvariantCulture);

            return (
                AudioModuleWarningCodes.LatencyTargetExceeded,
                $"The toolkit's buffers are adding about {measured} ms of delay to the audio.");
        }

        return (null, null);
    }

    private void Subscribe()
    {
        _deviceCatalog.DevicesChanged += OnMachineChanged;
        _processMonitor.Changed += OnMachineChanged;
    }

    private void Unsubscribe()
    {
        _deviceCatalog.DevicesChanged -= OnMachineChanged;
        _processMonitor.Changed -= OnMachineChanged;
    }

    private void OnMachineChanged(object? sender, EventArgs e) => Signal();

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioModule));
        }
    }
}
