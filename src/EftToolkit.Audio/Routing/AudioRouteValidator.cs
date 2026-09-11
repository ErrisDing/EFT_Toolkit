using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Processes;
using EftToolkit.Core.Configuration;

namespace EftToolkit.Audio.Routing;

/// <summary>
/// Decides whether a configured profile names a route that can actually be opened, and reports
/// which part of it is wrong when it cannot.
/// </summary>
/// <remarks>
/// Every check here has to happen before a stream is opened. A route that fails at the first audio
/// callback produces silence and a log line rather than an explanation, and the user is left
/// looking at a device list wondering which of the three endpoints is the problem.
/// </remarks>
public static class AudioRouteValidator
{
    /// <summary>The limiter links two channels, so both halves of the route must carry two.</summary>
    public const int StereoChannelCount = 2;

    /// <summary>
    /// The rates the toolkit streams. All three are rates the cable and a game agree on; the rest
    /// are refused rather than resampled, because a rate conversion in this path would add a
    /// latency the user would feel and could not account for.
    /// </summary>
    /// <remarks>
    /// 96 kHz is here for an endpoint the user has already turned up to it — a cable left at 96 kHz
    /// by a previous application is otherwise a route that cannot be enabled at all. The limiter is
    /// sized from this rate, so a rate accepted here is one the stream session can also open; the
    /// session reads this set rather than keeping a second copy of it.
    /// </remarks>
    public static readonly IReadOnlySet<int> SupportedSampleRates = new HashSet<int> { 44_100, 48_000, 96_000 };

    /// <summary>The supported rates for a message: <c>44100, 48000 or 96000</c>.</summary>
    public static string SupportedSampleRateList =>
        string.Join(", ", SupportedSampleRates.OrderBy(rate => rate));

    /// <summary>
    /// Whether a name could be the executable of a profile at all: a plain file name, with no
    /// directory in it.
    /// </summary>
    /// <remarks>
    /// The panel checks its field with this rather than with a rule of its own, so that what the
    /// user is allowed to type and what the route validator will accept are the same rule. This says
    /// nothing about whether the application exists — only <see cref="Validate"/> can say whether a
    /// route is openable.
    /// </remarks>
    public static bool IsUsableExecutableName(string? executableName) =>
        ProcessNames.TryNormalize(executableName, out _);

    public static AudioRouteValidation Validate(
        AudioProfileOptions profile,
        IReadOnlyList<AudioEndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(endpoints);

        if (!ProcessNames.TryNormalize(profile.ExecutableName, out string? executableName))
        {
            return AudioRouteValidation.Invalid(AudioRouteErrorCodes.InvalidExecutableName);
        }

        if (string.IsNullOrWhiteSpace(profile.VirtualRenderEndpointId) ||
            string.IsNullOrWhiteSpace(profile.VirtualCaptureEndpointId) ||
            string.IsNullOrWhiteSpace(profile.PhysicalRenderEndpointId))
        {
            return AudioRouteValidation.Invalid(AudioRouteErrorCodes.MissingEndpointId);
        }

        // Checked before looking anything up, so a profile that points its output back at its own
        // input is reported as the loop it is rather than as a missing device.
        if (string.Equals(
                profile.VirtualRenderEndpointId,
                profile.PhysicalRenderEndpointId,
                StringComparison.OrdinalIgnoreCase))
        {
            return AudioRouteValidation.Invalid(AudioRouteErrorCodes.SameRenderEndpoint);
        }

        AudioEndpointDescriptor? virtualRender = Find(endpoints, profile.VirtualRenderEndpointId);
        AudioEndpointDescriptor? virtualCapture = Find(endpoints, profile.VirtualCaptureEndpointId);
        AudioEndpointDescriptor? physicalRender = Find(endpoints, profile.PhysicalRenderEndpointId);

        if (virtualRender is null || virtualCapture is null || physicalRender is null)
        {
            return AudioRouteValidation.Invalid(AudioRouteErrorCodes.EndpointNotFound);
        }

        if (virtualRender.Flow != AudioDataFlow.Render ||
            virtualCapture.Flow != AudioDataFlow.Capture ||
            physicalRender.Flow != AudioDataFlow.Render)
        {
            return AudioRouteValidation.Invalid(AudioRouteErrorCodes.WrongDataFlow);
        }

        AudioEndpointDescriptor[] required = [virtualRender, virtualCapture, physicalRender];

        foreach (AudioEndpointDescriptor endpoint in required)
        {
            if (!endpoint.IsActive)
            {
                return AudioRouteValidation.Invalid(AudioRouteErrorCodes.EndpointNotActive);
            }

            if (endpoint.Channels != StereoChannelCount)
            {
                return AudioRouteValidation.Invalid(AudioRouteErrorCodes.UnsupportedChannelCount);
            }

            if (!SupportedSampleRates.Contains(endpoint.SampleRate))
            {
                return AudioRouteValidation.Invalid(AudioRouteErrorCodes.UnsupportedSampleRate);
            }
        }

        return AudioRouteValidation.Valid(new AudioRoute(
            VirtualRenderEndpointId: virtualRender.Id,
            VirtualCaptureEndpointId: virtualCapture.Id,
            PhysicalRenderEndpointId: physicalRender.Id,
            ExecutableName: executableName));
    }

    /// <summary>
    /// Endpoint IDs are GUID strings. They are compared without regard to case because Windows
    /// reports them in either one depending on which API produced them.
    /// </summary>
    private static AudioEndpointDescriptor? Find(
        IReadOnlyList<AudioEndpointDescriptor> endpoints,
        string endpointId)
    {
        foreach (AudioEndpointDescriptor endpoint in endpoints)
        {
            if (string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                return endpoint;
            }
        }

        return null;
    }
}
