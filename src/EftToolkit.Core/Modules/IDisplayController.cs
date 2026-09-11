using EftToolkit.Core.Display;

namespace EftToolkit.Core.Modules;

public interface IDisplayController : IToolkitModule
{
    /// <summary>Applies a preset to every selected display. Never throws for a single display failure.</summary>
    Task ApplyPresetAsync(DisplayPresetKind preset, CancellationToken cancellationToken);

    /// <summary>Re-enumerates displays after a topology, resolution, power, or session event and reapplies the current preset.</summary>
    Task RefreshAndReapplyAsync(CancellationToken cancellationToken);

    /// <summary>Restores a persisted original ramp when the current state still matches the last toolkit write.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);
}
