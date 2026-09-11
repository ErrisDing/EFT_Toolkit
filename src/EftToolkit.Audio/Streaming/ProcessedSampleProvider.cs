using System.Buffers.Binary;
using NAudio.Wave;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// The render side of the audio path: a bounded ring of processed stereo frames that WASAPI pulls
/// from, written by the capture callback.
/// </summary>
/// <remarks>
/// <para>
/// Capture and render run on different threads and are driven by two clocks that drift. The ring
/// absorbs that drift, and its two failure modes are handled differently on purpose. A read with
/// nothing left is padded with silence: the render side has to be given a full buffer, and a short
/// one would look like the end of the stream. A write with nowhere to go drops the block it was
/// handed, because the audio already queued is closer to what the user is hearing.
/// </para>
/// <para>
/// Owns no unmanaged resource, but is disposable so the stop path can clear the ring and be sure
/// nothing queued will be played after the streams have closed.
/// </para>
/// </remarks>
public sealed class ProcessedSampleProvider : IWaveProvider, IDisposable
{
    /// <summary>
    /// A tenth of a second. Long enough to ride out a scheduler hiccup on a loaded machine, short
    /// enough that the delay it can add is not audible as the game lagging its own sound.
    /// </summary>
    public static readonly TimeSpan DefaultCapacity = TimeSpan.FromMilliseconds(100);

    private const int ChannelCount = 2;
    private const int BytesPerSample = sizeof(float);
    private const int FrameBytes = ChannelCount * BytesPerSample;

    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private readonly object _gate = new();

    private int _readFrame;
    private int _writeFrame;
    private int _availableFrames;

    private int _fadeFramesTotal;
    private int _fadeFramesRemaining;

    private long _underruns;
    private long _overruns;
    private bool _disposed;

    public ProcessedSampleProvider(int sampleRate, TimeSpan? capacity = null)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "A sample rate must be positive.");
        }

        TimeSpan window = capacity ?? DefaultCapacity;

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                window,
                "The ring has to hold at least one frame.");
        }

        _capacityFrames = Math.Max(1, (int)Math.Round(sampleRate * window.TotalSeconds));
        _ring = new float[_capacityFrames * ChannelCount];

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, ChannelCount);
    }

    /// <summary>Interleaved 32-bit float stereo at the rate the limiter is running at.</summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>Reads that had to be padded with silence because the capture side had fallen behind.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Blocks that arrived with no room and were dropped.</summary>
    public long Overruns => Interlocked.Read(ref _overruns);

    /// <summary>Frames waiting to be played.</summary>
    public int AvailableFrames
    {
        get
        {
            lock (_gate)
            {
                return _availableFrames;
            }
        }
    }

    /// <summary>
    /// Queues processed frames. Returns how many were taken, which is either all of them or none:
    /// a block that does not fit is dropped whole rather than split.
    /// </summary>
    /// <exception cref="ArgumentException">The span does not hold whole stereo frames.</exception>
    public int Enqueue(ReadOnlySpan<float> interleavedStereo)
    {
        if (interleavedStereo.Length % ChannelCount != 0)
        {
            throw new ArgumentException(
                "Interleaved stereo audio must contain a whole number of two-channel frames.",
                nameof(interleavedStereo));
        }

        int frames = interleavedStereo.Length / ChannelCount;

        if (frames == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            if (frames > _capacityFrames - _availableFrames)
            {
                // Dropping the newest block keeps the ring holding what the user is about to hear.
                // Overwriting the oldest would replay audio they have already heard, which is worse
                // than a gap.
                Interlocked.Increment(ref _overruns);
                return 0;
            }

            for (int frame = 0; frame < frames; frame++)
            {
                int destination = ((_writeFrame + frame) % _capacityFrames) * ChannelCount;
                _ring[destination] = interleavedStereo[frame * ChannelCount];
                _ring[destination + 1] = interleavedStereo[(frame * ChannelCount) + 1];
            }

            _writeFrame = (_writeFrame + frames) % _capacityFrames;
            _availableFrames += frames;
        }

        return frames;
    }

    /// <summary>
    /// Begins ramping the output to silence over the given time. Frames that were already queued are
    /// ramped too, so the audio leaves the speakers rather than stopping between two samples.
    /// </summary>
    public void BeginFadeOut(TimeSpan duration)
    {
        lock (_gate)
        {
            _fadeFramesTotal = Math.Max(
                1,
                (int)Math.Round(WaveFormat.SampleRate * Math.Max(duration.TotalSeconds, 0.0)));
            _fadeFramesRemaining = _fadeFramesTotal;
        }
    }

    /// <summary>
    /// Fills the requested buffer. The return value is always the largest whole number of frames
    /// that fits: a live source has no end of stream, and reporting one would stop the render side.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int requestedFrames = count / FrameBytes;

        if (requestedFrames <= 0)
        {
            return 0;
        }

        int framesRead;

        lock (_gate)
        {
            framesRead = _disposed ? 0 : Math.Min(requestedFrames, _availableFrames);

            for (int frame = 0; frame < framesRead; frame++)
            {
                int source = ((_readFrame + frame) % _capacityFrames) * ChannelCount;
                float gain = NextFadeGain();

                Write(buffer, offset, frame, 0, _ring[source] * gain);
                Write(buffer, offset, frame, 1, _ring[source + 1] * gain);
            }

            _readFrame = (_readFrame + framesRead) % _capacityFrames;
            _availableFrames -= framesRead;

            if (_disposed)
            {
                _availableFrames = 0;
            }
        }

        if (framesRead < requestedFrames)
        {
            int silenceStart = offset + (framesRead * FrameBytes);
            Array.Clear(buffer, silenceStart, (requestedFrames - framesRead) * FrameBytes);

            Interlocked.Increment(ref _underruns);
        }

        return requestedFrames * FrameBytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Array.Clear(_ring);
            _availableFrames = 0;
            _readFrame = 0;
            _writeFrame = 0;
        }
    }

    /// <summary>
    /// The gain for the next frame. With no ramp in progress this is unity; during a ramp it walks
    /// down to exactly zero over the configured number of frames and stays there.
    /// </summary>
    private float NextFadeGain()
    {
        if (_fadeFramesTotal == 0)
        {
            return 1.0f;
        }

        if (_fadeFramesRemaining <= 0)
        {
            return 0.0f;
        }

        _fadeFramesRemaining--;

        return _fadeFramesRemaining / (float)_fadeFramesTotal;
    }

    /// <summary>
    /// Writes one sample. The provider always presents little-endian 32-bit float, so the sample is
    /// its bit pattern rather than a converted value.
    /// </summary>
    private static void Write(byte[] buffer, int offset, int frame, int channel, float value)
    {
        int index = offset + (frame * FrameBytes) + (channel * BytesPerSample);
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(index, BytesPerSample),
            BitConverter.SingleToInt32Bits(value));
    }
}
