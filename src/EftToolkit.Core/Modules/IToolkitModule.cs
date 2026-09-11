namespace EftToolkit.Core.Modules;

/// <summary>
/// Common lifecycle surface for the display and audio modules.
/// Enable, disable, and dispose are idempotent and safe to call repeatedly.
/// </summary>
public interface IToolkitModule : IAsyncDisposable
{
    ModuleStatus Status { get; }

    event EventHandler<ModuleStatus>? StatusChanged;

    Task EnableAsync(CancellationToken cancellationToken);

    Task DisableAsync(CancellationToken cancellationToken);
}
