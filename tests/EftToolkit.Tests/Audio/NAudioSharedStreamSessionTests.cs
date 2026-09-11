using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Tests.TestSupport;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EftToolkit.Tests.Audio;

/// <summary>
/// Exercises the stream session against recording clients rather than hardware. Opening a real
/// endpoint here would take the machine's speakers away from whoever is running the tests, and the
/// questions these tests ask are about ordering, disposal, and refusal, none of which need a device.
/// </summary>
public class NAudioSharedStreamSessionTests
{
    private static readonly WaveFormat Stereo48Float = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    private const string VirtualCaptureId = "virtual-capture";
    private const string PhysicalRenderId = "headphones";

    private readonly FakeClientFactory _factory = new();
    private readonly LifecycleLog _log = new();

    public NAudioSharedStreamSessionTests() => _factory.UseLog(_log);

    private NAudioSharedStreamSession CreateSession() => new(_factory, logger: null, CreateConverter);

    private IWaveProvider CreateConverter(IWaveProvider source, WaveFormat target) =>
        new FakeFormatConverter(source, target, _log);

    private static AudioRoute Route() =>
        new("virtual-render", VirtualCaptureId, PhysicalRenderId, "EscapeFromTarkov");

    private static AudioLimiterOptions Limiter() =>
        new(InputGainDb: 0, ThresholdDbFs: -6, Ratio: 4, KneeDb: 0, LookAheadMs: 0, AttackMs: 0, ReleaseMs: 50, CeilingDbFs: -1);

    // ---------------------------------------------------------------- starting

    [Fact]
    public async Task Start_opens_exactly_one_capture_and_one_render_client()
    {
        await using NAudioSharedStreamSession session = CreateSession();

        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.True(session.IsRunning);
        Assert.Single(_factory.Captures);
        Assert.Single(_factory.Renders);
    }

    [Fact]
    public async Task Both_clients_are_opened_in_shared_mode()
    {
        // Exclusive mode would take the endpoint away from the game and from every other
        // application on the machine. There is no fallback to it and this is what says so.
        await using NAudioSharedStreamSession session = CreateSession();

        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.Equal(AudioClientShareMode.Shared, _factory.Renders[0].ShareMode);
        Assert.Equal(VirtualCaptureId, _factory.Captures[0].EndpointId);
        Assert.Equal(PhysicalRenderId, _factory.Renders[0].EndpointId);
    }

    [Fact]
    public async Task Start_asks_for_the_configured_render_latency()
    {
        await using NAudioSharedStreamSession session = CreateSession();

        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.Equal(20, _factory.Renders[0].RequestedLatencyMilliseconds);
    }

    [Fact]
    public async Task Start_does_not_begin_sampling_before_both_sides_are_ready()
    {
        // A capture that starts first would push audio into a ring nothing is draining, and the
        // first buffer would be dropped as an overrun before the user heard anything.
        await using NAudioSharedStreamSession session = CreateSession();

        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.True(_factory.Renders[0].IsStarted);
        Assert.True(_factory.Captures[0].IsStarted);
        Assert.Equal(["render.start", "capture.start"], _log.Entries.Take(2));
    }

