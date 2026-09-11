using System.Buffers.Binary;
using System.Security.Cryptography;
using EftToolkit.Core.Configuration;
using EftToolkit.Display.Gamma;

namespace EftToolkit.Tests.Display;

public class GammaRampFingerprintTests
{
    [Fact]
    public void Compute_hashes_the_1536_channel_bytes_little_endian_in_rgb_order()
    {
        GammaRamp ramp = GammaRamp.CreateIdentity();

        string expected = ReferenceFingerprint(ramp);

        Assert.Equal(expected, GammaRampFingerprint.Compute(ramp));
    }

    [Fact]
    public void Compute_returns_an_uppercase_sixty_four_character_hex_string()
    {
        string fingerprint = GammaRampFingerprint.Compute(GammaRamp.CreateIdentity());

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, character => Assert.True(
            char.IsAsciiDigit(character) || (character >= 'A' && character <= 'F'),
            $"'{character}' is not an uppercase hexadecimal digit"));
    }

    [Fact]
    public void Compute_is_stable_for_equal_ramps()
    {
        Assert.Equal(
            GammaRampFingerprint.Compute(GammaRamp.CreateIdentity()),
            GammaRampFingerprint.Compute(GammaRamp.CreateIdentity()));
    }

    [Fact]
    public void Compute_changes_when_a_single_channel_entry_changes()
    {
        GammaRamp identity = GammaRamp.CreateIdentity();
        ushort[] altered = Enumerable.Range(0, 256).Select(index => (ushort)(index * 257)).ToArray();
        altered[200] += 1;

        Assert.NotEqual(
            GammaRampFingerprint.Compute(identity),
            GammaRampFingerprint.Compute(new GammaRamp(altered, identity.Green.ToArray(), identity.Blue.ToArray())));
    }

    [Fact]
    public void Compute_distinguishes_channel_order()
    {
        ushort[] low = Enumerable.Repeat((ushort)1000, 256).ToArray();
        ushort[] high = Enumerable.Repeat((ushort)60000, 256).ToArray();

        Assert.NotEqual(
            GammaRampFingerprint.Compute(new GammaRamp(low, high, low)),
            GammaRampFingerprint.Compute(new GammaRamp(high, low, low)));
    }

    [Fact]
    public void Compute_changes_when_the_preset_changes()
    {
        GammaRamp original = GammaRamp.CreateIdentity();

        Assert.NotEqual(
            GammaRampFingerprint.Compute(GammaRampComposer.Compose(original, new DisplayPresetOptions(1.15, 0.00, 1.00))),
            GammaRampFingerprint.Compute(GammaRampComposer.Compose(original, new DisplayPresetOptions(1.55, 0.02, 1.00))));
    }

    [Fact]
    public void Compute_matches_an_independently_computed_reference_for_a_known_ramp()
    {
        ushort[] channel = Enumerable.Range(0, 256).Select(index => (ushort)(65535 - index)).ToArray();
        GammaRamp ramp = new(channel, channel, channel);

        Assert.Equal(ReferenceFingerprint(ramp), GammaRampFingerprint.Compute(ramp));
    }

    /// <summary>
    /// Independent implementation of the specified layout: SHA-256 over three 256-entry channels of
    /// little-endian 16-bit values concatenated in red, green, blue order, rendered as uppercase hex.
    /// </summary>
    private static string ReferenceFingerprint(GammaRamp ramp)
    {
        byte[] bytes = new byte[3 * GammaRamp.ChannelLength * sizeof(ushort)];
        int offset = 0;

        foreach (IReadOnlyList<ushort> channel in new[] { ramp.Red, ramp.Green, ramp.Blue })
        {
            foreach (ushort value in channel)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)), value);
                offset += sizeof(ushort);
            }
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
