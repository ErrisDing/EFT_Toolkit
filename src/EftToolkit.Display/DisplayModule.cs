using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Channels;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Display;
using EftToolkit.Core.Modules;
using EftToolkit.Display.Devices;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Recovery;

namespace EftToolkit.Display;

/// <summary>
/// Owns the display side of the toolkit: capturing originals, applying presets, restoring on
/// shutdown, and repairing the display state left behind by a crash.
/// </summary>
/// <remarks>
/// Preset writes are serialized through a background worker because a single
/// <c>SetDeviceGammaRamp</c> call can block for a noticeable time, and a panel that waited on it
/// would stutter. The queue holds one entry and drops the oldest on overflow, so a burst of
/// shortcut presses converges on the last request instead of replaying every intermediate step
/// against the display driver.
/// </remarks>
public sealed class DisplayModule : IDisplayController
{
    private readonly DisplayOptions _options;
    private readonly IDisplayGammaGateway _gateway;
    private readonly IDisplayRecoveryStore _recoveryStore;
    private readonly IAppLogger? _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Serializes enable, disable, and refresh against each other.</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    /// <summary>
    /// Guards the state shared between the worker and the lifecycle path: rows, logical preset,
    /// module status, and the captured ramps. A topology refresh can persist while the worker is
    /// mid-write, so these are reached from two threads even though applies themselves are serial.
    /// </summary>
    private readonly object _stateGate = new();

    private readonly Dictionary<string, GammaRamp> _originals = new(StringComparer.Ordinal);

    /// <summary>The fingerprint of the ramp this toolkit last wrote, per display, for safe recovery.</summary>
    private readonly Dictionary<string, string> _lastWrittenFingerprints = new(StringComparer.Ordinal);

    private IReadOnlyList<DisplayStatus> _displays = [];
    private DisplayPresetKind _currentPreset = DisplayPresetKind.Original;
    private ModuleStatus _status = new(ModuleState.Disabled);

    private Channel<DisplayPresetKind>? _presetChannel;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private bool _enabled;
    private volatile bool _disposed;

    public DisplayModule(
        DisplayOptions options,
        IDisplayGammaGateway gateway,
        IDisplayRecoveryStore recoveryStore,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DisplayPresetKind CurrentPreset
    {
        get
        {
            lock (_stateGate)
            {
                return _currentPreset;
            }
        }
    }

    public IReadOnlyList<DisplayStatus> Displays
    {
        get
        {
            lock (_stateGate)
            {
                return _displays;
            }
        }
    }

    public ModuleStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    public event EventHandler<ModuleStatus>? StatusChanged;

    public event EventHandler? DisplaysChanged;

    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_enabled)
            {
                return;
            }

            SetStatus(new ModuleStatus(ModuleState.Starting, "Reading the current display ramps."));

            IReadOnlyList<DisplayDescriptor> displays = await _gateway
                .EnumerateAsync(cancellationToken)
                .ConfigureAwait(false);

            ClearCaptures();

            List<DisplayStatus> rows = await CaptureAsync(displays, cancellationToken).ConfigureAwait(false);
            _displays = rows;
            _currentPreset = DisplayPresetKind.Original;

            // Recovery is written before the module is allowed to apply anything. That ordering is
            // what makes a crash survivable: no preset write can reach a display whose original
            // ramp is not already on disk.
            if (OriginalCount > 0)
            {
                await PersistRecoveryAsync(cancellationToken).ConfigureAwait(false);
            }

            StartWorker();

