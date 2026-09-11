using EftToolkit.Display.Devices;
using EftToolkit.Display.Gamma;

namespace EftToolkit.Tests.Display;

/// <summary>
/// An in-memory display driver. A display holds exactly one ramp, the one currently on it; the
/// module's "original" is simply whatever was on the display when the module first read it, so
/// there is no separate original to model here.
/// <para>
/// <see cref="WriteGate"/> lets a test hold a write open so that requests arriving while the worker
/// is busy can be observed.
/// </para>
/// </summary>
internal sealed class FakeDisplayGammaGateway : IDisplayGammaGateway
{
    private readonly Dictionary<string, GammaRamp> _current = new(StringComparer.Ordinal);

    public List<DisplayDescriptor> Displays { get; } = [];

    /// <summary>Every ramp written, in order, across all displays.</summary>
    public List<GammaRamp> WriteLog { get; } = [];

    public Dictionary<string, int> WriteCountByDisplay { get; } = new(StringComparer.Ordinal);

    /// <summary>Displays whose writes are refused by the driver, as a rejection rather than an exception.</summary>
    public HashSet<string> FailOnWrite { get; } = new(StringComparer.Ordinal);

    /// <summary>Displays whose writes throw, standing in for a driver that faults mid-call.</summary>
    public HashSet<string> ThrowOnWrite { get; } = new(StringComparer.Ordinal);

    public HashSet<string> FailOnRead { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, writes block until it completes.</summary>
    public TaskCompletionSource? WriteGate { get; set; }

    public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GammaRamp? LastWrittenRamp => WriteLog.Count == 0 ? null : WriteLog[^1];

    public static FakeDisplayGammaGateway OneDisplay(GammaRamp current)
    {
        FakeDisplayGammaGateway gateway = new();
        gateway.AddDisplay("display-1", "Display One", current);
        return gateway;
    }

    public void AddDisplay(string stableId, string friendlyName, GammaRamp current)
    {
        Displays.Add(new DisplayDescriptor(
            stableId,
            $@"\\.\DISPLAY{Displays.Count + 1}",
            friendlyName,
            IsConnected: true,
            IsHdr: false,
            SupportsGammaRamp: true));

        _current[stableId] = current;
    }

    public GammaRamp CurrentRamp(string stableId) => _current[stableId];

    public Task<IReadOnlyList<DisplayDescriptor>> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DisplayDescriptor>>([.. Displays]);

    public Task<GammaRamp> ReadAsync(DisplayDescriptor display, CancellationToken cancellationToken)
    {
        if (FailOnRead.Contains(display.StableId))
        {
            throw new InvalidOperationException($"The ramp of {display.StableId} could not be read.");
        }

        return Task.FromResult(_current[display.StableId]);
    }

    public async Task<GammaWriteResult> WriteAsync(
        DisplayDescriptor display,
        GammaRamp ramp,
        CancellationToken cancellationToken)
    {
        FirstWriteStarted.TrySetResult();

        if (WriteGate is not null)
        {
            await WriteGate.Task.ConfigureAwait(false);
        }

        if (ThrowOnWrite.Contains(display.StableId))
        {
            throw new InvalidOperationException($"The ramp of {display.StableId} could not be written.");
        }

        if (FailOnWrite.Contains(display.StableId))
        {
            return GammaWriteResult.Rejected(87, $"The ramp of {display.StableId} was rejected.");
        }

        _current[display.StableId] = ramp;
        WriteLog.Add(ramp);
        WriteCountByDisplay[display.StableId] = WriteCountByDisplay.GetValueOrDefault(display.StableId) + 1;

        return GammaWriteResult.Accepted();
    }
}
