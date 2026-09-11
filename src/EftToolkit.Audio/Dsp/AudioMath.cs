namespace EftToolkit.Audio.Dsp;

/// <summary>Decibel conversions shared by the DSP, so the two modules agree on what 0 dB means.</summary>
public static class AudioMath
{
    /// <summary>
    /// The floor for a level measurement. Silence is not -infinity dB, it is a very small number,
    /// which keeps logarithms finite and a detector from being handed a zero to divide by.
    /// </summary>
    public const double Silence = 1e-12;

    /// <summary>Full scale is 1.0, so this is the amplitude that clips.</summary>
    public const double FullScaleDbFs = 0.0;

    public static double DbToLinear(double decibels) => Math.Pow(10.0, decibels / 20.0);

    public static double LinearToDb(double linear) => 20.0 * Math.Log10(linear);

    /// <summary>
    /// Level in dBFS, with a floor. Use this on any value that may legitimately be zero.
    /// </summary>
    public static double LinearToDbSafe(double linear) =>
        linear <= Silence ? LinearToDb(Silence) : LinearToDb(linear);

    /// <summary>
    /// Replaces a non-finite sample with silence. A NaN reaching a detector would poison every
    /// gain derived from it, and an infinity would survive a multiply and be replayed from a delay
    /// line long after the sample that caused it was gone.
    /// </summary>
    public static float Sanitize(float sample) => float.IsFinite(sample) ? sample : 0.0f;
}
