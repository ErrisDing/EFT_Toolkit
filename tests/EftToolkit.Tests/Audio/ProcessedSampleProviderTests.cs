using EftToolkit.Audio.Streaming;
using NAudio.Wave;

namespace EftToolkit.Tests.Audio;

public class ProcessedSampleProviderTests
{
    private const int SampleRate = 48_000;

    /// <summary>Ten milliseconds: 480 stereo frames, small enough to fill in a test by hand.</summary>
    private static readonly TimeSpan Capacity = TimeSpan.FromMilliseconds(10);

    private const int CapacityFrames = 480;

    private const int FrameBytes = sizeof(float) * 2;

    private static ProcessedSampleProvider CreateProvider() => new(SampleRate, Capacity);

    private static float[] Frames(int frameCount, float value)
    {
        float[] samples = new float[frameCount * 2];
        Array.Fill(samples, value);
        return samples;
    }

    /// <summary>Reads whole frames out of the provider as floats.</summary>
    private static float[] ReadFrames(ProcessedSampleProvider provider, int frameCount)
    {
        byte[] buffer = new byte[frameCount * FrameBytes];
        int read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);

        float[] samples = new float[frameCount * 2];
        Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
        return samples;
    }

    // ---------------------------------------------------------------- the shape of the output

    [Fact]
    public void The_provider_presents_stereo_float_at_the_negotiated_rate()
    {
        ProcessedSampleProvider provider = CreateProvider();

        Assert.Equal(2, provider.WaveFormat.Channels);
        Assert.Equal(SampleRate, provider.WaveFormat.SampleRate);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, provider.WaveFormat.Encoding);
    }

    [Fact]
    public void Enqueued_audio_is_read_back_in_order()
    {
        ProcessedSampleProvider provider = CreateProvider();

        int accepted = provider.Enqueue([0.1f, 0.2f, 0.3f, 0.4f]);

        Assert.Equal(2, accepted);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], ReadFrames(provider, 2));
    }

    [Fact]
    public void The_ring_is_read_in_the_order_it_was_written_across_a_wrap()
    {
        ProcessedSampleProvider provider = CreateProvider();

        // Fill the ring, drain it, then write again so the read and write cursors have both wrapped
        // past the end of the buffer and one frame would land in the wrong place if either were
        // reset rather than advanced.
        provider.Enqueue(Frames(CapacityFrames, 1.0f));
        ReadFrames(provider, CapacityFrames);

        provider.Enqueue(Frames(4, 2.0f));

        Assert.Equal(Frames(4, 2.0f), ReadFrames(provider, 4));
    }

    // ---------------------------------------------------------------- empty and partial reads

    [Fact]
    public void An_empty_provider_reads_silence_and_counts_one_underrun()
    {
        ProcessedSampleProvider provider = CreateProvider();

        float[] samples = ReadFrames(provider, 64);

        Assert.All(samples, sample => Assert.Equal(0.0f, sample));
        Assert.Equal(1, provider.Underruns);
    }

    [Fact]
    public void A_partial_read_is_padded_with_silence_and_counted_once()
    {
        // The render side always wants a whole buffer. Returning a short one would look like the end
        // of the stream rather than a gap in it.
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(10, 0.5f));

        float[] samples = ReadFrames(provider, 64);

        Assert.All(samples.Take(20), sample => Assert.Equal(0.5f, sample));
        Assert.All(samples.Skip(20), sample => Assert.Equal(0.0f, sample));
        Assert.Equal(1, provider.Underruns);
    }

    [Fact]
    public void A_read_that_was_satisfied_entirely_counts_no_underrun()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(64, 0.5f));

        ReadFrames(provider, 64);

        Assert.Equal(0, provider.Underruns);
    }

    [Fact]
    public void An_empty_read_still_returns_the_whole_requested_buffer()
    {
        ProcessedSampleProvider provider = CreateProvider();

        byte[] buffer = new byte[37];
        int read = provider.Read(buffer, 0, buffer.Length);

        // Four whole frames fit in thirty-seven bytes; the odd byte is not a frame and is left alone.
        Assert.Equal(32, read);
    }

    [Fact]
    public void A_request_too_small_to_hold_a_frame_reads_nothing()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(4, 0.5f));

        Assert.Equal(0, provider.Read(new byte[4], 0, 4));
    }

    // ---------------------------------------------------------------- overflow

    [Fact]
    public void Overflow_drops_the_newest_block_and_counts_an_overrun()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));

        int accepted = provider.Enqueue(Frames(CapacityFrames, 2.0f));

        Assert.Equal(0, accepted);
        Assert.Equal(1, provider.Overruns);

        // What the user has not heard yet is still there; the block that had nowhere to go is the
        // one that was thrown away, not the audio already queued.
        Assert.Equal(1.0f, ReadFrames(provider, 1)[0]);
    }

    [Fact]
    public void Overflow_never_replays_a_block_that_was_dropped()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));
        provider.Enqueue(Frames(CapacityFrames, 2.0f));

        float[] samples = ReadFrames(provider, CapacityFrames);

        Assert.DoesNotContain(2.0f, samples);
        Assert.All(samples, sample => Assert.Equal(1.0f, sample));
    }

    [Fact]
    public void A_block_that_fits_exactly_is_accepted()
    {
        ProcessedSampleProvider provider = CreateProvider();

        Assert.Equal(CapacityFrames, provider.Enqueue(Frames(CapacityFrames, 0.25f)));
        Assert.Equal(0, provider.Overruns);
    }

    [Fact]
    public void Space_becomes_available_again_once_the_ring_is_drained()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));
        ReadFrames(provider, CapacityFrames);

        Assert.Equal(CapacityFrames, provider.Enqueue(Frames(CapacityFrames, 2.0f)));
        Assert.Equal(0, provider.Overruns);
    }

    [Fact]
    public void An_empty_block_is_accepted_without_counting_an_overrun()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));

        Assert.Equal(0, provider.Enqueue([]));
        Assert.Equal(0, provider.Overruns);
    }

    [Fact]
    public void An_odd_sample_count_is_rejected_because_frames_are_stereo()
    {
        ProcessedSampleProvider provider = CreateProvider();

        Assert.Throws<ArgumentException>(() => provider.Enqueue(new float[3]));
    }

    // ---------------------------------------------------------------- the stop ramp

    [Fact]
    public void A_fade_out_ramps_the_output_to_silence()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));

        // Half the ring, so the ramp is measurable across frames that were already queued.
        provider.BeginFadeOut(TimeSpan.FromMilliseconds(5));

        float[] samples = ReadFrames(provider, CapacityFrames);

        // Left channel of each frame.
        float[] left = [.. Enumerable.Range(0, CapacityFrames).Select(frame => samples[frame * 2])];

        Assert.True(left[0] < 1.0f, $"the first frame should already be attenuated, got {left[0]}");
        Assert.True(left[0] > 0.99f, $"the ramp should start near unity, got {left[0]}");
        Assert.Equal(0.0f, left[239]);
        Assert.All(left.Skip(240), sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void A_fade_out_keeps_the_output_silent_afterwards()
    {
        // Once the streams are closing the output must not come back: a stream that resumed here
        // would emit a burst of whatever was left in the ring as the device shut down.
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));
        provider.BeginFadeOut(TimeSpan.FromMilliseconds(1));
        ReadFrames(provider, CapacityFrames);

        provider.Enqueue(Frames(64, 1.0f));

        Assert.All(ReadFrames(provider, 64), sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void The_fade_out_length_is_derived_from_the_sample_rate()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(CapacityFrames, 1.0f));
        provider.BeginFadeOut(TimeSpan.FromMilliseconds(10));

        float[] samples = ReadFrames(provider, CapacityFrames);

        // Ten milliseconds at 48 kHz is 480 frames, so the ramp covers the whole ring and lands on
        // zero exactly at the end of it.
        Assert.Equal(0.0f, samples[(479 * 2)]);
    }

    // ---------------------------------------------------------------- disposal

    [Fact]
    public void A_disposed_provider_reads_silence_rather_than_stale_audio()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Enqueue(Frames(64, 1.0f));

        provider.Dispose();

        Assert.All(ReadFrames(provider, 64), sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void A_disposed_provider_accepts_nothing_further()
    {
        ProcessedSampleProvider provider = CreateProvider();
        provider.Dispose();

        Assert.Equal(0, provider.Enqueue(Frames(64, 1.0f)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-48_000)]
    public void An_unusable_sample_rate_is_rejected(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessedSampleProvider(sampleRate, Capacity));
    }
}