            _enabled = true;
            SetStatus(SummarizeSelection());
            RaiseDisplaysChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_enabled)
            {
                // Idempotent: a second disable has nothing left to stop or restore.
                return;
            }

            SetStatus(new ModuleStatus(ModuleState.Stopping, "Restoring the original display ramps."));

            await StopWorkerAsync().ConfigureAwait(false);

            HashSet<string> restored = await RestoreOriginalsAsync(cancellationToken).ConfigureAwait(false);

            _enabled = false;
            ClearCaptures();
            SetDisplays([.. Displays.Select(row => row with { Preset = DisplayPresetKind.Original, LastResult = null })]);

            if (restored.Count > 0)
            {
                // Only displays that were restored and verified lose their recovery entry. Anything
                // that failed keeps its entry so the next launch can try again.
                await _recoveryStore.RemoveAsync(restored, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(new ModuleStatus(ModuleState.Disabled));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ApplyPresetAsync(DisplayPresetKind preset, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_enabled)
        {
            // The module is off, so there is nothing to enhance and no captured ramp to write back to.
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The logical preset is recorded before the request is queued, so the panel and the worker
        // agree on the target even when requests arrive faster than the worker drains them.
        lock (_stateGate)
        {
            _currentPreset = preset;
        }

        _presetChannel?.Writer.TryWrite(preset);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task RefreshAndReapplyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_enabled)
            {
                return;
            }

            IReadOnlyList<DisplayDescriptor> displays = await _gateway
                .EnumerateAsync(cancellationToken)
                .ConfigureAwait(false);

            SetDisplays(await CaptureAsync(displays, cancellationToken).ConfigureAwait(false));

            // Displays that reconnected keep the original captured when the module was enabled.
            // Re-reading now would capture whatever the toolkit itself last wrote, which would then
            // be restored as if it were the user's calibration.
            if (OriginalCount > 0 && !_disposed)
            {
                await PersistRecoveryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ApplyToSelectedAsync(CurrentPreset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        DisplayRecoverySnapshot? snapshot = await _recoveryStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot is null || snapshot.Displays.Count == 0)
        {
            return;
        }

        IReadOnlyList<DisplayDescriptor> displays = await _gateway
            .EnumerateAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, DisplayDescriptor> byId = [];
        foreach (DisplayDescriptor display in displays)
        {
            byId[display.StableId] = display;
        }

        HashSet<string> restored = new(StringComparer.Ordinal);

        foreach (DisplayRecoveryEntry entry in snapshot.Displays)
        {
            if (!entry.TryReadOriginalRamp(out GammaRamp? original))
            {
                continue;
            }

            if (!byId.TryGetValue(entry.StableId, out DisplayDescriptor? display))
            {
                _logger?.Write(
                    LogLevel.Information,
                    "display.recovery.displayMissing",
                    new Dictionary<string, object?> { ["displayId"] = entry.StableId });

                continue;
            }

            GammaRamp current;

            try
            {
                current = await _gateway.ReadAsync(display, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.Write(
                    LogLevel.Warning,
                    "display.recovery.readFailed",
                    new Dictionary<string, object?> { ["displayId"] = entry.StableId },
                    exception);

                continue;
            }

            if (!string.Equals(
                    GammaRampFingerprint.Compute(current),
                    entry.LastWrittenFingerprint,
                    StringComparison.Ordinal))
            {
                // The display no longer holds the ramp this toolkit wrote, so something else changed
                // it. Putting the original back would undo a change the toolkit never made.
                _logger?.Write(
                    LogLevel.Information,
                    "display.recovery.skippedExternalChange",
                    new Dictionary<string, object?> { ["displayId"] = entry.StableId });

                continue;
            }

            GammaWriteResult result = await _gateway
                .WriteAsync(display, original, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                restored.Add(entry.StableId);
            }
            else
            {
                _logger?.Write(
                    LogLevel.Warning,
                    "display.recovery.restoreFailed",
                    new Dictionary<string, object?>
                    {
                        ["displayId"] = entry.StableId,
                        ["apiAccepted"] = result.ApiAccepted,
                        ["readbackMatched"] = result.ReadbackMatched,
                    });
            }
        }

        if (restored.Count > 0)
        {
            await _recoveryStore.RemoveAsync(restored, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await DisableAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Write(LogLevel.Error, "display.dispose.failed", exception: exception);
        }

        _disposed = true;

        // The semaphore is deliberately left undisposed: disposing it here would turn a concurrent
        // disable into an ObjectDisposedException instead of a clean no-op.
    }

    /// <summary>
    /// Builds the panel rows for the displays just enumerated, capturing the original ramp of each
    /// display that is selected, connected, and readable.
    /// </summary>
    private async Task<List<DisplayStatus>> CaptureAsync(
        IReadOnlyList<DisplayDescriptor> displays,
        CancellationToken cancellationToken)
    {
        List<DisplayStatus> rows = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (DisplayDescriptor display in displays)
        {
            seen.Add(display.StableId);

            if (!IsSelected(display.StableId))
            {
                rows.Add(new DisplayStatus(display, false, DisplayPresetKind.Original, null, null));
                continue;
            }

            if (!display.IsConnected)
            {
                rows.Add(new DisplayStatus(display, false, DisplayPresetKind.Original, null, "This display is not connected."));
                continue;
            }

            if (!display.SupportsGammaRamp)
            {
                rows.Add(new DisplayStatus(
                    display,
                    false,
                    DisplayPresetKind.Original,
                    null,
                    "This display does not expose a gamma ramp."));

                continue;
            }

            if (TryGetOriginal(display.StableId, out _))
            {
                // Already captured on an earlier pass; the original must not be re-read, because
                // reading now could capture a ramp this toolkit wrote itself.
                rows.Add(new DisplayStatus(display, true, CurrentPreset, null, null));
                continue;
            }

            try
            {
                SetOriginal(display.StableId, await _gateway.ReadAsync(display, cancellationToken).ConfigureAwait(false));
                rows.Add(new DisplayStatus(display, true, CurrentPreset, null, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Without a readable original there is nothing to restore, so this display is left
                // out of the selection rather than enhanced irreversibly.
                RemoveOriginal(display.StableId);

                _logger?.Write(
                    LogLevel.Warning,
                    "display.original.unreadable",
                    new Dictionary<string, object?> { ["displayId"] = display.StableId },
                    exception);

                rows.Add(new DisplayStatus(
                    display,
                    false,
                    DisplayPresetKind.Original,
                    null,
                    "The current ramp could not be read, so this display was left unchanged."));
            }
        }

        // Selected displays that are not present at all still get a row, so the panel can report a
        // disconnected monitor instead of showing a selection that silently lost an entry.
        foreach (string selectedId in _options.SelectedDisplayIds)
        {
            if (seen.Contains(selectedId))
            {
                continue;
            }

            rows.Add(new DisplayStatus(
                new DisplayDescriptor(selectedId, string.Empty, selectedId, false, false, false),
                true,
                DisplayPresetKind.Original,
                null,
                "This display is not connected."));
        }

        return rows;
    }

    private async Task<HashSet<string>> RestoreOriginalsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> restored = new(StringComparer.Ordinal);

        foreach ((string stableId, GammaRamp original, _) in SnapshotCaptures())
        {
            DisplayDescriptor? display = Displays
                .FirstOrDefault(row => string.Equals(row.Display.StableId, stableId, StringComparison.Ordinal))
                ?.Display;

            if (display is null || !display.IsConnected)
            {
                continue;
            }

            try
            {
                GammaWriteResult result = await _gateway.WriteAsync(display, original, cancellationToken).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    restored.Add(stableId);
                }
                else
                {
                    _logger?.Write(
                        LogLevel.Warning,
                        "display.restore.failed",
                        new Dictionary<string, object?>
                        {
                            ["displayId"] = stableId,
                            ["message"] = result.Message,
                        });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.Write(
                    LogLevel.Warning,
                    "display.restore.failed",
                    new Dictionary<string, object?> { ["displayId"] = stableId },
                    exception);
            }
        }

        return restored;
    }

    private async Task ApplyToSelectedAsync(DisplayPresetKind preset, CancellationToken cancellationToken)
    {
        List<DisplayStatus> updated = [];
        int attempted = 0;
        int succeeded = 0;
        bool fingerprintChanged = false;

        foreach (DisplayStatus row in Displays)
        {
            if (!row.Selected || !row.Display.IsConnected)
            {
                updated.Add(row);
                continue;
            }

            if (!TryGetOriginal(row.Display.StableId, out GammaRamp? original))
            {
                updated.Add(row with { Preset = preset, Message = "The original ramp for this display was not captured." });
                continue;
            }

            attempted++;

            GammaRamp ramp = preset == DisplayPresetKind.Original
                ? original
                : GammaRampComposer.Compose(original, PresetOptionsFor(preset));

            try
            {
                GammaWriteResult result = await _gateway.WriteAsync(row.Display, ramp, cancellationToken).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    succeeded++;
                    SetLastWrittenFingerprint(row.Display.StableId, GammaRampFingerprint.Compute(ramp));
                    fingerprintChanged = true;
                }

                updated.Add(row with { Preset = preset, LastResult = result, Message = result.Message });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A display that fails must not stop the ones after it: each row carries its own result.
                _logger?.Write(
                    LogLevel.Warning,
                    "display.preset.writeFailed",
                    new Dictionary<string, object?> { ["displayId"] = row.Display.StableId },
                    exception);

                updated.Add(row with { Preset = preset, Message = exception.Message });
            }
        }

        SetDisplays(updated);

        if (fingerprintChanged)
        {
            // The last-written fingerprint has to be current before the next crash, otherwise
            // recovery would compare against a ramp that is no longer on the display.
            await PersistRecoveryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (attempted > 0 && succeeded == 0)
        {
            SetStatus(new ModuleStatus(
                ModuleState.Degraded,
                "No selected display accepted the preset.",
                "display.preset.noneApplied"));
        }
        else
        {
            SetStatus(SummarizeSelection());
        }
    }

    private DisplayPresetOptions PresetOptionsFor(DisplayPresetKind preset) => preset switch
    {
        DisplayPresetKind.Low => _options.Low,
        DisplayPresetKind.Medium => _options.Medium,
        DisplayPresetKind.High => _options.High,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "The original preset has no parameters."),
    };

    private async Task PersistRecoveryAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAt = _timeProvider.GetUtcNow();
        List<DisplayRecoveryEntry> entries = [];

        foreach ((string stableId, GammaRamp original, string fingerprint) in SnapshotCaptures())
        {
            entries.Add(DisplayRecoveryEntry.Create(stableId, original, fingerprint, capturedAt));
        }

        await _recoveryStore
            .SaveAsync(new DisplayRecoverySnapshot(DisplayRecoverySnapshot.CurrentSchemaVersion, entries), cancellationToken)
            .ConfigureAwait(false);
    }

    private void StartWorker()
    {
        _presetChannel = Channel.CreateBounded<DisplayPresetKind>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _workerCancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunWorkerAsync(_presetChannel.Reader, _workerCancellation.Token));
    }

    private async Task StopWorkerAsync()
    {
        if (_presetChannel is null || _worker is null)
        {
            return;
        }

        _presetChannel.Writer.TryComplete();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the worker was cancelled rather than drained.
        }

        _workerCancellation?.Dispose();
        _workerCancellation = null;
        _worker = null;
        _presetChannel = null;
    }

    private async Task RunWorkerAsync(ChannelReader<DisplayPresetKind> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain whatever has accumulated and keep only the newest request. Superseded presets
                // are never written: the display converges on the last thing the user asked for.
                DisplayPresetKind preset = CurrentPreset;

                while (reader.TryRead(out DisplayPresetKind queued))
                {
                    preset = queued;
                }

                await ApplyToSelectedAsync(preset, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Error, "display.worker.failed", exception: exception);
            SetStatus(new ModuleStatus(ModuleState.Faulted, exception.Message, "display.worker.failed"));
        }
    }

    /// <summary>
    /// The module is only <see cref="ModuleState.Active"/> while it has something to act on; with no
    /// usable display selected it is enabled but passing through, which is what Bypass means.
    /// </summary>
    private ModuleStatus SummarizeSelection()
    {
        // Counted as connected as well as selected: a monitor the user picked but that is unplugged
        // is still part of the selection, yet the module is not driving anything.
        int selected = Displays.Count(row => row.Selected && row.Display.IsConnected);

        if (selected == 0)
        {
            return new ModuleStatus(
                ModuleState.Bypass,
                "No compatible display is selected.",
                "display.selection.empty");
        }

        return new ModuleStatus(
            ModuleState.Active,
            string.Create(CultureInfo.InvariantCulture, $"{selected} display(s) selected."));
    }

    private bool IsSelected(string stableId) =>
        _options.SelectedDisplayIds.Contains(stableId, StringComparer.Ordinal);

    /// <summary>How many displays have had their original ramp captured.</summary>
    private int OriginalCount
    {
        get
        {
            lock (_stateGate)
            {
                return _originals.Count;
            }
        }
    }

    private bool TryGetOriginal(string stableId, [NotNullWhen(true)] out GammaRamp? original)
    {
        lock (_stateGate)
        {
            return _originals.TryGetValue(stableId, out original);
        }
    }

    private void SetOriginal(string stableId, GammaRamp ramp)
    {
        lock (_stateGate)
        {
            _originals[stableId] = ramp;
        }
    }

    private void RemoveOriginal(string stableId)
    {
        lock (_stateGate)
        {
            _originals.Remove(stableId);
        }
    }

    private void SetLastWrittenFingerprint(string stableId, string fingerprint)
    {
        lock (_stateGate)
        {
            _lastWrittenFingerprints[stableId] = fingerprint;
        }
    }

    private void ClearCaptures()
    {
        lock (_stateGate)
        {
            _originals.Clear();
            _lastWrittenFingerprints.Clear();
        }
    }

    /// <summary>
    /// A copy of the captured ramps and their last-written fingerprints, taken under the state gate
    /// so a caller can act on a consistent set while the worker keeps writing.
    /// </summary>
    private (string StableId, GammaRamp Original, string Fingerprint)[] SnapshotCaptures()
    {
        lock (_stateGate)
        {
            return [.. _originals.Select(pair => (
                pair.Key,
                pair.Value,
                _lastWrittenFingerprints.TryGetValue(pair.Key, out string? written) ? written : string.Empty))];
        }
    }

    private void SetDisplays(IReadOnlyList<DisplayStatus> displays)
    {
        lock (_stateGate)
        {
            _displays = displays;
        }

        RaiseDisplaysChanged();
    }

    private void SetStatus(ModuleStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private void RaiseDisplaysChanged() => DisplaysChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DisplayModule));
        }
    }
}
