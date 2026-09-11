using System.Buffers.Binary;
using EftToolkit.Audio.Dsp;
using NAudio.Wave;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// Turns the bytes WASAPI delivers into interleaved stereo floats the limiter can process.
/// </summary>
/// <remarks>
/// <para>
/// Every format is scaled by a power of two, so the conversion is exact: full-scale negative lands
/// on -1 and the largest positive sample lands one step short of 1. Dividing by the largest
/// positive value instead would push -1 past the ceiling.
/// </para>
/// <para>
/// This is the one place a non-finite sample can enter the pipeline, so it is the place they are
/// removed.
/// </para>
/// </remarks>
public static class PcmFloatConverter
{
    /// <summary>Everything in this path is stereo, so a frame is two samples.</summary>
    public const int ChannelCount = 2;

    /// <summary>
    /// Converts as many whole stereo frames as both buffers allow and returns the number of samples
    /// written, which is always even. A trailing partial frame is ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The format is not stereo.</exception>
    /// <exception cref="NotSupportedException">The format is not 16, 24, or 32-bit PCM, or 32-bit float.</exception>
    public static int ConvertToFloat(ReadOnlySpan<byte> source, WaveFormat format, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(format);

        WaveFormat resolved = Resolve(format);

        if (resolved.Channels != ChannelCount)
        {
            throw new ArgumentException(
                $"Only stereo audio is converted; this format has {resolved.Channels} channels.",
                nameof(format));
        }

        int bytesPerSample = resolved.BitsPerSample / 8;

        if (bytesPerSample <= 0)
        {
            throw new ArgumentException(
                $"A bit depth of {resolved.BitsPerSample} does not describe whole bytes.",
                nameof(format));
        }

        int frameBytes = bytesPerSample * ChannelCount;
        int frames = Math.Min(source.Length / frameBytes, destination.Length / ChannelCount);

        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = frame * frameBytes;

            for (int channel = 0; channel < ChannelCount; channel++)
            {
                ReadOnlySpan<byte> sample = source.Slice(frameOffset + (channel * bytesPerSample), bytesPerSample);
                destination[(frame * ChannelCount) + channel] = ReadSample(sample, resolved);
            }
        }

        return frames * ChannelCount;
    }

    /// <summary>
    /// How many whole frames a block of bytes holds in this format. The capture path sizes its
    /// scratch buffer with this rather than over-allocating, so that a block larger than the one the
    /// endpoint was opened with is still converted in full instead of being quietly truncated.
    /// </summary>
    /// <exception cref="ArgumentException">The bit depth does not describe whole bytes.</exception>
    public static int FrameCountFor(int byteCount, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        WaveFormat resolved = Resolve(format);
        int bytesPerSample = resolved.BitsPerSample / 8;

        if (bytesPerSample <= 0)
        {
            throw new ArgumentException(
                $"A bit depth of {resolved.BitsPerSample} does not describe whole bytes.",
                nameof(format));
        }

        int frameBytes = bytesPerSample * resolved.Channels;

        return frameBytes > 0 && byteCount > 0 ? byteCount / frameBytes : 0;
    }

    /// <summary>
    /// WASAPI hands shared-mode streams a WAVE_FORMAT_EXTENSIBLE format, where the real encoding is
    /// in the subformat rather than the encoding field. Anything that is not extensible, or whose
    /// subformat is unknown, is passed through and refused by the encoding check instead.
    /// </summary>
    private static WaveFormat Resolve(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : format;

    private static float ReadSample(ReadOnlySpan<byte> sample, WaveFormat format) =>
        format.Encoding switch
        {
            WaveFormatEncoding.Pcm => ReadPcm(sample, format.BitsPerSample),
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 =>
                AudioMath.Sanitize(BinaryPrimitives.ReadSingleLittleEndian(sample)),
            _ => throw new NotSupportedException(
                $"A {format.BitsPerSample}-bit {format.Encoding} sample is not converted."),
        };

    private static float ReadPcm(ReadOnlySpan<byte> sample, int bitsPerSample) =>
        bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0f,
            24 => ReadInt24LittleEndian(sample) / 8388608.0f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0f,
            _ => throw new NotSupportedException($"{bitsPerSample}-bit PCM is not converted."),
        };

    /// <summary>
    /// A packed 24-bit sample, sign-extended. The double shift is what carries bit 23 into all the
    /// bits above it, so the most negative sample reads as -8388608 rather than as its positive
    /// complement.
    /// </summary>
    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> sample)
    {
        int value = sample[0] | (sample[1] << 8) | (sample[2] << 16);

        return (value << 8) >> 8;
    }
}
