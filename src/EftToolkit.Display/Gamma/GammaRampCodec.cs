using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace EftToolkit.Display.Gamma;

/// <summary>
/// The single definition of a ramp's byte form: three channels of
/// <see cref="GammaRamp.ChannelLength"/> little-endian sixteen-bit entries, red, then green, then
/// blue. Both the fingerprint and the recovery snapshot are built from this layout, so it lives in
/// one place rather than being restated wherever a ramp has to be persisted or hashed.
/// </summary>
public static class GammaRampCodec
{
    public const int ChannelByteLength = GammaRamp.ChannelLength * sizeof(ushort);

    public const int ByteLength = 3 * ChannelByteLength;

    public static byte[] ToBytes(GammaRamp ramp)
    {
        ArgumentNullException.ThrowIfNull(ramp);

        byte[] bytes = new byte[ByteLength];
        int offset = 0;

        offset = WriteChannel(bytes, offset, ramp.Red);
        offset = WriteChannel(bytes, offset, ramp.Green);
        WriteChannel(bytes, offset, ramp.Blue);

        return bytes;
    }

    public static GammaRamp FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
        {
            throw new ArgumentException(
                $"A ramp must be exactly {ByteLength} bytes, but {bytes.Length} were supplied.",
                nameof(bytes));
        }

        return new GammaRamp(
            ReadChannel(bytes, 0),
            ReadChannel(bytes, ChannelByteLength),
            ReadChannel(bytes, 2 * ChannelByteLength));
    }

    public static string ToBase64(GammaRamp ramp) => Convert.ToBase64String(ToBytes(ramp));

    /// <summary>
    /// Decodes a persisted ramp. Returns <see langword="false"/> rather than throwing, because the
    /// input comes from a file that may have been edited or truncated.
    /// </summary>
    public static bool TryFromBase64(string? base64, [NotNullWhen(true)] out GammaRamp? ramp)
    {
        ramp = null;

        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[ByteLength];

        if (!Convert.TryFromBase64String(base64, bytes, out int written) || written != ByteLength)
        {
            return false;
        }

        ramp = FromBytes(bytes);
        return true;
    }

    private static int WriteChannel(byte[] destination, int offset, IReadOnlyList<ushort> channel)
    {
        for (int index = 0; index < channel.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, sizeof(ushort)), channel[index]);
            offset += sizeof(ushort);
        }

        return offset;
    }

    private static ushort[] ReadChannel(ReadOnlySpan<byte> source, int offset)
    {
        ushort[] channel = new ushort[GammaRamp.ChannelLength];

        for (int index = 0; index < GammaRamp.ChannelLength; index++)
        {
            channel[index] = BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + (index * sizeof(ushort)))..]);
        }

        return channel;
    }
}
