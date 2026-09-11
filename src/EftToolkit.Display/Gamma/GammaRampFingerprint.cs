using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EftToolkit.Display.Gamma;

/// <summary>
/// Stable identity for a ramp's contents, used to tell "the ramp we wrote" apart from "a ramp
/// someone else wrote" when deciding whether a crash-recovery snapshot is still safe to apply.
/// </summary>
public static class GammaRampFingerprint
{
    private const int ByteLength = 3 * GammaRamp.ChannelLength * sizeof(ushort);

    public static string Compute(GammaRamp ramp)
    {
        ArgumentNullException.ThrowIfNull(ramp);

        byte[] bytes = new byte[ByteLength];
        int offset = 0;

        offset = WriteChannel(bytes, offset, ramp.Red);
        offset = WriteChannel(bytes, offset, ramp.Green);
        WriteChannel(bytes, offset, ramp.Blue);

        return Convert.ToHexString(SHA256.HashData(bytes));
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
}
