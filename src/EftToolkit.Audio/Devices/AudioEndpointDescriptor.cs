namespace EftToolkit.Audio.Devices;

/// <summary>
/// Which way audio travels through an endpoint, named the way Windows names it: a render endpoint
/// is one Windows plays to, a capture endpoint is one Windows records from.
/// </summary>
public enum AudioDataFlow
{
    /// <summary>A recording endpoint: a microphone, or the output side of a virtual cable.</summary>
    Capture,

    /// <summary>A playback endpoint: headphones, or the input side of a virtual cable.</summary>
    Render,
}

/// <summary>
/// One audio endpoint that is currently active on the machine. Channel count and sample rate are
/// those of the endpoint's mix format, which is the format a shared-mode stream is handed.
/// </summary>
public sealed record AudioEndpointDescriptor(
    string Id,
    string FriendlyName,
    AudioDataFlow Flow,
    bool IsActive,
    int Channels,
    int SampleRate);
