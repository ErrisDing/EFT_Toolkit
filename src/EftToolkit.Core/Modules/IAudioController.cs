namespace EftToolkit.Core.Modules;

public interface IAudioController : IToolkitModule
{
    bool IsTargetRunning { get; }

    bool IsRouteOpen { get; }

    /// <summary>
    /// Whether the user has asked for the audio to pass through unchanged. This is the preference
    /// rather than what the stream is doing: it is true with nothing open, which is what lets a
    /// caller that is about to write a bypass of its own tell the two apart.
    /// </summary>
    bool IsBypassed { get; }

    Task SetBypassAsync(bool bypass, CancellationToken cancellationToken);
}
