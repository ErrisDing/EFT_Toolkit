namespace EftToolkit.Audio.Processes;

/// <summary>
/// Watches for one executable being started and stopped.
/// </summary>
public interface IProcessMonitor : IAsyncDisposable
{
    /// <summary>Whether the watched executable is running.</summary>
    bool IsRunning { get; }

    /// <summary>The IDs observed at the most recent observation.</summary>
    IReadOnlySet<int> ProcessIds { get; }

    /// <summary>
    /// Raised when the set of running IDs changes. A poll that observed the same set raises
    /// nothing, so subscribers hear about the game opening and closing rather than about polling.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Begins watching. Calling this again for the same name is a no-op.</summary>
    Task StartAsync(string executableName, CancellationToken cancellationToken);

    /// <summary>Stops watching and forgets what was observed.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
