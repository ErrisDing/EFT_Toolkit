namespace EftToolkit.Core.Configuration;

/// <summary>
/// Sanitizes loaded configuration. An out-of-range value is replaced with its approved default
/// rather than being clamped to the nearest bound: a clamped value is one the user never chose,
/// and silently keeping half of a malformed preset is harder to diagnose than rejecting it.
/// A configuration that fails to parse at all is preserved on disk by the options store.
/// </summary>
public static class OptionsValidator
{
    public const double GammaMinimum = 0.50;
    public const double GammaMaximum = 3.00;
    public const double ShadowLiftMinimum = 0.00;
    public const double ShadowLiftMaximum = 0.20;
    public const double OutputCeilingMinimum = 0.50;
    public const double OutputCeilingMaximum = 1.00;

    public const double InputGainDbMinimum = 0.0;
    public const double InputGainDbMaximum = 24.0;
    public const double ThresholdDbFsMinimum = -24.0;
    public const double ThresholdDbFsMaximum = -1.0;
    public const double RatioMinimum = 1.0;
    public const double RatioMaximum = 50.0;
    public const double KneeDbMinimum = 0.0;
    public const double KneeDbMaximum = 12.0;
    public const double LookAheadMsMinimum = 0.0;
    public const double LookAheadMsMaximum = 20.0;
    public const double AttackMsMinimum = 0.1;
    public const double AttackMsMaximum = 50.0;
    public const double ReleaseMsMinimum = 10.0;
    public const double ReleaseMsMaximum = 1000.0;
    public const double CeilingDbFsMinimum = -12.0;
    public const double CeilingDbFsMaximum = -0.1;

    /// <summary>
    /// Returns a sanitized copy. Null arguments, null collections, and non-finite numbers are
    /// treated as absent rather than fatal, so a partially written file still yields a usable app.
    /// </summary>
    public static ToolkitOptions Validate(ToolkitOptions? options)
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        if (options is null)
        {
            return defaults;
        }

        return new ToolkitOptions(
            options.SchemaVersion,
            ValidateDisplay(options.Display, defaults.Display),
            ValidateAudio(options.Audio, defaults.Audio));
    }

    private static DisplayOptions ValidateDisplay(DisplayOptions? display, DisplayOptions fallback)
    {
        if (display is null)
        {
            return fallback;
        }

        return new DisplayOptions(
            display.Enabled,
            SanitizeIdentifiers(display.SelectedDisplayIds),
            ValidatePreset(display.Low, fallback.Low),
            ValidatePreset(display.Medium, fallback.Medium),
            ValidatePreset(display.High, fallback.High));
    }

    private static DisplayPresetOptions ValidatePreset(DisplayPresetOptions? preset, DisplayPresetOptions fallback)
    {
        if (preset is null)
        {
            return fallback;
        }

        return new DisplayPresetOptions(
            InRange(preset.Gamma, GammaMinimum, GammaMaximum) ? preset.Gamma : fallback.Gamma,
            InRange(preset.ShadowLift, ShadowLiftMinimum, ShadowLiftMaximum) ? preset.ShadowLift : fallback.ShadowLift,
            InRange(preset.OutputCeiling, OutputCeilingMinimum, OutputCeilingMaximum) ? preset.OutputCeiling : fallback.OutputCeiling);
    }

    private static AudioOptions ValidateAudio(AudioOptions? audio, AudioOptions fallback)
    {
        if (audio is null)
        {
            return fallback;
        }

        List<AudioProfileOptions> profiles = SanitizeProfiles(audio.Profiles);
        if (profiles.Count == 0)
        {
            profiles.AddRange(fallback.Profiles);
        }

        return new AudioOptions(
            audio.Enabled,
            ResolveActiveProfileId(audio.ActiveProfileId, profiles),
            ValidateLimiter(audio.Limiter, fallback.Limiter),
            profiles);
    }

    private static AudioLimiterOptions ValidateLimiter(AudioLimiterOptions? limiter, AudioLimiterOptions fallback)
    {
        if (limiter is null)
        {
            return fallback;
        }

        return new AudioLimiterOptions(
            InRange(limiter.InputGainDb, InputGainDbMinimum, InputGainDbMaximum) ? limiter.InputGainDb : fallback.InputGainDb,
            InRange(limiter.ThresholdDbFs, ThresholdDbFsMinimum, ThresholdDbFsMaximum) ? limiter.ThresholdDbFs : fallback.ThresholdDbFs,
            InRange(limiter.Ratio, RatioMinimum, RatioMaximum) ? limiter.Ratio : fallback.Ratio,
            InRange(limiter.KneeDb, KneeDbMinimum, KneeDbMaximum) ? limiter.KneeDb : fallback.KneeDb,
            InRange(limiter.LookAheadMs, LookAheadMsMinimum, LookAheadMsMaximum) ? limiter.LookAheadMs : fallback.LookAheadMs,
            InRange(limiter.AttackMs, AttackMsMinimum, AttackMsMaximum) ? limiter.AttackMs : fallback.AttackMs,
            InRange(limiter.ReleaseMs, ReleaseMsMinimum, ReleaseMsMaximum) ? limiter.ReleaseMs : fallback.ReleaseMs,
            InRange(limiter.CeilingDbFs, CeilingDbFsMinimum, CeilingDbFsMaximum) ? limiter.CeilingDbFs : fallback.CeilingDbFs);
    }

    /// <summary>
    /// Drops blank and duplicate profile identifiers. Identifiers are compared ignoring case
    /// because they are hand-edited configuration keys, where "EFT" and "eft" are a duplicate,
    /// not a distinction.
    /// </summary>
    private static List<AudioProfileOptions> SanitizeProfiles(IReadOnlyList<AudioProfileOptions>? profiles)
    {
        List<AudioProfileOptions> result = new();
        if (profiles is null)
        {
            return result;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (AudioProfileOptions? profile in profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
            {
                continue;
            }

            if (seen.Add(profile.Id))
            {
                result.Add(profile);
            }
        }

        return result;
    }

    private static string ResolveActiveProfileId(string? activeProfileId, List<AudioProfileOptions> profiles)
    {
        if (!string.IsNullOrWhiteSpace(activeProfileId))
        {
            foreach (AudioProfileOptions profile in profiles)
            {
                if (string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    return profile.Id;
                }
            }
        }

        return profiles[0].Id;
    }

    private static IReadOnlyList<string> SanitizeIdentifiers(IReadOnlyList<string>? identifiers)
    {
        List<string> result = new();
        if (identifiers is null)
        {
            return result;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? identifier in identifiers)
        {
            if (!string.IsNullOrWhiteSpace(identifier) && seen.Add(identifier))
            {
                result.Add(identifier);
            }
        }

        return result;
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
