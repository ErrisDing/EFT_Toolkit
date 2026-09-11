namespace EftToolkit.Display.Gamma;

/// <summary>
/// An immutable 3-by-256 16-bit RGB gamma ramp, the representation the Win32 gamma-ramp API uses.
/// Channels are copied on construction so a caller cannot mutate a ramp after it has been recorded
/// as a recovery snapshot, and are exposed as read-only views for the same reason.
/// </summary>
public sealed class GammaRamp : IEquatable<GammaRamp>
{
    public const int ChannelLength = 256;

    private readonly ushort[] _red;
    private readonly ushort[] _green;
    private readonly ushort[] _blue;

    public GammaRamp(ReadOnlySpan<ushort> red, ReadOnlySpan<ushort> green, ReadOnlySpan<ushort> blue)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(red.Length, ChannelLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(green.Length, ChannelLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(blue.Length, ChannelLength);

        _red = red.ToArray();
        _green = green.ToArray();
        _blue = blue.ToArray();
    }

    public IReadOnlyList<ushort> Red => _red;

    public IReadOnlyList<ushort> Green => _green;

    public IReadOnlyList<ushort> Blue => _blue;

    /// <summary>The neutral ramp: index <c>i</c> maps to <c>i * 257</c>, spanning the full 16-bit range.</summary>
    public static GammaRamp CreateIdentity() => new(IdentityChannel(), IdentityChannel(), IdentityChannel());

    public bool Equals(GammaRamp? other) =>
        other is not null
        && _red.AsSpan().SequenceEqual(other._red)
        && _green.AsSpan().SequenceEqual(other._green)
        && _blue.AsSpan().SequenceEqual(other._blue);

    public override bool Equals(object? obj) => obj is GammaRamp other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (ushort value in _red)
        {
            hash.Add(value);
        }

        foreach (ushort value in _green)
        {
            hash.Add(value);
        }

        foreach (ushort value in _blue)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static ushort[] IdentityChannel()
    {
        ushort[] channel = new ushort[ChannelLength];
        for (int index = 0; index < ChannelLength; index++)
        {
            channel[index] = (ushort)(index * 257);
        }

        return channel;
    }
}
