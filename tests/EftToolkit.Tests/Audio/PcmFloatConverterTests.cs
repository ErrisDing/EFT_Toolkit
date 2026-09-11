using EftToolkit.Audio.Streaming;
using NAudio.Wave;

namespace EftToolkit.Tests.Audio;

public class PcmFloatConverterTests
{
    private static readonly WaveFormat Pcm16 = new(48_000, 16, 2);
    private static readonly WaveFormat Pcm24 = new(48_000, 24, 2);
    private static readonly WaveFormat Pcm32 = new(48_000, 32, 2);
    private static readonly WaveFormat Float32 = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    // ---------------------------------------------------------------- 16-bit PCM

    [Fact]
    public void Pcm16_maps_full_scale_negative_to_minus_one()
    {
        // 0x8000 is one step further from zero than 0x7FFF, and it is the sample that has to reach
        // -1 exactly: dividing by 32767 instead would put it past the ceiling.
        float[] destination = new float[2];

        int written = PcmFloatConverter.ConvertToFloat([0x00, 0x80, 0x00, 0x80], Pcm16, destination);

        Assert.Equal(2, written);
        Assert.Equal(-1.0f, destination[0]);
        Assert.Equal(-1.0f, destination[1]);
    }

    [Fact]
    public void Pcm16_maps_the_largest_positive_sample_to_just_under_full_scale()
    {
        float[] destination = new float[2];

        PcmFloatConverter.ConvertToFloat([0xFF, 0x7F, 0xFF, 0x7F], Pcm16, destination);

        Assert.Equal(32767f / 32768f, destination[0]);
        Assert.True(destination[0] <= 1.0f);
    }

    [Fact]
    public void Pcm16_converts_interleaved_frames_in_order()
    {
        float[] destination = new float[4];

        // Frames (16384, -16384) and (0, -32768).
        PcmFloatConverter.ConvertToFloat(
            [0x00, 0x40, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x80],
            Pcm16,
            destination);

        Assert.Equal([0.5f, -0.5f, 0.0f, -1.0f], destination);
    }

    // ---------------------------------------------------------------- 24-bit packed PCM

    [Fact]
    public void Pcm24_maps_its_ends_to_full_scale_negative_and_just_under_full_scale()
    {
        float[] destination = new float[2];

        // Most negative is 0x800000, most positive is 0x7FFFFF, both little-endian and packed.
        PcmFloatConverter.ConvertToFloat([0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F], Pcm24, destination);

        Assert.Equal(-1.0f, destination[0]);
        Assert.Equal(8388607f / 8388608f, destination[1]);
        Assert.True(destination[1] <= 1.0f);
    }

    [Fact]
    public void Pcm24_reads_packed_samples_from_the_right_offset_of_each_frame()
    {
        float[] destination = new float[2];

        // The second frame's bytes must not be shifted by the first frame's odd three-byte width.
        PcmFloatConverter.ConvertToFloat([0x00, 0x00, 0x00, 0x00, 0x00, 0x40], Pcm24, destination);

        Assert.Equal([0.0f, 0.5f], destination);
    }

    // ---------------------------------------------------------------- 32-bit PCM and float

    [Fact]
    public void Pcm32_maps_its_ends_to_full_scale_negative_and_just_under_full_scale()
    {
        float[] destination = new float[2];

        PcmFloatConverter.ConvertToFloat(
            [0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0x7F],
            Pcm32,
            destination);

        Assert.Equal(-1.0f, destination[0]);
        Assert.Equal(2147483647f / 2147483648f, destination[1]);
        Assert.True(destination[1] <= 1.0f);
    }

    [Fact]
    public void Float32_passes_samples_through_unchanged()
    {
        float[] destination = new float[2];

        PcmFloatConverter.ConvertToFloat(
            BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(-0.75f)).ToArray(),
            Float32,
            destination);

        Assert.Equal([0.25f, -0.75f], destination);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Non_finite_float_input_becomes_silence(float value)
    {
        // The limiter would otherwise multiply an infinity into the look-ahead line and keep
        // replaying it, and a NaN would sit in the ring forever.
        float[] destination = new float[2];

        PcmFloatConverter.ConvertToFloat(
            BitConverter.GetBytes(value).Concat(BitConverter.GetBytes(value)).ToArray(),
            Float32,
            destination);

        Assert.Equal([0.0f, 0.0f], destination);
    }

    // ---------------------------------------------------------------- extensible formats

    [Fact]
    public void An_extensible_float_format_is_resolved_before_conversion()
    {
        // WASAPI hands shared-mode streams a WAVE_FORMAT_EXTENSIBLE format, so the encoding is in
        // the subformat rather than in the encoding field.
        WaveFormat extensible = new WaveFormatExtensible(48_000, 32, 2, 22);
        float[] destination = new float[2];

        PcmFloatConverter.ConvertToFloat(
            BitConverter.GetBytes(0.5f).Concat(BitConverter.GetBytes(-0.5f)).ToArray(),
            extensible,
            destination);

        Assert.Equal([0.5f, -0.5f], destination);
    }

