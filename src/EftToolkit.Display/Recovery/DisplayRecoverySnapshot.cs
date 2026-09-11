using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EftToolkit.Display.Gamma;

namespace EftToolkit.Display.Recovery;

/// <summary>
/// One display's captured original ramp, held so a crash can be undone on the next launch.
/// <see cref="LastWrittenFingerprint"/> is what makes the undo safe: a restore only happens while
/// the display still holds the ramp the toolkit last wrote, so a change made by anyone else is
/// never overwritten.
/// </summary>
public sealed record DisplayRecoveryEntry(
    string StableId,
    string OriginalRampBase64,
    string LastWrittenFingerprint,
    DateTimeOffset CapturedAtUtc,
    string Checksum)
{
    /// <summary>Records a ramp this toolkit captured, together with the fingerprint of its last write.</summary>
    public static DisplayRecoveryEntry Create(
        string stableId,
        GammaRamp originalRamp,
        string lastWrittenFingerprint,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentNullException.ThrowIfNull(originalRamp);
        ArgumentNullException.ThrowIfNull(lastWrittenFingerprint);

        string base64 = GammaRampCodec.ToBase64(originalRamp);

        return new DisplayRecoveryEntry(
            stableId,
            base64,
            lastWrittenFingerprint,
            capturedAtUtc,
            ComputeChecksum(stableId, originalRamp, lastWrittenFingerprint, capturedAtUtc));
    }

    /// <summary>
    /// Returns the original ramp only when the entry is internally consistent: the checksum must
    /// match and the stored ramp must decode to a well-formed ramp. Anything else is treated as
    /// absent rather than as a ramp worth writing to a display.
    /// </summary>
    public bool TryReadOriginalRamp([NotNullWhen(true)] out GammaRamp? ramp)
    {
        ramp = null;

        if (!GammaRampCodec.TryFromBase64(OriginalRampBase64, out GammaRamp? decoded))
        {
            return false;
        }

        // An integrity check rather than a secret comparison, so an ordinary ordinal compare is
        // right here; a persisted file can also carry a null checksum, which this treats as absent.
        if (!string.Equals(
                ComputeChecksum(StableId, decoded, LastWrittenFingerprint, CapturedAtUtc),
                Checksum,
                StringComparison.Ordinal))
        {
            return false;
        }

        ramp = decoded;
        return true;
    }

    /// <summary>
    /// Hashes stable id, ramp bytes, fingerprint, and timestamp. The ramp is a fixed length, but the
    /// stable id and the timestamp are not, so every field is length-prefixed: otherwise a longer id
    /// and a shorter ramp could produce the same byte stream as the original pair.
    /// </summary>
    private static string ComputeChecksum(
        string stableId,
        GammaRamp ramp,
        string lastWrittenFingerprint,
        DateTimeOffset capturedAtUtc)
    {
        using MemoryStream buffer = new();

        WriteBlock(buffer, Encoding.UTF8.GetBytes(stableId));
        WriteBlock(buffer, GammaRampCodec.ToBytes(ramp));
        WriteBlock(buffer, Encoding.UTF8.GetBytes(lastWrittenFingerprint));
        WriteBlock(buffer, Encoding.UTF8.GetBytes(capturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteBlock(Stream destination, byte[] block)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, block.Length);

        destination.Write(length);
        destination.Write(block);
    }
}

/// <summary>The whole recovery file: every display this toolkit has captured and not yet restored.</summary>
public sealed record DisplayRecoverySnapshot(int SchemaVersion, IReadOnlyList<DisplayRecoveryEntry> Displays)
{
    public const int CurrentSchemaVersion = 1;

    public static DisplayRecoverySnapshot Empty { get; } = new(CurrentSchemaVersion, []);
}
