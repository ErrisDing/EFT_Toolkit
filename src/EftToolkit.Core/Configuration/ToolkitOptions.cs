namespace EftToolkit.Core.Configuration;

/// <summary>User-tunable parameters for one gamma-ramp preset. See <see cref="DisplayPresetOptions"/> bounds in <see cref="OptionsValidator"/>.</summary>
public sealed record DisplayPresetOptions(double Gamma, double ShadowLift, double OutputCeiling);

public sealed record DisplayOptions(
    bool Enabled,
    IReadOnlyList<string> SelectedDisplayIds,
    DisplayPresetOptions Low,
    DisplayPresetOptions Medium,
    DisplayPresetOptions High)
{
    // The compiler-generated record equality compares collection members by reference, which would
    // make two identical configurations unequal. Configuration is compared after every load, so
    // these members are compared element by element instead.
    public bool Equals(DisplayOptions? other) =>
        other is not null
        && Enabled == other.Enabled
        && Sequence.Equal(SelectedDisplayIds, other.SelectedDisplayIds)
        && Low == other.Low
        && Medium == other.Medium
        && High == other.High;

    public override int GetHashCode() => HashCode.Combine(Enabled, Low, Medium, High);
}

/// <summary>
/// Limiter parameters. Only gain, threshold, and ceiling are surfaced in the simple panel;
/// the remaining values stay as versioned advanced configuration for the first release.
/// </summary>
public sealed record AudioLimiterOptions(
    double InputGainDb,
    double ThresholdDbFs,
    double Ratio,
    double KneeDb,
    double LookAheadMs,
    double AttackMs,
    double ReleaseMs,
    double CeilingDbFs);

/// <summary>
/// One routable application profile. The virtual endpoints are the two sides of the
/// separately installed virtual cable; the toolkit never installs or updates that driver.
/// </summary>
public sealed record AudioProfileOptions(
    string Id,
    string DisplayName,
    string ExecutableName,
    string? VirtualRenderEndpointId,
    string? VirtualCaptureEndpointId,
    string? PhysicalRenderEndpointId);

public sealed record AudioOptions(
    bool Enabled,
    string ActiveProfileId,
    AudioLimiterOptions Limiter,
    IReadOnlyList<AudioProfileOptions> Profiles)
{
    public bool Equals(AudioOptions? other) =>
        other is not null
        && Enabled == other.Enabled
        && string.Equals(ActiveProfileId, other.ActiveProfileId, StringComparison.Ordinal)
        && Limiter == other.Limiter
        && Sequence.Equal(Profiles, other.Profiles);

    public override int GetHashCode() => HashCode.Combine(Enabled, ActiveProfileId, Limiter);
}

/// <summary>Element-wise comparison for the collection members of the configuration records.</summary>
internal static class Sequence
{
    internal static bool Equal<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record ToolkitOptions(int SchemaVersion, DisplayOptions Display, AudioOptions Audio)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Profile selected when no other profile is configured or when a persisted profile is unusable.</summary>
    public const string DefaultAudioProfileId = "eft";

    /// <summary>
    /// Conservative starting point. Display enhancement is off, audio is off, and every value
    /// matches the approved defaults in the design document.
    /// </summary>
    public static ToolkitOptions CreateDefault() => new(
        CurrentSchemaVersion,
        new DisplayOptions(
            Enabled: false,
            SelectedDisplayIds: Array.Empty<string>(),
            Low: new DisplayPresetOptions(1.15, 0.00, 1.00),
            Medium: new DisplayPresetOptions(1.35, 0.01, 1.00),
            High: new DisplayPresetOptions(1.55, 0.02, 1.00)),
        new AudioOptions(
            Enabled: false,
            ActiveProfileId: DefaultAudioProfileId,
            Limiter: new AudioLimiterOptions(
                InputGainDb: 12.0,
                ThresholdDbFs: -12.0,
                Ratio: 10.0,
                KneeDb: 6.0,
                LookAheadMs: 5.0,
                AttackMs: 1.0,
                ReleaseMs: 100.0,
                CeilingDbFs: -1.0),
            Profiles: new[]
            {
                new AudioProfileOptions(
                    Id: DefaultAudioProfileId,
                    DisplayName: "Escape from Tarkov",
                    ExecutableName: "EscapeFromTarkov",
                    VirtualRenderEndpointId: null,
                    VirtualCaptureEndpointId: null,
                    PhysicalRenderEndpointId: null),
            }));
}
