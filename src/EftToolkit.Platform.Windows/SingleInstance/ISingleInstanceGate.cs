namespace EftToolkit.Platform.Windows.SingleInstance;

/// <summary>
/// Decides whether this process is the one copy of the toolkit that runs, and lets a later launch
/// ask that copy to show its window.
/// </summary>
/// <remarks>
/// The toolkit is a tray application whose window can be hidden, so a second launch is almost
/// always the user trying to get the first one back. Being a second instance is therefore a normal
/// outcome rather than an error: the caller is expected to notify the primary and exit.
/// </remarks>
public interface ISingleInstanceGate : IAsyncDisposable
{
    /// <summary>Whether this gate is the primary instance and is holding the claim.</summary>
    bool IsPrimary { get; }

    /// <summary>
    /// Raised on the primary when another launch asked it to activate. Raised on a thread of the
    /// pipe server's choosing, not on the caller's, so a handler that touches a UI must marshal.
    /// </summary>
    event EventHandler? ActivationRequested;

    /// <summary>
    /// Claims the instance. Returns whether this gate is the primary: <see langword="false"/> means
    /// another copy already holds the claim, and the caller should notify it and exit.
    /// </summary>
    Task<bool> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the primary to activate. Only meaningful on a gate that did not acquire.
    /// </summary>
    /// <exception cref="TimeoutException">No primary instance was listening.</exception>
    Task NotifyPrimaryAsync(CancellationToken cancellationToken);
}
