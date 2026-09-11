using System.Security.Cryptography;

namespace EftToolkit.Display.Gamma;

/// <summary>
/// Stable identity for a ramp's contents, used to tell "the ramp we wrote" apart from "a ramp
/// someone else wrote" when deciding whether a crash-recovery snapshot is still safe to apply.
/// </summary>
public static class GammaRampFingerprint
{
    public static string Compute(GammaRamp ramp)
    {
        ArgumentNullException.ThrowIfNull(ramp);

        return Convert.ToHexString(SHA256.HashData(GammaRampCodec.ToBytes(ramp)));
    }
}
