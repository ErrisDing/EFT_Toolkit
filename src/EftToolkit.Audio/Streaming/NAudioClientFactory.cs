using EftToolkit.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// Opens capture and render clients on Windows audio endpoints.
/// </summary>
/// <remarks>
/// Endpoints are resolved by the ID the user configured, never by index or by position in the
/// enumeration: the order Windows reports devices in is not stable across restarts, and a route that
/// silently pointed at a different device would be worse than one that failed to open.
/// </remarks>
internal sealed class NAudioClientFactory : INAudioClientFactory
{
    private readonly IAppLogger? _logger;

    public NAudioClientFactory(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public ISharedCaptureClient CreateCapture(string endpointId) =>
        new NAudioCaptureClient(OpenDevice(endpointId, DataFlow.Capture));

    public ISharedRenderClient CreateRender(string endpointId, int latencyMilliseconds) =>
        new NAudioRenderClient(OpenDevice(endpointId, DataFlow.Render), latencyMilliseconds, _logger);

    /// <summary>
    /// Finds one active endpoint by ID and hands ownership of it to the caller. An endpoint that is
    /// not active is not opened: a shared-mode stream against a disabled device fails at
    /// initialisation with an error that says far less than this one does.
    /// </summary>
    private static MMDevice OpenDevice(string endpointId, DataFlow flow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        using MMDeviceEnumerator enumerator = new();
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);

        for (int index = 0; index < devices.Count; index++)
        {
            MMDevice device = devices[index];

            if (string.Equals(device.ID, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }

            device.Dispose();
        }

        throw new InvalidOperationException(
            $"No active {flow} endpoint has the ID \"{endpointId}\". The device may have been removed, " +
            "disabled, or reconfigured since the route was set up.");
    }

    /// <summary>
    /// A capture endpoint opened in shared mode, with its events forwarded as plain callbacks.
    /// </summary>
    private sealed class NAudioCaptureClient : ISharedCaptureClient
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;

        internal NAudioCaptureClient(MMDevice device)
        {
            _device = device;

            _capture = new WasapiCapture(
                device,
                useEventSync: true,
                audioBufferMillisecondsLength: NAudioSharedStreamSession.CaptureBufferMilliseconds);

            // WasapiCapture can be switched to exclusive mode after construction. It is set here
            // rather than left to the default so the guarantee does not rest on a default value.
            _capture.ShareMode = AudioClientShareMode.Shared;

            BufferMilliseconds = NAudioSharedStreamSession.CaptureBufferMilliseconds;
            WaveFormat = _capture.WaveFormat;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public WaveFormat WaveFormat { get; }

        public int BufferMilliseconds { get; }

        public event EventHandler<CapturedAudioEventArgs>? DataAvailable;

        public event EventHandler<Exception>? Faulted;

        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.Run(_capture.StartRecording, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.Run(_capture.StopRecording, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            _capture.Dispose();
            _device.Dispose();

            return ValueTask.CompletedTask;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e) =>
            DataAvailable?.Invoke(this, new CapturedAudioEventArgs(e.Buffer, e.BytesRecorded));

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            // A stop the session asked for carries no exception, so silence here is the normal case.
            if (e.Exception is { } exception)
            {
                Faulted?.Invoke(this, exception);
            }
        }
    }

    /// <summary>A render endpoint opened in shared mode.</summary>
    private sealed class NAudioRenderClient : ISharedRenderClient
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;

        internal NAudioRenderClient(MMDevice device, int latencyMilliseconds, IAppLogger? logger)
        {
            _device = device;

            BufferMilliseconds = latencyMilliseconds;
            MixFormat = ReadMixFormat(device, logger);

            _output = new WasapiOut(
                device,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: latencyMilliseconds);

            _output.PlaybackStopped += OnPlaybackStopped;
        }

        public WaveFormat MixFormat { get; }

        public int BufferMilliseconds { get; }

        public event EventHandler<Exception>? Faulted;

        public Task StartAsync(
            IWaveProvider provider,
            AudioClientShareMode shareMode,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (shareMode != AudioClientShareMode.Shared)
            {
                // Belt and braces over the constructor above, which already fixes shared mode. This
                // is the one call that could open an endpoint in exclusive mode, so it is the one
                // place that refuses to.
                throw new NotSupportedException(
                    "Only WASAPI shared mode is used; exclusive mode would take the endpoint away " +
                    "from the game and from every other application.");
            }

            return Task.Run(
                () =>
                {
                    _output.Init(provider);
                    _output.Play();
                },
                cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.Run(_output.Stop, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _output.PlaybackStopped -= OnPlaybackStopped;

            _output.Dispose();
            _device.Dispose();

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// The format the device will accept in shared mode. NAudio negotiates around an
        /// unsupported source format itself, but the session needs to know what it is negotiating
        /// towards in order to decide whether a resampler is needed.
        /// </summary>
        private static WaveFormat ReadMixFormat(MMDevice device, IAppLogger? logger)
        {
            try
            {
                using AudioClient client = device.AudioClient;
                return client.MixFormat;
            }
            catch (Exception exception)
            {
                logger?.Write(LogLevel.Debug, "audio.stream.mixFormatUnavailable", exception: exception);

                // Reported as a zero-sample-rate format, which start-up refuses. Guessing a format
                // here would produce a stream that plays at the wrong speed.
                return new WaveFormat(0, 0);
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is { } exception)
            {
                Faulted?.Invoke(this, exception);
            }
        }
    }
}
