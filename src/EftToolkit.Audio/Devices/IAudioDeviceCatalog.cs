namespace EftToolkit.Audio.Devices;

/// <summary>
/// Reads the machine's audio endpoints and the sessions playing on one of them. Nothing here opens
/// an audio stream: enumeration reports what exists, and opening a stream is the stream session's
/// job.
/// </summary>
public interface IAudioDeviceCatalog : IAsyncDisposable
{
    /// <summary>
    /// Raised when an endpoint is added, removed, or changes state. Raised on the thread the
    /// operating system reports the change from, which is not a thread of the caller's choosing.
    /// </summary>
    event EventHandler? DevicesChanged;

    /// <summary>Every active endpoint, in no particular order.</summary>
    Task<IReadOnlyList<AudioEndpointDescriptor>> GetEndpointsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The processes holding an audio session on one render endpoint. Sessions belonging to system
    /// sounds are not included, because they belong to no application the user picked.
    /// </summary>
    Task<IReadOnlySet<int>> GetActiveProcessIdsAsync(
        string renderEndpointId,
        CancellationToken cancellationToken);
}
