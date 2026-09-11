using EftToolkit.Core.Configuration;

namespace EftToolkit.Tests.Core;

public class OptionsValidatorTests
{
    [Fact]
    public void CreateDefault_uses_approved_display_and_audio_values()
    {
        ToolkitOptions options = ToolkitOptions.CreateDefault();

        Assert.Equal(new DisplayPresetOptions(1.15, 0.00, 1.00), options.Display.Low);
        Assert.Equal(new DisplayPresetOptions(1.35, 0.01, 1.00), options.Display.Medium);
        Assert.Equal(new DisplayPresetOptions(1.55, 0.02, 1.00), options.Display.High);
        Assert.Equal(12.0, options.Audio.Limiter.InputGainDb);
        Assert.Equal(-12.0, options.Audio.Limiter.ThresholdDbFs);
        Assert.Equal(-1.0, options.Audio.Limiter.CeilingDbFs);
        Assert.Equal("EscapeFromTarkov", options.Audio.Profiles.Single().ExecutableName);
    }

    [Fact]
    public void CreateDefault_is_already_valid()
    {
        Assert.Equal(ToolkitOptions.CreateDefault(), OptionsValidator.Validate(ToolkitOptions.CreateDefault()));
    }

    [Fact]
    public void CreateDefault_uses_documented_limiter_values()
    {
        AudioLimiterOptions limiter = ToolkitOptions.CreateDefault().Audio.Limiter;

        Assert.Equal(10.0, limiter.Ratio);
        Assert.Equal(6.0, limiter.KneeDb);
        Assert.Equal(5.0, limiter.LookAheadMs);
        Assert.Equal(1.0, limiter.AttackMs);
        Assert.Equal(100.0, limiter.ReleaseMs);
    }

    [Fact]
    public void Validate_null_returns_defaults()
    {
        Assert.Equal(ToolkitOptions.CreateDefault(), OptionsValidator.Validate(null));
    }

    [Theory]
    [InlineData(0.50, true)]
    [InlineData(1.75, true)]
    [InlineData(3.00, true)]
    [InlineData(0.49, false)]
    [InlineData(3.01, false)]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Validate_enforces_gamma_bounds(double gamma, bool accepted)
    {
        DisplayPresetOptions fallback = ToolkitOptions.CreateDefault().Display.Low;

        DisplayPresetOptions actual = ValidateLow(new DisplayPresetOptions(gamma, 0.00, 1.00));

        Assert.Equal(accepted ? gamma : fallback.Gamma, actual.Gamma);
    }

    [Theory]
    [InlineData(0.00, true)]
    [InlineData(0.10, true)]
    [InlineData(0.20, true)]
    [InlineData(-0.01, false)]
    [InlineData(0.21, false)]
    [InlineData(1.0, false)]
    [InlineData(double.NaN, false)]
    public void Validate_enforces_shadow_lift_bounds(double shadowLift, bool accepted)
    {
        DisplayPresetOptions fallback = ToolkitOptions.CreateDefault().Display.Low;

        DisplayPresetOptions actual = ValidateLow(new DisplayPresetOptions(1.15, shadowLift, 1.00));

        Assert.Equal(accepted ? shadowLift : fallback.ShadowLift, actual.ShadowLift);
    }

    [Theory]
    [InlineData(0.50, true)]
    [InlineData(0.75, true)]
    [InlineData(1.00, true)]
    [InlineData(0.49, false)]
    [InlineData(1.01, false)]
    [InlineData(-0.5, false)]
    [InlineData(double.NaN, false)]
    public void Validate_enforces_output_ceiling_bounds(double ceiling, bool accepted)
    {
        DisplayPresetOptions fallback = ToolkitOptions.CreateDefault().Display.Low;

        DisplayPresetOptions actual = ValidateLow(new DisplayPresetOptions(1.15, 0.00, ceiling));

        Assert.Equal(accepted ? ceiling : fallback.OutputCeiling, actual.OutputCeiling);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(12.0, true)]
    [InlineData(24.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(24.1, false)]
    [InlineData(double.NaN, false)]
    public void Validate_enforces_input_gain_bounds(double gainDb, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { InputGainDb = gainDb });

        Assert.Equal(accepted ? gainDb : fallback.InputGainDb, actual.InputGainDb);
    }

