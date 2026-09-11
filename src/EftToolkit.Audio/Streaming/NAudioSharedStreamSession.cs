using System.Buffers;
using EftToolkit.Audio.Dsp;
using EftToolkit.Audio.Routing;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// One routed audio path, opened entirely in WASAPI shared mode: capture the virtual endpoint,
/// convert to interleaved floats, limit, buffer, and render to the physical endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Shared mode is not a preference here, it is the design. Exclusive mode would take the endpoint
/// away from the game, from Discord, and from everything else the user has open, and it would fail
/// outright whenever anything else was already using the device. Nothing in this class can reach
/// exclusive mode: the share mode is named once, at the call that opens the render side.
/// </para>
/// <para>
/// The stop path is ordered rather than merely complete. Capture stops first so no new audio is
/// produced, the ring is then faded out so the last thing the user hears is a ramp rather than a
/// click, and only then does the render side stop. Each step is bounded, because a device driver
/// that has wedged is a thing that happens and it must not take the application down with it.
/// </para>
/// </remarks>
public sealed class NAudioSharedStreamSession : IAudioStreamSession
{
    /// <summary>Long enough to remove the click, short enough to be inaudible as a fade.</summary>
    public static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(10);

    /// <summary>How long any one teardown step may take before it is abandoned and logged.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The render buffer the physical endpoint is opened with, in milliseconds.</summary>
    public const int RenderLatencyMilliseconds = 20;

    /// <summary>
    /// The capture buffer the virtual endpoint is opened with, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Kept equal to the render latency rather than at the 100 ms a capture stream would normally
    /// ask for. These buffers are the toolkit's own contribution to the delay between the game
    /// making a sound and the user hearing it, and 100 ms of it is audible as a lag that the user
    /// cannot attribute to anything. The cost is a smaller margin against the capture thread being
    /// starved, which shows up as an underrun — a dropout — rather than as latency, and underruns
    /// are counted and reported.
    /// </remarks>
    public const int CaptureBufferMilliseconds = 20;

    /// <summary>Media Foundation resampler quality, 1 to 60.</summary>
    private const int ResamplerQualitySetting = 60;

    /// <summary>What the resampler is assumed to add, for the latency estimate only.</summary>
    private const double ResamplerLatencyMilliseconds = 10.0;

    private readonly INAudioClientFactory _factory;
    private readonly IAppLogger? _logger;
    private readonly Func<IWaveProvider, WaveFormat, IWaveProvider> _formatConverter;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private ISharedCaptureClient? _capture;
    private ISharedRenderClient? _render;
    private ProcessedSampleProvider? _provider;
    private IWaveProvider? _renderSource;
    private StereoLinkedLimiter? _limiter;
    private WaveFormat? _captureFormat;
    private float[]? _scratch;

    private volatile bool _running;
    private volatile bool _bypass;
    private bool _converted;
    private bool _disposed;

    /// <summary>Zero until a fault has been reported, so the first one wins and the rest are dropped.</summary>
    private int _faulted;

    internal NAudioSharedStreamSession(
        INAudioClientFactory factory,
        IAppLogger? logger = null,
        Func<IWaveProvider, WaveFormat, IWaveProvider>? formatConverter = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
        _formatConverter = formatConverter ?? CreateResampler;
    }

    public bool IsRunning => _running;

    public event EventHandler<Exception>? Faulted;

    public AudioStreamMetrics Metrics
    {
        get
        {
            ProcessedSampleProvider? provider = _provider;
            StereoLinkedLimiter? limiter = _limiter;

            if (!_running || provider is null || limiter is null)
            {
                return AudioStreamMetrics.Idle;
            }

            return new AudioStreamMetrics(
                provider.Underruns,
                provider.Overruns,
                _capture?.BufferMilliseconds ?? 0,
                _render?.BufferMilliseconds ?? 0,
                EstimatedAdditionalLatencyMilliseconds(),
                limiter.Metrics);
        }
    }

    public async Task StartAsync(AudioRoute route, AudioLimiterOptions limiter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(limiter);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                throw new InvalidOperationException("This session is already streaming.");
            }

