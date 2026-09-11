namespace EftToolkit.Core.Platform;

/// <summary>
/// Reports the Windows events that invalidate a gamma ramp: a display topology or resolution
/// change, an automatic resume from sleep, and a session unlock. Events that arrive close together
/// are collapsed, because Windows reports one monitor change as several messages and each one would
/// otherwise cost a full re-enumeration.
/// </summary>
public interface IPlatformEventSource : IAsyncDisposable
{
    event EventHandler? DisplayEnvironmentChanged;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
