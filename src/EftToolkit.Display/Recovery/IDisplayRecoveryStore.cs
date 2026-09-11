namespace EftToolkit.Display.Recovery;

/// <summary>
/// Persists captured original ramps across restarts so a crash while a preset is applied can be
/// undone. Implementations must never return an entry they cannot vouch for.
/// </summary>
public interface IDisplayRecoveryStore
{
    /// <summary>Returns <see langword="null"/> when there is nothing to recover.</summary>
    Task<DisplayRecoverySnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(DisplayRecoverySnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Drops the entries for displays whose originals have been put back.</summary>
    Task RemoveAsync(IReadOnlySet<string> restoredStableIds, CancellationToken cancellationToken);
}
