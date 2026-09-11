using EftToolkit.Core.Display;
using EftToolkit.Display.Devices;

namespace EftToolkit.Display;

/// <summary>
/// One row of the display panel: what the display is, whether it is part of the selection, which
/// preset is applied to it, and how the last write went. A display that is selected but not
/// currently connected still gets a row, so the panel can say so instead of silently dropping it.
/// </summary>
public sealed record DisplayStatus(
    DisplayDescriptor Display,
    bool Selected,
    DisplayPresetKind Preset,
    GammaWriteResult? LastResult,
    string? Message);