    [Theory]
    [InlineData(-24.0, true)]
    [InlineData(-1.0, true)]
    [InlineData(-24.1, false)]
    [InlineData(-0.9, false)]
    [InlineData(0.0, false)]
    [InlineData(double.NaN, false)]
    public void Validate_enforces_threshold_bounds(double thresholdDbFs, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { ThresholdDbFs = thresholdDbFs });

        Assert.Equal(accepted ? thresholdDbFs : fallback.ThresholdDbFs, actual.ThresholdDbFs);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(50.0, true)]
    [InlineData(0.9, false)]
    [InlineData(50.1, false)]
    [InlineData(double.NaN, false)]
    public void Validate_enforces_ratio_bounds(double ratio, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { Ratio = ratio });

        Assert.Equal(accepted ? ratio : fallback.Ratio, actual.Ratio);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(12.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(12.1, false)]
    public void Validate_enforces_knee_bounds(double kneeDb, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { KneeDb = kneeDb });

        Assert.Equal(accepted ? kneeDb : fallback.KneeDb, actual.KneeDb);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(20.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(20.1, false)]
    public void Validate_enforces_look_ahead_bounds(double lookAheadMs, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { LookAheadMs = lookAheadMs });

        Assert.Equal(accepted ? lookAheadMs : fallback.LookAheadMs, actual.LookAheadMs);
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(50.0, true)]
    [InlineData(0.09, false)]
    [InlineData(50.1, false)]
    public void Validate_enforces_attack_bounds(double attackMs, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { AttackMs = attackMs });

        Assert.Equal(accepted ? attackMs : fallback.AttackMs, actual.AttackMs);
    }

    [Theory]
    [InlineData(10.0, true)]
    [InlineData(1000.0, true)]
    [InlineData(9.9, false)]
    [InlineData(1000.1, false)]
    public void Validate_enforces_release_bounds(double releaseMs, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { ReleaseMs = releaseMs });

        Assert.Equal(accepted ? releaseMs : fallback.ReleaseMs, actual.ReleaseMs);
    }

    [Theory]
    [InlineData(-12.0, true)]
    [InlineData(-0.1, true)]
    [InlineData(-12.1, false)]
    [InlineData(0.0, false)]
    public void Validate_enforces_ceiling_bounds(double ceilingDbFs, bool accepted)
    {
        AudioLimiterOptions fallback = ToolkitOptions.CreateDefault().Audio.Limiter;

        AudioLimiterOptions actual = ValidateLimiter(ToolkitOptions.CreateDefault().Audio.Limiter with { CeilingDbFs = ceilingDbFs });

        Assert.Equal(accepted ? ceilingDbFs : fallback.CeilingDbFs, actual.CeilingDbFs);
    }

    [Fact]
    public void Validate_replaces_only_the_invalid_field_without_clamping()
    {
        AudioLimiterOptions actual = ValidateLimiter(
            ToolkitOptions.CreateDefault().Audio.Limiter with { Ratio = 500.0, KneeDb = 3.0 });

        Assert.Equal(ToolkitOptions.CreateDefault().Audio.Limiter.Ratio, actual.Ratio);
        Assert.Equal(3.0, actual.KneeDb);
    }

    [Fact]
    public void Validate_drops_blank_and_duplicate_profile_ids()
    {
        AudioOptions actual = ValidateAudio(ToolkitOptions.CreateDefault().Audio with
        {
            Profiles =
            [
                new AudioProfileOptions("eft", "Escape from Tarkov", "EscapeFromTarkov", null, null, null),
                new AudioProfileOptions("EFT", "Duplicate", "Other", null, null, null),
                new AudioProfileOptions("   ", "Blank id", "Blank", null, null, null),
                new AudioProfileOptions("discord", "Discord", "Discord", null, null, null),
            ],
        });

        Assert.Equal(["eft", "discord"], actual.Profiles.Select(profile => profile.Id));
    }

    [Fact]
    public void Validate_repairs_active_profile_id_that_does_not_exist()
    {
        AudioOptions actual = ValidateAudio(ToolkitOptions.CreateDefault().Audio with
        {
            ActiveProfileId = "missing",
            Profiles = [new AudioProfileOptions("discord", "Discord", "Discord", null, null, null)],
        });

        Assert.Equal("discord", actual.ActiveProfileId);
    }

    [Fact]
    public void Validate_canonicalises_active_profile_id_casing()
    {
        AudioOptions actual = ValidateAudio(ToolkitOptions.CreateDefault().Audio with
        {
            ActiveProfileId = "DISCORD",
            Profiles =
            [
                new AudioProfileOptions("eft", "Escape from Tarkov", "EscapeFromTarkov", null, null, null),
                new AudioProfileOptions("discord", "Discord", "Discord", null, null, null),
            ],
        });

        Assert.Equal("discord", actual.ActiveProfileId);
    }

    [Fact]
    public void Validate_falls_back_to_default_profile_when_every_profile_is_unusable()
    {
        AudioOptions actual = ValidateAudio(ToolkitOptions.CreateDefault().Audio with
        {
            ActiveProfileId = "eft",
            Profiles = [new AudioProfileOptions("  ", "Blank", "Blank", null, null, null)],
        });

        Assert.Equal("EscapeFromTarkov", actual.Profiles.Single().ExecutableName);
        Assert.Equal(ToolkitOptions.DefaultAudioProfileId, actual.ActiveProfileId);
    }

    [Fact]
    public void Validate_drops_blank_and_duplicate_selected_display_ids()
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        ToolkitOptions actual = OptionsValidator.Validate(defaults with
        {
            Display = defaults.Display with { SelectedDisplayIds = ["a", "A", "", "  ", "b"] },
        });

        Assert.Equal(["a", "b"], actual.Display.SelectedDisplayIds);
    }

    [Fact]
    public void Validate_tolerates_null_collections()
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        ToolkitOptions actual = OptionsValidator.Validate(defaults with
        {
            Display = defaults.Display with { SelectedDisplayIds = null! },
            Audio = defaults.Audio with { Profiles = null! },
        });

        Assert.Empty(actual.Display.SelectedDisplayIds);
        Assert.Equal("EscapeFromTarkov", actual.Audio.Profiles.Single().ExecutableName);
    }

    [Fact]
    public void Validate_preserves_enable_flags_and_schema_version()
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        ToolkitOptions actual = OptionsValidator.Validate(defaults with
        {
            SchemaVersion = 1,
            Display = defaults.Display with { Enabled = true },
            Audio = defaults.Audio with { Enabled = true },
        });

        Assert.True(actual.Display.Enabled);
        Assert.True(actual.Audio.Enabled);
        Assert.Equal(ToolkitOptions.CurrentSchemaVersion, actual.SchemaVersion);
    }

    private static DisplayPresetOptions ValidateLow(DisplayPresetOptions preset)
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        return OptionsValidator.Validate(defaults with { Display = defaults.Display with { Low = preset } }).Display.Low;
    }

    private static AudioLimiterOptions ValidateLimiter(AudioLimiterOptions limiter)
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        return OptionsValidator.Validate(defaults with { Audio = defaults.Audio with { Limiter = limiter } }).Audio.Limiter;
    }

    private static AudioOptions ValidateAudio(AudioOptions audio)
    {
        ToolkitOptions defaults = ToolkitOptions.CreateDefault();
        return OptionsValidator.Validate(defaults with { Audio = audio }).Audio;
    }
}
