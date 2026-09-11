namespace EftToolkit.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// Structured application log. Captured audio and raw byte buffers are never recorded; property
/// keys that would carry samples are dropped by the implementation rather than serialized.
/// </summary>
public interface IAppLogger : IAsyncDisposable
{
    void Write(
        LogLevel level,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null);
}
