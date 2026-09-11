namespace EftToolkit.Core.Display;

/// <summary>
/// The four fixed display shortcuts. F2 restores the captured original ramp and disables
/// enhancement; F3, F4, and F5 apply the low, medium, and high presets. These bindings are
/// fixed for the first release and are not user configurable.
/// </summary>
public enum DisplayPresetKind
{
    Original,
    Low,
    Medium,
    High,
}