    [Fact]
    public void An_extensible_pcm_format_is_resolved_before_conversion()
    {
        WaveFormat extensible = new WaveFormatExtensible(48_000, 16, 2, 22);
        float[] destination = new float[2];

        PcmFloatConverter.ConvertToFloat([0x00, 0x80, 0xFF, 0x7F], extensible, destination);

        Assert.Equal(-1.0f, destination[0]);
        Assert.True(destination[1] <= 1.0f);
    }

    // ---------------------------------------------------------------- formats this path cannot take

    [Fact]
    public void A_mono_format_is_rejected()
    {
        WaveFormat mono = new(48_000, 16, 1);

        Assert.Throws<ArgumentException>(
            () => PcmFloatConverter.ConvertToFloat(new byte[4], mono, new float[4]));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(64)]
    public void An_unsupported_bit_depth_is_rejected(int bitsPerSample)
    {
        WaveFormat format = new(48_000, bitsPerSample, 2);

        Assert.Throws<NotSupportedException>(
            () => PcmFloatConverter.ConvertToFloat(new byte[64], format, new float[64]));
    }

    [Fact]
    public void An_unsupported_encoding_is_rejected()
    {
        WaveFormat compressed = WaveFormat.CreateMuLawFormat(48_000, 2);

        Assert.Throws<NotSupportedException>(
            () => PcmFloatConverter.ConvertToFloat(new byte[64], compressed, new float[64]));
    }

    // ---------------------------------------------------------------- buffer sizing

    [Fact]
    public void Conversion_stops_at_whichever_buffer_runs_out_first()
    {
        float[] destination = new float[2];

        // Sixteen samples offered, room for two.
        int written = PcmFloatConverter.ConvertToFloat(new byte[32], Pcm16, destination);

        Assert.Equal(2, written);
        Assert.Equal([0.0f, 0.0f], destination);
    }

    [Fact]
    public void A_trailing_partial_frame_is_ignored()
    {
        // Three samples: one whole stereo frame plus a leftover channel.
        float[] destination = new float[4];

        int written = PcmFloatConverter.ConvertToFloat([0x00, 0x40, 0x00, 0xC0, 0x00, 0x00], Pcm16, destination);

        Assert.Equal(2, written);
        Assert.Equal(0.5f, destination[0]);
    }

    [Fact]
    public void Conversion_reports_the_number_of_samples_written()
    {
        float[] destination = new float[8];

        int written = PcmFloatConverter.ConvertToFloat(new byte[6], Pcm24, destination);

        Assert.Equal(2, written);
    }

    [Fact]
    public void An_empty_source_writes_nothing()
    {
        float[] destination = new float[4];

        Assert.Equal(0, PcmFloatConverter.ConvertToFloat([], Pcm16, destination));
    }

    // ---------------------------------------------------------------- sizing a destination

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(48_000, 12_000)]
    public void The_frame_count_is_what_a_stereo_block_holds(int byteCount, int expectedFrames)
    {
        // Sixteen-bit stereo is four bytes a frame, so seven bytes is one frame and a leftover.
        Assert.Equal(expectedFrames, PcmFloatConverter.FrameCountFor(byteCount, Pcm16));
    }

    [Fact]
    public void The_frame_count_accounts_for_a_wider_sample()
    {
        // The same number of bytes is half as many frames at 32-bit as at 16-bit, which is exactly
        // the mistake an over-allocating scratch buffer would hide.
        Assert.Equal(8, PcmFloatConverter.FrameCountFor(32, Pcm16));
        Assert.Equal(4, PcmFloatConverter.FrameCountFor(32, Pcm32));
    }

    [Fact]
    public void The_frame_count_reads_through_an_extensible_format()
    {
        // Sixteen-bit stereo again, this time wrapped the way WASAPI wraps it. The size has to come
        // from the subformat's encoding, which for this constructor is the PCM one.
        WaveFormatExtensible format = new(48_000, 16, 2, 16);

        Assert.Equal(8, PcmFloatConverter.FrameCountFor(32, format));
    }

    [Fact]
    public void A_bit_depth_that_is_not_whole_bytes_has_no_frame_count()
    {
        // Four bits a sample is not a depth this path can size a frame from, and dividing by it
        // would give a frame size of zero and then a division by that.
        WaveFormat format = new(48_000, 4, 2);

        Assert.Throws<ArgumentException>(() => PcmFloatConverter.FrameCountFor(32, format));
    }
}