    [Fact]
    public async Task A_duplicate_start_is_rejected()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAsync(Route(), Limiter(), CancellationToken.None));

        // The rejected call must not have opened a second pair behind the first.
        Assert.Single(_factory.Captures);
        Assert.Single(_factory.Renders);
    }

    // ---------------------------------------------------------------- refusing unusable formats

    [Fact]
    public async Task A_non_stereo_capture_format_is_refused_and_both_sides_are_closed()
    {
        _factory.CaptureFormat = new WaveFormat(48_000, 16, 1);

        await using NAudioSharedStreamSession session = CreateSession();

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.StartAsync(Route(), Limiter(), CancellationToken.None));

        Assert.False(session.IsRunning);
        Assert.All(_factory.Captures, client => Assert.True(client.IsDisposed));
        Assert.All(_factory.Renders, client => Assert.True(client.IsDisposed));
    }

    [Fact]
    public async Task A_non_stereo_render_format_is_refused_and_both_sides_are_closed()
    {
        _factory.RenderFormat = new WaveFormat(48_000, 16, 6);

        await using NAudioSharedStreamSession session = CreateSession();

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.StartAsync(Route(), Limiter(), CancellationToken.None));

        Assert.False(session.IsRunning);
        Assert.All(_factory.Renders, client => Assert.True(client.IsDisposed));
        Assert.All(_factory.Captures, client => Assert.True(client.IsDisposed));
    }

    [Fact]
    public async Task A_route_running_at_96_kHz_is_streamed()
    {
        // The check that refuses a rate the limiter cannot be sized for, and the one the panel runs
        // before enabling the route, have to name the same rates. This is the session half of that
        // pair: a rate the panel offered and the session then refused would leave the user with an
        // enable button that always fails.
        WaveFormat stereo96Float = WaveFormat.CreateIeeeFloatWaveFormat(96_000, 2);
        _factory.CaptureFormat = stereo96Float;
        _factory.RenderFormat = stereo96Float;

        await using NAudioSharedStreamSession session = CreateSession();

        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.True(session.IsRunning);
    }

    [Theory]
    [InlineData(22_050)]
    [InlineData(32_000)]
    [InlineData(88_200)]
    [InlineData(192_000)]
    public async Task A_sample_rate_outside_the_supported_set_is_refused(int sampleRate)
    {
        _factory.CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

        await using NAudioSharedStreamSession session = CreateSession();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => session.StartAsync(Route(), Limiter(), CancellationToken.None));

        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task A_failure_opening_the_render_client_closes_the_capture_client()
    {
        _factory.RenderCreateFailure = new InvalidOperationException("no such endpoint");

        await using NAudioSharedStreamSession session = CreateSession();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAsync(Route(), Limiter(), CancellationToken.None));

        Assert.False(session.IsRunning);
        Assert.All(_factory.Captures, client => Assert.True(client.IsDisposed));
    }

    // ---------------------------------------------------------------- audio through the session

    [Fact]
    public async Task Capture_audio_reaches_the_render_source()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        byte[] captured = Float32Bytes(0.25f, -0.25f, 0.5f, -0.5f);
        _factory.Captures[0].Deliver(captured);

        float[] played = ReadFloats(_factory.Renders[0].Source!, 4);

        // Unity gain and a threshold of -6 dBFS, so the quiet frames come through untouched.
        Assert.Equal([0.25f, -0.25f, 0.5f, -0.5f], played);
    }

    [Fact]
    public async Task Pcm_capture_audio_is_converted_before_it_reaches_the_render_source()
    {
        _factory.CaptureFormat = new WaveFormat(48_000, 16, 2);

        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        // (16384, -16384) as 16-bit PCM, which is half scale.
        _factory.Captures[0].Deliver([0x00, 0x40, 0x00, 0xC0]);

        float[] played = ReadFloats(_factory.Renders[0].Source!, 2);

        Assert.Equal([0.5f, -0.5f], played);
    }

    [Fact]
    public async Task Bypass_forwards_audio_without_limiting_it()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        await session.SetBypassAsync(bypass: true, CancellationToken.None);

        // Far above the threshold, so the limiter would have pulled this down by a long way.
        _factory.Captures[0].Deliver(Float32Bytes(0.99f, 0.99f, 0.99f, 0.99f));

        float[] played = ReadFloats(_factory.Renders[0].Source!, 4);

        Assert.All(played, sample => Assert.Equal(0.99f, sample));
    }

    [Fact]
    public async Task Leaving_bypass_returns_the_audio_to_the_limiter()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        await session.SetBypassAsync(bypass: true, CancellationToken.None);
        await session.SetBypassAsync(bypass: false, CancellationToken.None);

        _factory.Captures[0].Deliver(Float32Bytes(0.99f, 0.99f));

        float[] played = ReadFloats(_factory.Renders[0].Source!, 2);

        Assert.True(Math.Abs(played[0]) < 0.99f, $"expected the limiter to act, got {played[0]}");
    }

    [Fact]
    public async Task Bypass_still_removes_samples_the_limiter_could_not_use()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);
        await session.SetBypassAsync(bypass: true, CancellationToken.None);

        _factory.Captures[0].Deliver(Float32Bytes(float.NaN, 0.5f, float.PositiveInfinity, -0.5f));

        float[] played = ReadFloats(_factory.Renders[0].Source!, 4);

        Assert.Equal([0.0f, 0.5f, 0.0f, -0.5f], played);
    }

    // ---------------------------------------------------------------- faults

    [Fact]
    public async Task A_capture_fault_is_reported_once()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        List<Exception> faults = [];
        session.Faulted += (_, exception) => faults.Add(exception);

        _factory.Captures[0].Fail(new InvalidOperationException("the cable was unplugged"));
        _factory.Captures[0].Fail(new InvalidOperationException("and again"));

        Assert.Single(faults);
        Assert.Equal("the cable was unplugged", faults[0].Message);
    }

    [Fact]
    public async Task A_render_fault_is_reported_once()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        List<Exception> faults = [];
        session.Faulted += (_, exception) => faults.Add(exception);

        _factory.Renders[0].Fail(new InvalidOperationException("the headphones were removed"));
        _factory.Renders[0].Fail(new InvalidOperationException("and again"));

        Assert.Single(faults);
    }

    [Fact]
    public async Task Faults_are_not_reported_while_the_session_is_stopped()
    {
        // The clients outlive the session's interest in them, and a fault raised during teardown
        // would send the module into recovery for a stream it has already let go of.
        NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        FakeCaptureClient capture = _factory.Captures[0];
        await session.StopAsync(CancellationToken.None);

        List<Exception> faults = [];
        session.Faulted += (_, exception) => faults.Add(exception);
        capture.Fail(new InvalidOperationException("late"));

        Assert.Empty(faults);

        await session.DisposeAsync();
    }

    // ---------------------------------------------------------------- stopping

    [Fact]
    public async Task Stop_stops_capture_before_render()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        await session.StopAsync(CancellationToken.None);

        Assert.False(session.IsRunning);
        Assert.True(
            _log.IndexOf("capture.stop") < _log.IndexOf("render.stop"),
            $"capture has to stop first, but the order was [{string.Join(", ", _log.Entries)}]");
    }

    [Fact]
    public async Task Stop_releases_the_capture_the_converter_and_the_render_side_in_that_order()
    {
        // Everything the session owns is released, capture first and the render side last. The ring
        // is released between the two as well, but it has no hook to record that with, so this pins
        // the three sides that do.
        _factory.CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        await session.StopAsync(CancellationToken.None);

        Assert.Equal(
            ["capture.stop", "render.stop", "capture.dispose", "converter.dispose", "render.dispose"],
            _log.Entries.SkipWhile(entry => entry != "capture.stop"));
    }

    [Fact]
    public async Task Stop_leaves_the_endpoints_free_to_be_opened_again()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.True(session.IsRunning);
        Assert.Equal(2, _factory.Captures.Count);
        Assert.All(_factory.Captures, client => Assert.True(client.IsDisposed || client.IsStarted));
    }

    [Fact]
    public async Task Repeated_stop_is_safe()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        await session.StopAsync(CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        Assert.False(session.IsRunning);
        Assert.Equal(1, _factory.Captures[0].DisposeCount);
        Assert.Equal(1, _factory.Renders[0].DisposeCount);
    }

    [Fact]
    public async Task Stop_before_start_is_safe()
    {
        await using NAudioSharedStreamSession session = CreateSession();

        await session.StopAsync(CancellationToken.None);

        Assert.False(session.IsRunning);
        Assert.Empty(_factory.Captures);
    }

    [Fact]
    public async Task A_stop_that_never_completes_is_logged_rather_than_waited_on_forever()
    {
        RecordingLogger logger = new();
        NAudioSharedStreamSession session = new(_factory, logger, CreateConverter);
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        // A device driver that has wedged must not wedge the application with it.
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Captures[0].StopGate = gate;

        Task stop = session.StopAsync(CancellationToken.None);

        Task finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(finished == stop, "StopAsync never returned");
        Assert.True(logger.Logged("audio.stream.stopTimedOut"), "the wedged stop was never reported");

        gate.SetResult();
        await session.DisposeAsync();
    }

    // ---------------------------------------------------------------- metrics

    [Fact]
    public async Task Metrics_report_the_buffer_sizes_that_were_asked_for()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        AudioStreamMetrics metrics = session.Metrics;

        Assert.Equal(_factory.Captures[0].BufferMilliseconds, metrics.CaptureBufferMilliseconds);
        Assert.Equal(_factory.Renders[0].BufferMilliseconds, metrics.RenderBufferMilliseconds);
        Assert.True(metrics.EstimatedAdditionalLatencyMilliseconds > 0.0);
    }

    [Fact]
    public async Task Metrics_report_underruns_as_they_happen()
    {
        await using NAudioSharedStreamSession session = CreateSession();
        await session.StartAsync(Route(), Limiter(), CancellationToken.None);

        Assert.Equal(0, session.Metrics.Underruns);

        // Nothing was ever captured, so this read has to be padded with silence.
        _ = ReadFloats(_factory.Renders[0].Source!, 32);

        Assert.Equal(1, session.Metrics.Underruns);
    }

    [Fact]
    public async Task Metrics_report_nothing_running_before_the_first_start()
    {
        await using NAudioSharedStreamSession session = CreateSession();

        Assert.False(session.IsRunning);
        Assert.Equal(0, session.Metrics.Underruns);
        Assert.Equal(0, session.Metrics.CaptureBufferMilliseconds);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Float32Bytes(params float[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(float)];

        for (int index = 0; index < samples.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float)), samples[index]);
        }

        return bytes;
    }

    private static float[] ReadFloats(IWaveProvider provider, int count)
    {
        byte[] buffer = new byte[count * sizeof(float)];
        int read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);

        float[] samples = new float[count];
        Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
        return samples;
    }

    /// <summary>Records the order lifecycle calls arrive in, across both fake clients.</summary>
    private sealed class LifecycleLog
    {
        private readonly List<string> _entries = [];

        internal IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        internal void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        internal int IndexOf(string entry)
        {
            lock (_entries)
            {
                return _entries.IndexOf(entry);
            }
        }
    }

    private sealed class FakeClientFactory : INAudioClientFactory
    {
        internal WaveFormat CaptureFormat { get; set; } = Stereo48Float;

        internal WaveFormat RenderFormat { get; set; } = Stereo48Float;

        internal Exception? CaptureCreateFailure { get; set; }

        internal Exception? RenderCreateFailure { get; set; }

        internal List<FakeCaptureClient> Captures { get; } = [];

        internal List<FakeRenderClient> Renders { get; } = [];

        private LifecycleLog Log { get; set; } = new();

        internal void UseLog(LifecycleLog log) => Log = log;

        public ISharedCaptureClient CreateCapture(string endpointId)
        {
            if (CaptureCreateFailure is not null)
            {
                throw CaptureCreateFailure;
            }

            FakeCaptureClient client = new(endpointId, CaptureFormat, 100, Log);
            Captures.Add(client);
            return client;
        }

        public ISharedRenderClient CreateRender(string endpointId, int latencyMilliseconds)
        {
            if (RenderCreateFailure is not null)
            {
                throw RenderCreateFailure;
            }

            FakeRenderClient client = new(endpointId, RenderFormat, latencyMilliseconds, Log);
            Renders.Add(client);
            return client;
        }
    }

    private sealed class FakeCaptureClient : ISharedCaptureClient
    {
        private readonly LifecycleLog _log;

        internal FakeCaptureClient(string endpointId, WaveFormat waveFormat, int bufferMilliseconds, LifecycleLog log)
        {
            EndpointId = endpointId;
            WaveFormat = waveFormat;
            BufferMilliseconds = bufferMilliseconds;
            _log = log;
        }

        public event EventHandler<CapturedAudioEventArgs>? DataAvailable;

        public event EventHandler<Exception>? Faulted;

        public WaveFormat WaveFormat { get; }

        public int BufferMilliseconds { get; }

        internal string EndpointId { get; }

        internal bool IsStarted { get; private set; }

        internal bool IsDisposed { get; private set; }

        internal int DisposeCount { get; private set; }

        /// <summary>When set, Stop blocks until it is completed.</summary>
        internal TaskCompletionSource? StopGate { get; set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _log.Add("capture.start");
            await Task.Yield();
            IsStarted = true;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _log.Add("capture.stop");

            if (StopGate is { } gate)
            {
                await gate.Task;
            }
        }

        public ValueTask DisposeAsync()
        {
            _log.Add("capture.dispose");
            IsDisposed = true;
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Deliver(byte[] buffer) =>
            DataAvailable?.Invoke(this, new CapturedAudioEventArgs(buffer, buffer.Length));

        internal void Fail(Exception exception) => Faulted?.Invoke(this, exception);
    }

    private sealed class FakeRenderClient : ISharedRenderClient
    {
        private readonly LifecycleLog _log;

        internal FakeRenderClient(string endpointId, WaveFormat mixFormat, int latencyMilliseconds, LifecycleLog log)
        {
            EndpointId = endpointId;
            MixFormat = mixFormat;
            RequestedLatencyMilliseconds = latencyMilliseconds;
            BufferMilliseconds = latencyMilliseconds;
            _log = log;
        }

        public event EventHandler<Exception>? Faulted;

        public WaveFormat MixFormat { get; }

        public int BufferMilliseconds { get; }

        internal string EndpointId { get; }

        internal int RequestedLatencyMilliseconds { get; }

        internal bool IsStarted { get; private set; }

        internal bool IsDisposed { get; private set; }

        internal int DisposeCount { get; private set; }

        internal IWaveProvider? Source { get; private set; }

        internal AudioClientShareMode? ShareMode { get; private set; }

        public async Task StartAsync(
            IWaveProvider provider,
            AudioClientShareMode shareMode,
            CancellationToken cancellationToken)
        {
            _log.Add("render.start");
            await Task.Yield();

            Source = provider;
            ShareMode = shareMode;
            IsStarted = true;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _log.Add("render.stop");
            await Task.Yield();
        }

        public ValueTask DisposeAsync()
        {
            _log.Add("render.dispose");
            IsDisposed = true;
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Fail(Exception exception) => Faulted?.Invoke(this, exception);
    }

    private sealed class FakeFormatConverter : IWaveProvider, IDisposable
    {
        private readonly IWaveProvider _source;
        private readonly LifecycleLog _log;

        internal FakeFormatConverter(IWaveProvider source, WaveFormat targetFormat, LifecycleLog log)
        {
            _source = source;
            _log = log;
            WaveFormat = targetFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);

        public void Dispose() => _log.Add("converter.dispose");
    }

}
