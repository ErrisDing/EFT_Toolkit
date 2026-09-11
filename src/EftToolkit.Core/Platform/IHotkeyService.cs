using EftToolkit.Core.Display;

namespace EftToolkit.Core.Platform;

/// <summary>
/// Registers the fixed F2-F5 display shortcuts and reports them as preset requests. The shortcuts
/// are global, so they are bound to a dedicated message window rather than to a UI window that may
/// be hidden or recreated.
/// </summary>
public interface IHotkeyService : IAsyncDisposable
{
    /// <summary>
    /// Whether each preset's shortcut is currently held. A key another application already owns is
    /// reported as <see langword="false"/> here while the remaining keys keep working.
    /// </summary>
    IReadOnlyDictionary<DisplayPresetKind, bool> Registrations { get; }

    event EventHandler<DisplayPresetKind>? PresetRequested;

    Task RegisterAsync(CancellationToken cancellationToken);

    Task UnregisterAsync(CancellationToken cancellationToken);
}
