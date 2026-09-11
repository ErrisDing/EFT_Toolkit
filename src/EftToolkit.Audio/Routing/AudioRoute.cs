namespace EftToolkit.Audio.Routing;

/// <summary>
/// A route the toolkit can open: the application's audio leaves through a virtual render endpoint,
/// comes back on its virtual capture endpoint, and is played out of a real one.
/// </summary>
public sealed record AudioRoute(
    string VirtualRenderEndpointId,
    string VirtualCaptureEndpointId,
    string PhysicalRenderEndpointId,
    string ExecutableName);

/// <summary>The outcome of checking a configured profile against the endpoints that exist.</summary>
/// <remarks>
/// <see cref="ErrorCode"/> is a stable identifier rather than a message, so the panel can say
/// something specific and a log can be grouped without depending on wording.
/// </remarks>
public sealed record AudioRouteValidation(bool IsValid, AudioRoute? Route, string? ErrorCode)
{
    internal static AudioRouteValidation Valid(AudioRoute route) => new(true, route, null);

    internal static AudioRouteValidation Invalid(string errorCode) => new(false, null, errorCode);
}

/// <summary>The reasons a profile can be refused, one per way of getting it wrong.</summary>
public static class AudioRouteErrorCodes
{
    /// <summary>The executable name is empty or carries a directory.</summary>
    public const string InvalidExecutableName = "audio.route.invalidExecutableName";

    /// <summary>One of the three endpoints has not been chosen.</summary>
    public const string MissingEndpointId = "audio.route.missingEndpointId";

    /// <summary>A chosen endpoint is not present on the machine.</summary>
    public const string EndpointNotFound = "audio.route.endpointNotFound";

    /// <summary>A chosen endpoint is present but disabled, unplugged, or otherwise unavailable.</summary>
    public const string EndpointNotActive = "audio.route.endpointNotActive";

    /// <summary>One of the endpoints is the wrong kind: a render chosen where a capture is needed, or the reverse.</summary>
    public const string WrongDataFlow = "audio.route.wrongDataFlow";

    /// <summary>The virtual and physical render endpoints are the same device, which is a loop.</summary>
    public const string SameRenderEndpoint = "audio.route.sameRenderEndpoint";

    /// <summary>An endpoint is not stereo.</summary>
    public const string UnsupportedChannelCount = "audio.route.unsupportedChannelCount";

    /// <summary>An endpoint runs at a sample rate the first release does not support.</summary>
    public const string UnsupportedSampleRate = "audio.route.unsupportedSampleRate";
}
