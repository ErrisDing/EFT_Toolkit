namespace EftToolkit.Platform.Windows.Messaging;

/// <summary>
/// A window that receives platform messages. Implementations own a real window on their own thread;
/// <see cref="WindowsMessageSink"/> is the production one and the test suite substitutes its own.
/// </summary>
/// <remarks>
/// Handlers run on the thread that owns the window and must return promptly. Anything that touches
/// the display driver belongs on a worker, not here.
/// </remarks>
public interface IWindowsMessageSource : IAsyncDisposable
{
    /// <summary>The window handle to register hotkeys and session notifications against.</summary>
    nint WindowHandle { get; }

    event EventHandler<WindowsMessage>? MessageReceived;

    /// <summary>
    /// Runs <paramref name="callback"/> on the thread that owns the window and returns its result.
    /// </summary>
    /// <remarks>
    /// Windows requires <c>RegisterHotKey</c> to be called by the thread that owns the window, so a
    /// caller on another thread cannot register directly. Callbacks queued before
    /// <see cref="StopAsync"/> are guaranteed to have run by the time it returns, because posted
    /// messages are processed in order.
    /// </remarks>
    Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
