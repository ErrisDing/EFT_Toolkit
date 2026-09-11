using EftToolkit.Core.Modules;

namespace EftToolkit.Audio;

/// <summary>
/// Everything the panel needs to describe the audio path: the module's lifecycle status, what the
/// route is doing right now, and any warning worth putting in front of the user.
/// </summary>
/// <param name="Module">The lifecycle status, shared with the display module.</param>
/// <param name="IsTargetRunning">Whether the routed application is currently running.</param>
/// <param name="IsRouteOpen">Whether a stream is open and carrying audio.</param>
/// <param name="HasUnrelatedSessions">
/// Whether something other than the routed application is playing into the virtual render endpoint,
/// which means its audio is being limited as well.
/// </param>
/// <param name="WarningCode">
/// A stable identifier for a condition that does not stop the audio, or null when there is none.
/// Never parse <see cref="WarningMessage"/> to decide anything.
/// </param>
/// <param name="WarningMessage">A sentence describing the warning, for display only.</param>
public sealed record AudioModuleStatus(
    ModuleStatus Module,
    bool IsTargetRunning,
    bool IsRouteOpen,
    bool HasUnrelatedSessions,
    string? WarningCode,
    string? WarningMessage);

/// <summary>
/// The reasons the audio module stops rather than waits. These are stable identifiers: the panel
/// and the log group on them, and the wording of a message never carries a behavior.
/// </summary>
public static class AudioModuleErrorCodes
{
    /// <summary>The configured virtual render endpoint is not present, active, and usable.</summary>
    public const string MissingVirtualRender = "audio.module.missingVirtualRender";

    /// <summary>The configured virtual capture endpoint is not present, active, and usable.</summary>
    public const string MissingVirtualCapture = "audio.module.missingVirtualCapture";

    /// <summary>The configured playback endpoint is not present, active, and usable.</summary>
    public const string MissingPhysicalRender = "audio.module.missingPhysicalRender";

    /// <summary>An endpoint exists but is not a stereo format at a supported sample rate.</summary>
    public const string UnsupportedFormat = "audio.module.unsupportedFormat";

    /// <summary>The shared-mode endpoints could not be opened.</summary>
    public const string SharedModeOpenFailed = "audio.module.sharedModeOpenFailed";

    /// <summary>An open stream reported a failure of its own.</summary>
    public const string StreamFaulted = "audio.module.streamFaulted";

    /// <summary>The routed application is not running, so there is nothing to process.</summary>
    public const string TargetNotRunning = "audio.module.targetNotRunning";

    /// <summary>The selected profile is not in the configuration.</summary>
    public const string MissingProfile = "audio.module.missingProfile";

    /// <summary>The profile does not describe a route that can be opened at all.</summary>
    public const string InvalidRoute = "audio.module.invalidRoute";

    /// <summary>A transition failed in a way that was not anticipated. The exception is in the log.</summary>
    public const string TransitionFailed = "audio.module.transitionFailed";
}

/// <summary>Conditions that are worth reporting but do not stop the audio.</summary>
public static class AudioModuleWarningCodes
{
    /// <summary>
    /// The toolkit's own buffers add more delay than the target. Audio keeps flowing: a delay the
    /// user can feel is a reason to say so, not a reason to cut them off.
    /// </summary>
    public const string LatencyTargetExceeded = "audio.module.latencyTargetExceeded";

    /// <summary>Another application is playing into the routed endpoint and is being processed too.</summary>
    public const string UnrelatedSessions = "audio.module.unrelatedSessions";
}
