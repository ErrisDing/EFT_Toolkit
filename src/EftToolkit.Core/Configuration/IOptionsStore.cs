namespace EftToolkit.Core.Configuration;

/// <summary>
/// Persists user configuration. Implementations must never leave a partially written file behind
/// and must never discard a file they could not read.
/// </summary>
public interface IOptionsStore
{
    /// <summary>
    /// Loads and sanitizes configuration. Returns validated defaults when nothing is stored,
    /// when the file cannot be parsed, or when its schema version is unsupported.
    /// </summary>
    Task<ToolkitOptions> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ToolkitOptions options, CancellationToken cancellationToken);
}
