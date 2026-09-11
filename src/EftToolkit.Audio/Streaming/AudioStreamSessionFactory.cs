using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// Builds the stream sessions the audio module opens: one per opened route, because a session that
/// has started is holding both endpoints until it is stopped.
/// </summary>
/// <remarks>
/// <para>
/// The session itself is internal, and deliberately so. Everything that keeps this toolkit in WASAPI
/// shared mode lives inside it, and nothing outside this assembly should be able to start a capture
/// and render pair of its own. The composition root still has to hand the audio module something
/// that can produce one, so this is the seam rather than a made-public constructor.
/// </para>
/// <para>
/// The client factory is shared across the sessions: it holds no device of its own — each call
/// resolves the configured endpoint by identifier and hands ownership of it to the caller — so
/// one instance is enough for the lifetime of the application.
/// </para>
/// </remarks>
public static class AudioStreamSessionFactory
{
    /// <summary>
    /// A factory that opens the real Windows endpoints in shared mode.
    /// </summary>
    public static Func<IAudioStreamSession> Create(IAppLogger? logger = null)
    {
        NAudioClientFactory clients = new(logger);

        return () => new NAudioSharedStreamSession(clients, logger);
    }
}
