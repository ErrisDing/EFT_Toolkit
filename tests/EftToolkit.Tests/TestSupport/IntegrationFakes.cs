using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Processes;
using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;
using EftToolkit.Core.Platform;

namespace EftToolkit.Tests.TestSupport;

/// <summary>
/// Keeps configuration in memory instead of on disk. What is stored is what the next read returns,
/// which is all a test needs from a file it never opens.
/// </summary>
internal sealed class MemoryOptionsStore(ToolkitOptions options) : IOptionsStore
{
    private ToolkitOptions _options = options;

    /// <summary>Every configuration handed to <see cref="SaveAsync"/>, in order.</summary>
    public List<ToolkitOptions> Saved { get; } = [];

    public Task<ToolkitOptions> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_options);

    public Task SaveAsync(ToolkitOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        Saved.Add(options);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An audio device catalog reporting a fixed list. Endpoints are set rather than discovered, so a
/// test can say exactly what the machine has — including having nothing at all.
/// </summary>
internal sealed class StubDeviceCatalog : IAudioDeviceCatalog
{
    public List<AudioEndpointDescriptor> Endpoints { get; set; } = [];

    public IReadOnlySet<int> SessionProcessIds { get; set; } = new HashSet<int>();

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<AudioEndpointDescriptor>> GetEndpointsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AudioEndpointDescriptor>>([.. Endpoints]);

    public Task<IReadOnlySet<int>> GetActiveProcessIdsAsync(
        string renderEndpointId,
        CancellationToken cancellationToken) =>
        Task.FromResult(SessionProcessIds);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Reports whether the watched application is running. Whether it is running is a fact about the
/// machine, so stopping the watch does not change it: a stub that conflated the two would make a
/// module that stopped watching look like a game that exited.
/// </summary>
internal sealed class StubProcessMonitor : IProcessMonitor
{
    public bool IsRunning { get; set; }

    public IReadOnlySet<int> ProcessIds => IsRunning ? new HashSet<int> { 42 } : new HashSet<int>();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task StartAsync(string executableName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A stream that opens without a device. It records what it was opened with and what it was told
/// afterwards, which is what the tests that care about bypass and teardown order read.
/// </summary>
internal sealed class StubStreamSession : IAudioStreamSession
{
    public bool IsRunning { get; private set; }

    public int StopCount { get; private set; }

    /// <summary>The limiter the session was last opened with. Only known at that moment.</summary>
    public AudioLimiterOptions? LastLimiter { get; private set; }

    /// <summary>Every bypass value written, in order.</summary>
    public List<bool> BypassValues { get; } = [];

    public AudioStreamMetrics Metrics { get; set; } = AudioStreamMetrics.Idle;

    public event EventHandler<Exception>? Faulted
    {
        add { }
        remove { }
    }

    public Task StartAsync(AudioRoute route, AudioLimiterOptions limiter, CancellationToken cancellationToken)
    {
        LastLimiter = limiter;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task SetBypassAsync(bool bypass, CancellationToken cancellationToken)
    {
        BypassValues.Add(bypass);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Takes no shortcut from the machine running the tests — a real registration would bind F2-F5
/// machine-wide and fail outright where another application already owns them — and lets a test
/// press one instead.
/// </summary>
/// <remarks>
/// The event is delivered rather than swallowed, because delivering it is the whole of what the
/// real service does: the preset travels from the message window through the coordinator to the
/// display module, and a stub that dropped it would leave that path untested.
/// </remarks>
internal sealed class StubHotkeys(List<string>? journal = null) : IHotkeyService
{
    public IReadOnlyDictionary<DisplayPresetKind, bool> Registrations { get; } =
        new Dictionary<DisplayPresetKind, bool>();

    public event EventHandler<DisplayPresetKind>? PresetRequested;

    /// <summary>Presses one of the shortcuts, as the message window would.</summary>
    internal void Press(DisplayPresetKind preset) => PresetRequested?.Invoke(this, preset);

    public Task RegisterAsync(CancellationToken cancellationToken)
    {
        journal?.Add("hotkeys.register");
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(CancellationToken cancellationToken)
    {
        journal?.Add("hotkeys.unregister");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Never reports a Windows event, so nothing here is exercised beyond being wired up.</summary>
internal sealed class StubEvents(List<string>? journal = null) : IPlatformEventSource
{
    public event EventHandler? DisplayEnvironmentChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        journal?.Add("platformEvents.start");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        journal?.Add("platformEvents.stop");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