            try
            {
                // The fields are published as each piece is opened rather than at the end, so the
                // recovery path below can find and release anything that exists no matter where the
                // failure landed.
                _capture = _factory.CreateCapture(route.VirtualCaptureEndpointId);
                _captureFormat = _capture.WaveFormat;

                _render = _factory.CreateRender(route.PhysicalRenderEndpointId, RenderLatencyMilliseconds);

                int sampleRate = RequireUsableFormat(_capture.WaveFormat, "capture");
                _ = RequireUsableFormat(_render.MixFormat, "render");

                _limiter = new StereoLinkedLimiter(sampleRate, limiter);
                _provider = new ProcessedSampleProvider(sampleRate);

                // Only a rate change needs the resampler. An encoding change is left to NAudio, which
                // negotiates it against the device's format when the render side is initialised.
                _converted = _capture.WaveFormat.SampleRate != _render.MixFormat.SampleRate;
                _renderSource = _converted ? _formatConverter(_provider, _render.MixFormat) : _provider;

                _scratch = ArrayPool<float>.Shared.Rent(ScratchSamplesFor(sampleRate, _capture.BufferMilliseconds));

                _capture.DataAvailable += OnDataAvailable;
                _capture.Faulted += OnCaptureFaulted;
                _render.Faulted += OnRenderFaulted;

                Interlocked.Exchange(ref _faulted, 0);
                _bypass = false;

                // The render side is started first so the ring is already being drained when the
                // first captured block arrives. Starting capture first would drop that block as an
                // overrun, which is a click at the start of every session.
                await _render.StartAsync(_renderSource, AudioClientShareMode.Shared, cancellationToken)
                    .ConfigureAwait(false);

                _running = true;

                await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Nothing is streaming, so there is no fade to run: release whatever was opened and
                // let the caller see why.
                StreamResources abandoned = Detach();
                await ReleaseInOrderAsync(abandoned).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task SetBypassAsync(bool bypass, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bypass == bypass)
        {
            return Task.CompletedTask;
        }

        _bypass = bypass;

        if (!bypass)
        {
            // The envelope measured a signal that was never processed, so it is not a sensible
            // starting point for the one that is about to be.
            _limiter?.Reset();
        }

        _logger?.Write(
            LogLevel.Information,
            bypass ? "audio.stream.bypassed" : "audio.stream.engaged");

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A caller may abandon the stop before it starts, but once it has started it runs to
        // completion: a half-released stream holds endpoints the next start needs.
        cancellationToken.ThrowIfCancellationRequested();

        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            StreamResources resources = Detach();

            if (resources.IsEmpty)
            {
                return;
            }

            await StopCaptureAsync(resources).ConfigureAwait(false);

            await FadeOutAsync(resources).ConfigureAwait(false);

            await StopRenderAsync(resources).ConfigureAwait(false);

            await ReleaseInOrderAsync(resources).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            StreamResources resources = Detach();
            await ReleaseInOrderAsync(resources).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    // ---------------------------------------------------------------- capture path

    /// <summary>
    /// Runs on the capture thread, once per buffer, for as long as the session is streaming. It
    /// allocates nothing in the steady state: the scratch buffer was rented at start, the limiter
    /// owns its own delay line, and the ring was allocated with the provider.
    /// </summary>
    private void OnDataAvailable(object? sender, CapturedAudioEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        try
        {
            float[]? scratch = _scratch;
            ProcessedSampleProvider? provider = _provider;
            StereoLinkedLimiter? limiter = _limiter;
            WaveFormat? format = _captureFormat;

            if (scratch is null || provider is null || limiter is null || format is null)
            {
                return;
            }

            int bytes = Math.Min(e.BytesRecorded, e.Buffer.Length);
            int frames = PcmFloatConverter.FrameCountFor(bytes, format);

            if (frames == 0)
            {
                return;
            }

            int needed = frames * PcmFloatConverter.ChannelCount;

            // The rented buffer covers the size the endpoint was opened with. A device is within its
            // rights to hand over more than that, and dropping that audio silently would be worse
            // than one allocation.
            float[]? oversized = needed > scratch.Length ? new float[needed] : null;
            Span<float> destination = oversized ?? scratch;

            int written = PcmFloatConverter.ConvertToFloat(
                e.Buffer.Span[..bytes],
                format,
                destination[..needed]);

            if (written == 0)
            {
                return;
            }

            Span<float> ready = destination[..written];

            if (_bypass)
            {
                Sanitize(ready);
            }
            else
            {
                limiter.Process(ready);
            }

            provider.Enqueue(ready);
        }
        catch (Exception exception)
        {
            // This runs inside a COM callback. Anything that escapes it is lost, so everything is
            // turned into a fault the module above can act on.
            RaiseFault(exception);
        }
    }

    private void OnCaptureFaulted(object? sender, Exception exception) => RaiseFault(exception);

    private void OnRenderFaulted(object? sender, Exception exception) => RaiseFault(exception);

    /// <summary>
    /// Reports the first failure and ignores the rest. A device that has gone away tends to raise
    /// several, and the module above only needs to know it happened once.
    /// </summary>
    private void RaiseFault(Exception exception)
    {
        if (!_running || Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _logger?.Write(LogLevel.Error, "audio.stream.faulted", null, exception);

        try
        {
            Faulted?.Invoke(this, exception);
        }
        catch (Exception handlerFailure)
        {
            _logger?.Write(LogLevel.Warning, "audio.stream.faultHandlerFailed", null, handlerFailure);
        }
    }

    private static void Sanitize(Span<float> samples)
    {
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = AudioMath.Sanitize(samples[index]);
        }
    }

    // ---------------------------------------------------------------- teardown

    private Task StopCaptureAsync(StreamResources resources) =>
        resources.Capture is { } capture
            ? RunTeardownStepAsync(
                "capture",
                "audio.stream.stopTimedOut",
                () => capture.StopAsync(CancellationToken.None))
            : Task.CompletedTask;

    private Task StopRenderAsync(StreamResources resources) =>
        resources.Render is { } render
            ? RunTeardownStepAsync(
                "render",
                "audio.stream.stopTimedOut",
                () => render.StopAsync(CancellationToken.None))
            : Task.CompletedTask;

    /// <summary>
    /// Ramps the ring's output to zero and waits for the ramp to have been played. Without this the
    /// last sample before silence is a step, which is audible as a click at every stop.
    /// </summary>
    private static async Task FadeOutAsync(StreamResources resources)
    {
        if (resources.Provider is not { } provider)
        {
            return;
        }

        provider.BeginFadeOut(FadeOutDuration);

        await Task.Delay(FadeOutDuration).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases capture, the resampler, the ring, and the render side, in that order: each one holds
    /// a reference to the next.
    /// </summary>
    private async Task ReleaseInOrderAsync(StreamResources resources)
    {
        await DisposeClientAsync(resources.Capture, "capture").ConfigureAwait(false);

        if (resources.Converter is { } converter)
        {
            // A resampler is a Media Foundation transform, so it is COM underneath and can block.
            // Running it off the current thread is what makes the timeout below mean anything.
            await RunTeardownStepAsync(
                "converter",
                "audio.stream.disposeTimedOut",
                () => Task.Run(converter.Dispose, CancellationToken.None)).ConfigureAwait(false);
        }

        if (resources.Provider is { } provider)
        {
            await RunTeardownStepAsync(
                "provider",
                "audio.stream.disposeTimedOut",
                () => Task.Run(provider.Dispose, CancellationToken.None)).ConfigureAwait(false);
        }

        await DisposeClientAsync(resources.Render, "render").ConfigureAwait(false);

        if (resources.Scratch is { } scratch)
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    private Task DisposeClientAsync(IAsyncDisposable? resource, string component) =>
        resource is null
            ? Task.CompletedTask
            : RunTeardownStepAsync(
                component,
                "audio.stream.disposeTimedOut",
                () => resource.DisposeAsync().AsTask());

    /// <summary>
    /// Runs one teardown step under a bound. A step that throws, or that has not finished within
    /// <see cref="StopTimeout"/>, is logged and abandoned: the remaining steps still have to run,
    /// and the user's application still has to close.
    /// </summary>
    private async Task RunTeardownStepAsync(string component, string timeoutEventName, Func<Task> step)
    {
        Task task;

        try
        {
            task = step();
        }
        catch (Exception exception)
        {
            LogStepFailure(component, exception);
            return;
        }

        Task finished = await Task.WhenAny(task, Task.Delay(StopTimeout)).ConfigureAwait(false);

        if (finished != task)
        {
            // Nothing is going to observe this task now, so its exception has to be read here or it
            // surfaces as an unobserved task exception at collection time.
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _logger?.Write(
                LogLevel.Warning,
                timeoutEventName,
                new Dictionary<string, object?> { ["component"] = component });

            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogStepFailure(component, exception);
        }
    }

    private void LogStepFailure(string component, Exception exception) =>
        _logger?.Write(
            LogLevel.Warning,
            "audio.stream.teardownFailed",
            new Dictionary<string, object?> { ["component"] = component },
            exception);

    /// <summary>
    /// Takes the running session's pieces out of their fields and detaches every subscription. The
    /// fields are cleared first, so the capture thread stops seeing a session it could still write
    /// into, and so a second call finds nothing to do.
    /// </summary>
    private StreamResources Detach()
    {
        StreamResources resources = new(
            _capture,
            _render,
            _provider,
            _renderSource,
            _scratch);

        _running = false;
        _capture = null;
        _render = null;
        _provider = null;
        _limiter = null;
        _renderSource = null;
        _captureFormat = null;
        _scratch = null;

        if (resources.Capture is { } capture)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.Faulted -= OnCaptureFaulted;
        }

        if (resources.Render is { } render)
        {
            render.Faulted -= OnRenderFaulted;
        }

        return resources;
    }

    // ---------------------------------------------------------------- formats and reporting

    /// <summary>
    /// Refuses anything the toolkit cannot process faithfully, and returns the validated rate.
    /// Stereo is a hard requirement because the limiter is stereo-linked; a mono or surround stream
    /// would have to be downmixed or panned, and the toolkit does not do either.
    /// </summary>
    private static int RequireUsableFormat(WaveFormat format, string side)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (format.Channels != PcmFloatConverter.ChannelCount)
        {
            throw new ArgumentException(
                $"The {side} endpoint is {format.Channels}-channel; only stereo is streamed.",
                nameof(format));
        }

        // The same set the panel validates against, so a rate offered there is one this session
        // opens. A second copy of the list here is a rate the user can select and never enable.
        if (!AudioRouteValidator.SupportedSampleRates.Contains(format.SampleRate))
        {
            throw new NotSupportedException(
                $"The {side} endpoint runs at {format.SampleRate} Hz; "
                + $"only {AudioRouteValidator.SupportedSampleRateList} are streamed.");
        }

        return format.SampleRate;
    }

    /// <summary>
    /// The scratch buffer is sized for one capture buffer's worth of frames and no more. The frame
    /// count is the endpoint's own buffer length, so the common case never needs a larger one.
    /// </summary>
    private static int ScratchSamplesFor(int sampleRate, int bufferMilliseconds)
    {
        int frames = Math.Max(sampleRate / 10, sampleRate * Math.Max(bufferMilliseconds, 0) / 1000);

        return frames * PcmFloatConverter.ChannelCount;
    }

    private double EstimatedAdditionalLatencyMilliseconds()
    {
        double lookAhead = _limiter is { } limiter && limiter.LookAheadFrames > 0 && _captureFormat is { } format
            ? limiter.LookAheadFrames * 1000.0 / format.SampleRate
            : 0.0;

        return (_capture?.BufferMilliseconds ?? 0)
            + (_render?.BufferMilliseconds ?? 0)
            + lookAhead
            + (_converted ? ResamplerLatencyMilliseconds : 0.0);
    }

    /// <summary>
    /// The default conversion for a rate mismatch. Only used when the two endpoints disagree, which
    /// is not the usual case: usually the virtual cable is set to the same rate as the device.
    /// </summary>
    private static IWaveProvider CreateResampler(IWaveProvider source, WaveFormat targetFormat) =>
        new MediaFoundationResampler(source, targetFormat) { ResamplerQuality = ResamplerQualitySetting };

    /// <summary>
    /// Everything one started session owns. Passed around teardown as a unit so that partially
    /// built sessions are cleaned up by the same code as complete ones.
    /// </summary>
    private readonly record struct StreamResources(
        ISharedCaptureClient? Capture,
        ISharedRenderClient? Render,
        ProcessedSampleProvider? Provider,
        IWaveProvider? RenderSource,
        float[]? Scratch)
    {
        internal bool IsEmpty => Capture is null && Render is null && Provider is null;

        /// <summary>The resampler, when there is one. The provider itself is disposed separately.</summary>
        internal IDisposable? Converter =>
            RenderSource is not null && !ReferenceEquals(RenderSource, Provider)
                ? RenderSource as IDisposable
                : null;
    }
}
