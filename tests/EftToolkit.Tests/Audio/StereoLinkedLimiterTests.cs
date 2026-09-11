using EftToolkit.Audio.Dsp;
using EftToolkit.Core.Configuration;

namespace EftToolkit.Tests.Audio;

public class StereoLinkedLimiterTests
{
    private const int SampleRate = 48_000;

    /// <summary>
    /// Attack is zero by default so a block's gain has settled within one frame; the tests that
    /// care about the attack shape set it explicitly. Ceiling is the default -1 dBFS ceiling the
    /// configuration ships with.
    /// </summary>
    private static AudioLimiterOptions TestSettings(
        double inputGainDb = 0,
        double thresholdDbFs = -6,
        double ratio = 4,
        double kneeDb = 6,
        double lookAheadMs = 0,
        double attackMs = 0,
        double releaseMs = 50,
        double ceilingDbFs = -1) =>
        new(inputGainDb, thresholdDbFs, ratio, kneeDb, lookAheadMs, attackMs, releaseMs, ceilingDbFs);

    private static float CeilingLinear => (float)AudioMath.DbToLinear(-1);

    // ---------------------------------------------------------------- linkage and ceiling

    [Fact]
    public void Process_applies_identical_gain_reduction_to_both_channels()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(inputGainDb: 12, lookAheadMs: 0));
        float[] interleaved = [0.90f, 0.09f, 0.90f, 0.09f];

        limiter.Process(interleaved);

        // Both channels are scaled by the gain the louder one needs, so the stereo image is
        // untouched: the ratio between the channels is exactly what it was.
        Assert.Equal(10.0f, interleaved[0] / interleaved[1], 3);
        Assert.Equal(10.0f, interleaved[2] / interleaved[3], 3);
        Assert.All(interleaved, sample => Assert.InRange(Math.Abs(sample), 0.0f, CeilingLinear));
    }

    [Fact]
    public void A_quiet_frame_passes_through_untouched()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings());

        // -20 dBFS, well under the -6 dBFS threshold.
        float[] interleaved = [0.1f, -0.1f];

        limiter.Process(interleaved);

        Assert.Equal(0.1f, interleaved[0], 6);
        Assert.Equal(-0.1f, interleaved[1], 6);
    }

    [Fact]
    public void Process_never_exceeds_the_ceiling_for_any_input()
    {
        StereoLinkedLimiter limiter = new(
            SampleRate,
            TestSettings(inputGainDb: 24, thresholdDbFs: -12, ratio: 8, ceilingDbFs: -1));

        Random random = new(20260911);
        float[] block = new float[512];

        for (int round = 0; round < 200; round++)
        {
            for (int index = 0; index < block.Length; index++)
            {
                // Full-scale noise, far past the threshold.
                block[index] = (float)((random.NextDouble() * 2.0) - 1.0);
            }

            limiter.Process(block);

            Assert.All(block, sample =>
            {
                Assert.True(float.IsFinite(sample));
                Assert.InRange(Math.Abs(sample), 0.0f, CeilingLinear + 1e-6f);
            });
        }
    }

    // ---------------------------------------------------------------- hostile input

    [Fact]
    public void Process_replaces_non_finite_input_with_silence()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings());
        float[] interleaved = [float.NaN, float.PositiveInfinity];

        limiter.Process(interleaved);

        Assert.Equal([0.0f, 0.0f], interleaved);
    }

    [Fact]
    public void A_non_finite_sample_does_not_poison_the_frames_after_it()
    {
        // A single NaN would otherwise sit in the look-ahead buffer and be multiplied by the
        // envelope on every subsequent block that reads it.
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(lookAheadMs: 5));
        float[] block = new float[2000];
        block[0] = float.NaN;
        block[1] = float.NegativeInfinity;

        limiter.Process(block);

        Assert.All(block, sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void An_odd_sample_count_is_rejected_because_frames_are_stereo()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings());

        Assert.Throws<ArgumentException>(() => limiter.Process(new float[3]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(-48_000)]
    public void An_unusable_sample_rate_is_rejected(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StereoLinkedLimiter(sampleRate, TestSettings()));
    }

    // ---------------------------------------------------------------- look-ahead

    [Theory]
    [InlineData(44_100, 1.0, 44)]
    [InlineData(48_000, 1.0, 48)]
    [InlineData(48_000, 0.5, 24)]
    [InlineData(48_000, 2.0, 96)]
    [InlineData(44_100, 2.4, 106)]
    public void LookAhead_delays_the_output_by_the_configured_number_of_frames(
        int sampleRate,
        double lookAheadMs,
        int expectedFrames)
    {
        // A threshold at full scale with no knee, so every frame comes out at unity and this
        // measures the delay and nothing else.
        StereoLinkedLimiter limiter = new(
            sampleRate,
            TestSettings(lookAheadMs: lookAheadMs, thresholdDbFs: 0, kneeDb: 0));

        int frames = expectedFrames + 10;
        float[] block = new float[frames * 2];

        // A constant half-scale frame, immediately followed by silence. Nothing is above the
        // threshold, so the signal arrives at the output unchanged apart from its position.
        for (int frame = 0; frame < frames; frame++)
        {
            block[frame * 2] = 0.5f;
            block[(frame * 2) + 1] = 0.5f;
        }

        limiter.Process(block);

        // Whichever sample is loudest marks where the impulse landed.
        int loudest = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            if (Math.Abs(block[frame * 2]) > Math.Abs(block[loudest * 2]))
            {
                loudest = frame;
            }
        }

        Assert.Equal(expectedFrames, loudest);
    }

    [Fact]
    public void A_zero_length_look_ahead_passes_samples_straight_through()
    {
        // A threshold at full scale, so this measures the delay and nothing else.
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(lookAheadMs: 0, thresholdDbFs: 0, kneeDb: 0));
        float[] interleaved = [0.5f, 0.5f, 0.0f, 0.0f];

        limiter.Process(interleaved);

        Assert.Equal(0.5f, interleaved[0], 6);
        Assert.Equal(0.5f, interleaved[1], 6);
        Assert.Equal(0.0f, interleaved[2]);
        Assert.Equal(0.0f, interleaved[3]);
    }

    // ---------------------------------------------------------------- knee and envelope

    [Fact]
    public void A_soft_knee_begins_reducing_before_the_threshold_is_reached()
    {
        // A 6 dB knee around a -9 dBFS threshold spans -12 to -6, so a -10.5 dBFS input is inside
        // the knee and is already being touched even though it is under the threshold.
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(thresholdDbFs: -9, kneeDb: 6, ratio: 4));
        float[] interleaved = [(float)AudioMath.DbToLinear(-10.5), (float)AudioMath.DbToLinear(-10.5)];

        limiter.Process(interleaved);

        float outputDb = (float)AudioMath.LinearToDb(Math.Abs(interleaved[0]));
        Assert.True(outputDb < -10.5f, $"expected the knee to reduce a -10.5 dBFS input, got {outputDb} dBFS");
    }

    [Fact]
    public void The_edge_of_a_soft_knee_is_not_yet_reducing()
    {
        // Exactly at the bottom of the knee, the curve starts from unity: the quadratic term is
        // zero there, so this is the boundary the soft knee has to meet without a step.
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(thresholdDbFs: -9, kneeDb: 6, ratio: 4));
        float[] interleaved = [(float)AudioMath.DbToLinear(-12), (float)AudioMath.DbToLinear(-12)];

        limiter.Process(interleaved);

        Assert.Equal(-12.0, AudioMath.LinearToDb(Math.Abs(interleaved[0])), 3);
    }

    [Fact]
    public void A_signal_below_the_knee_is_left_alone()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(thresholdDbFs: -9, kneeDb: 6));
        float[] interleaved = [(float)AudioMath.DbToLinear(-30), (float)AudioMath.DbToLinear(-30)];

        limiter.Process(interleaved);

        Assert.Equal(-30.0, AudioMath.LinearToDb(Math.Abs(interleaved[0])), 3);
    }

    [Fact]
    public void A_zero_width_knee_does_not_divide_by_zero_and_behaves_as_a_hard_knee()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(thresholdDbFs: -6, kneeDb: 0, ratio: 4));

        // Just below the threshold: untouched.
        float[] below = [(float)AudioMath.DbToLinear(-6.5), (float)AudioMath.DbToLinear(-6.5)];
        limiter.Process(below);
        Assert.Equal(-6.5, AudioMath.LinearToDb(Math.Abs(below[0])), 3);

        // Just above it: reduced, and finite.
        float[] above = [(float)AudioMath.DbToLinear(-5.5), (float)AudioMath.DbToLinear(-5.5)];
        limiter.Process(above);
        Assert.True(float.IsFinite(above[0]));
        Assert.True(AudioMath.LinearToDb(Math.Abs(above[0])) < -5.5);
    }

    [Fact]
    public void Gain_reduction_recovers_toward_unity_once_the_signal_stops()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(
            inputGainDb: 18,
            lookAheadMs: 1,
            attackMs: 1,
            releaseMs: 20));

        // A loud burst, long enough for the envelope to settle into heavy reduction.
        float[] loud = new float[4800 * 2];
        Array.Fill(loud, 0.8f);
        limiter.Process(loud);

        double duringBurst = limiter.Metrics.GainReductionDb;
        Assert.True(duringBurst > 6.0, $"expected heavy reduction during the burst, got {duringBurst} dB");

        // Then silence, in blocks, for well over the release time. Metrics report the deepest
        // reduction reached inside the block that was just processed, so a single long block would
        // report the reduction it started with rather than the recovery it ended on.
        //
        // Processing writes into the span, so the array has to be cleared before each block. Reusing
        // it as-is would feed the previous block's look-ahead tail straight back in as input.
        float[] silence = new float[4800 * 2];
        double afterRelease = duringBurst;

        for (int block = 0; block < 5; block++)
        {
            Array.Clear(silence);
            limiter.Process(silence);
            afterRelease = limiter.Metrics.GainReductionDb;
        }

        Assert.True(afterRelease < 0.5, $"expected the gain to recover, still at {afterRelease} dB");
    }

    [Fact]
    public void A_steady_sine_settles_without_overshoot_or_instability()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(
            inputGainDb: 12,
            lookAheadMs: 2,
            attackMs: 1,
            releaseMs: 100));

        float[] block = new float[1024];
        double previous = 0.0;

        for (int round = 0; round < 60; round++)
        {
            FillSine(block, SampleRate, 1000, 0.9, (round * (block.Length / 2)) / (double)SampleRate);

            limiter.Process(block);

            Assert.All(block, sample =>
            {
                Assert.True(float.IsFinite(sample));
                Assert.InRange(Math.Abs(sample), 0.0f, CeilingLinear + 1e-6f);
            });

            // Reduction deepens or holds; it must not oscillate downward forever.
            double reduction = limiter.Metrics.GainReductionDb;
            Assert.True(reduction >= previous - 1.0, $"reduction moved from {previous} to {reduction} dB");
            previous = reduction;
        }

        Assert.True(previous > 6.0, $"expected sustained reduction, got {previous} dB");
    }

    [Fact]
    public void Reset_silences_the_delay_line_and_returns_the_gain_to_unity()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(inputGainDb: 24, lookAheadMs: 2));

        float[] loud = new float[960 * 2];
        Array.Fill(loud, 0.9f);
        limiter.Process(loud);

        Assert.True(limiter.Metrics.GainReductionDb > 1.0);

        limiter.Reset();

        Assert.Equal(0.0, limiter.Metrics.GainReductionDb);

        // The delay line is empty, so the very next block cannot leak the previous one.
        float[] probe = new float[480 * 2];
        limiter.Process(probe);

        Assert.All(probe, sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void Reconfigure_applies_the_new_look_ahead_length_without_reusing_the_old_buffer()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(lookAheadMs: 0));

        limiter.Reconfigure(SampleRate, TestSettings(lookAheadMs: 1, thresholdDbFs: 0, kneeDb: 0));

        // 1 ms at 48 kHz is a 48 frame delay.
        float[] block = new float[120 * 2];
        for (int frame = 0; frame < 120; frame++)
        {
            block[frame * 2] = 0.5f;
            block[(frame * 2) + 1] = 0.5f;
        }

        limiter.Process(block);

        // The new line starts empty, so the first 48 frames come out silent and the input only
        // begins to appear once the whole of the new delay has been filled.
        Assert.Equal(0.0f, block[0]);
        Assert.Equal(0.0f, block[47 * 2]);
        Assert.Equal(0.5f, block[48 * 2], 6);
    }

    // ---------------------------------------------------------------- metering and throughput

    [Fact]
    public void Metrics_report_the_block_that_was_just_processed()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(inputGainDb: 12, thresholdDbFs: -6));

        float[] quiet = [0.001f, 0.001f];
        limiter.Process(quiet);
        Assert.Equal(0.0, limiter.Metrics.GainReductionDb, 3);

        float[] loud = [0.9f, 0.9f];
        limiter.Process(loud);

        AudioProcessorMetrics metrics = limiter.Metrics;
        Assert.True(metrics.GainReductionDb > 0.0);
        Assert.True(double.IsFinite(metrics.InputPeakDbFs));
        Assert.True(double.IsFinite(metrics.OutputPeakDbFs));
        Assert.True(metrics.OutputPeakDbFs <= metrics.InputPeakDbFs);
    }

    [Fact]
    public void Thirty_seconds_of_audio_stays_finite_and_within_the_ceiling()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(
            inputGainDb: 12,
            lookAheadMs: 3,
            attackMs: 2,
            releaseMs: 250));

        const int BlockFrames = 512;
        float[] block = new float[BlockFrames * 2];
        long frames = 0;
        double phase = 0;

        // Sweeps through a sine and a square-ish signal, then full-scale noise, so the detector
        // meets transients, steady state, and content with no usable structure.
        Random random = new(20260911);
        long target = 30L * SampleRate;

        while (frames < target)
        {
            double frequency = 40.0 + (2000.0 * ((frames % (SampleRate * 5)) / (double)(SampleRate * 5)));
            FillSine(block, SampleRate, frequency, 0.95, phase);
            phase += (BlockFrames * frequency) / SampleRate;

            for (int index = 0; index < block.Length; index += 2)
            {
                block[index] = (float)((block[index] + block[index + 1]) / 2.0);
                block[index + 1] = block[index];
            }

            if (frames > 15L * SampleRate && frames < 16L * SampleRate)
            {
                for (int index = 0; index < block.Length; index++)
                {
                    block[index] = (float)((random.NextDouble() * 2.0) - 1.0);
                }
            }

            limiter.Process(block);

            Assert.All(block, sample =>
            {
                Assert.True(float.IsFinite(sample), "the limiter produced a non-finite sample");
                Assert.InRange(Math.Abs(sample), 0.0f, CeilingLinear + 1e-6f);
            });

            frames += BlockFrames;
        }
    }

    [Fact]
    public void Processing_a_block_does_not_allocate_once_warmed_up()
    {
        StereoLinkedLimiter limiter = new(SampleRate, TestSettings(
            inputGainDb: 12,
            lookAheadMs: 2,
            attackMs: 1,
            releaseMs: 100));

        float[] block = new float[960 * 2];

        // Warm up the JIT and let the steady state settle before measuring.
        for (int round = 0; round < 50; round++)
        {
            FillSine(block, SampleRate, 440, 0.9, round * 0.01);
            limiter.Process(block);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int round = 0; round < 200; round++)
        {
            limiter.Process(block);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void FillSine(
        float[] interleaved,
        int sampleRate,
        double frequency,
        double amplitude,
        double startSeconds)
    {
        for (int frame = 0; frame < interleaved.Length / 2; frame++)
        {
            float value = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * (startSeconds + (frame / (double)sampleRate))));
            interleaved[frame * 2] = value;
            interleaved[(frame * 2) + 1] = value;
        }
    }
}
