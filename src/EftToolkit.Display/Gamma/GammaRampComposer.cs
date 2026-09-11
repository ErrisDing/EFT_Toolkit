using EftToolkit.Core.Configuration;

namespace EftToolkit.Display.Gamma;

/// <summary>
/// Builds an enhanced ramp from a display's captured ramp.
/// <para>
/// The preset is a transfer curve, not a replacement: it maps each normalized output index to a
/// source position inside the original ramp and interpolates there. The enhancement therefore
/// composes with whatever calibration the display already has instead of discarding it for an
/// identity-based ramp.
/// </para>
/// </summary>
public static class GammaRampComposer
{
    public static GammaRamp Compose(GammaRamp original, DisplayPresetOptions options)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfUnusable(options);

        return new GammaRamp(
            ComposeChannel(original.Red, options),
            ComposeChannel(original.Green, options),
            ComposeChannel(original.Blue, options));
    }

    private static void ThrowIfUnusable(DisplayPresetOptions options)
    {
        if (!double.IsFinite(options.Gamma) || options.Gamma <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Gamma,
                "Gamma must be a finite positive value; Compose cannot evaluate a curve without one.");
        }

        if (!double.IsFinite(options.ShadowLift) || !double.IsFinite(options.OutputCeiling))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Shadow lift and output ceiling must be finite; see OptionsValidator for the approved ranges.");
        }
    }

    private static ushort[] ComposeChannel(IReadOnlyList<ushort> channel, DisplayPresetOptions options)
    {
        ushort[] result = new ushort[GammaRamp.ChannelLength];
        double gammaExponent = 1.0 / options.Gamma;

        for (int index = 0; index < GammaRamp.ChannelLength; index++)
        {
            double normalized = index / (double)(GammaRamp.ChannelLength - 1);
            double transformed = (options.ShadowLift + ((1.0 - options.ShadowLift) * Math.Pow(normalized, gammaExponent)));
            double capped = Math.Clamp(transformed, 0.0, options.OutputCeiling);

            double sourcePosition = capped * (GammaRamp.ChannelLength - 1);
            int lower = (int)Math.Floor(sourcePosition);
            int upper = Math.Min(GammaRamp.ChannelLength - 1, lower + 1);
            double fraction = sourcePosition - lower;

            result[index] = (ushort)Math.Clamp(
                Math.Round(channel[lower] + ((channel[upper] - channel[lower]) * fraction)),
                ushort.MinValue,
                ushort.MaxValue);
        }

        // A ramp that dips would invert or band on the panel, so the curve is forced non-decreasing.
        for (int index = 1; index < GammaRamp.ChannelLength; index++)
        {
            result[index] = Math.Max(result[index], result[index - 1]);
        }

        return result;
    }
}
