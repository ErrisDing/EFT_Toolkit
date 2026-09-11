namespace EftToolkit.Core.Modules;

public interface IAudioController : IToolkitModule
{
    bool IsTargetRunning { get; }

    bool IsRouteOpen { get; }

    Task SetBypassAsync(bool bypass, CancellationToken cancellationToken);
}
