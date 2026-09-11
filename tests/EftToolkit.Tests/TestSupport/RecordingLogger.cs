using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Tests.TestSupport;

/// <summary>
/// Captures what was written to the application log so a test can assert on it.
/// </summary>
/// <remarks>
/// Writes arrive from whichever thread produced them — the audio capture thread and the process
/// poll both log while a test is reading — so the list is guarded rather than assumed to be
/// single-threaded.
/// </remarks>
internal sealed class RecordingLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];

    internal IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    internal bool Logged(string eventName) => Entries.Any(entry => entry.EventName == eventName);

    public void Write(
        LogLevel level,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(level, eventName, properties, exception));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One line that was written, with everything the caller passed.</summary>
internal sealed record LogEntry(
    LogLevel Level,
    string EventName,
    IReadOnlyDictionary<string, object?>? Properties,
    Exception? Exception);
