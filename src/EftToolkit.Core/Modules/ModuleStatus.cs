namespace EftToolkit.Core.Modules;

/// <summary>
/// A module's current state plus operator-facing copy and a stable machine-readable code.
/// Callers must branch on <see cref="ErrorCode"/>, never on <see cref="Message"/>.
/// </summary>
public sealed record ModuleStatus(ModuleState State, string? Message = null, string? ErrorCode = null);
