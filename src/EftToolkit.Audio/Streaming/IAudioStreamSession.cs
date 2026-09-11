using EftToolkit.Audio.Routing;
using EftToolkit.Core.Configuration;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// One opened route: audio captured from the virtual endpoint, limited, and played to the physical
/// one. Both endpoints are always opened in WASAPI shared mode; there is no exclusive fallback.
/// </summary>
public interface IAudioStreamSession : IAsyncDisposable
{
    /// <summary>True between a successful <see cref="StartAsync"/> and the next <see cref="StopAsync"/>.</summary>
    bool IsRunning { get; }

    /// <summary>A snapshot of the counters and levels as they stand. Never null.</summary>
    AudioStreamMetrics Metrics { get; }

    /// <summary>
    /// Raised once when either side of the stream fails, after which the session is no longer doing
    /// anything useful. Recovery is the owner's job, not the session's.
    /// </summary>
    event EventHandler<Exception>? Faulted;

    /// <summary>
    /// Opens both endpoints and begins streaming. The limiter configuration only takes effect at
    /// this point; changing it means stopping and starting again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is already running.</exception>
    /// <exception cref="ArgumentException">An endpoint's format is not stereo.</exception>
    /// <exception cref="NotSupportedException">
    /// An endpoint's sample rate is not one of <see cref="Routing.AudioRouteValidator.SupportedSampleRates"/>.
    /// </exception>
    Task StartAsync(AudioRoute route, AudioLimiterOptions limiter, CancellationToken cancellationToken);

    /// <summary>
    /// Switches between the limiter and a straight pass-through. The streams stay open either way:
    /// closing and reopening them would drop the audio the user is listening to.
    /// </summary>
    Task SetBypassAsync(bool bypass, CancellationToken cancellationToken);

    /// <summary>Fades out, stops both sides, and releases every device. Safe to call repeatedly.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
