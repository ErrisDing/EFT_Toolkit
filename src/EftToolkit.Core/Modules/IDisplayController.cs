using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;

namespace EftToolkit.Core.Modules;

public interface IDisplayController : IToolkitModule
{
    /// <summary>The preset the module is currently converging on.</summary>
    DisplayPresetKind CurrentPreset { get; }

    /// <summary>Applies a preset to every selected display. Never throws for a single display failure.</summary>
    Task ApplyPresetAsync(DisplayPresetKind preset, CancellationToken cancellationToken);

    /// <summary>
    /// Adopts an edited configuration: which displays are selected, and the values each preset
    /// composes from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The module captured its options when it was built, so an edit reaching it any other way would
    /// be an edit that never happened. Adopting is not acting: a new selection matters at the next
    /// enumeration and a new preset value at the next write, so the caller decides what to run
    /// afterwards.
    /// </para>
    /// <para>
    /// What is adopted is the caller's to have validated. A gamma value outside the approved range is
    /// a dark screen rather than a brighter one, and this is not the layer that decides the range.
    /// </para>
    /// </remarks>
    void UpdateOptions(DisplayOptions options);

    /// <summary>Re-enumerates displays after a topology, resolution, power, or session event and reapplies the current preset.</summary>
    Task RefreshAndReapplyAsync(CancellationToken cancellationToken);

    /// <summary>Restores a persisted original ramp when the current state still matches the last toolkit write.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);
}
