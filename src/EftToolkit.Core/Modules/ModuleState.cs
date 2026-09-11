namespace EftToolkit.Core.Modules;

/// <summary>
/// Lifecycle position of a toolkit module. Every module follows the same idempotent model:
/// <c>Disabled -> Starting -> Active/Bypass -> Degraded/Faulted -> Stopping -> Disabled</c>.
/// </summary>
public enum ModuleState
{
    Disabled,
    Starting,
    Active,
    Bypass,
    Degraded,
    Faulted,
    Stopping,
}
