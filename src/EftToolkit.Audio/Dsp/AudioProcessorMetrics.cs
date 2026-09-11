namespace EftToolkit.Audio.Dsp;

/// <summary>
/// A snapshot of what the limiter did to the last block it processed. Gain reduction is reported
/// as a positive number of decibels, because that is how a meter is read.
/// </summary>
/// <remarks>
/// <para>
/// Gain reduction is the deepest reduction reached anywhere inside the block, not the value the
/// block ended on. A meter built on this therefore shows the worst moment of each block, which is
/// what a peak meter is for, and a block that starts deep in reduction and recovers within it
/// still reports the deep value.
/// </para>
/// <para>
/// A struct, so publishing a measurement does not allocate on the audio thread.
/// </para>
/// </remarks>
public readonly record struct AudioProcessorMetrics(
    double InputPeakDbFs,
    double OutputPeakDbFs,
    double GainReductionDb)
{
    public static readonly AudioProcessorMetrics Silent = new(
        AudioMath.LinearToDb(AudioMath.Silence),
        AudioMath.LinearToDb(AudioMath.Silence),
        0.0);
}
