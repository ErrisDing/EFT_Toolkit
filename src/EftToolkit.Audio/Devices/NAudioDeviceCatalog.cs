using EftToolkit.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace EftToolkit.Audio.Devices;

/// <summary>
/// The production endpoint catalog, built on the Windows audio endpoint APIs.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here opens a stream. Reading an endpoint's mix format and listing its sessions are
/// properties of the endpoint, and the stream session is the only thing that ever initialises an
/// <c>AudioClient</c> in shared mode.
/// </para>
/// <para>
/// Every <see cref="MMDevice"/> and <see cref="AudioClient"/> this class obtains is released before
/// it returns. Holding one open would keep the endpoint from being reconfigured while the toolkit
/// ran.
/// </para>
/// </remarks>
public sealed class NAudioDeviceCatalog : IAudioDeviceCatalog
{
    private static readonly DataFlow[] Flows = [DataFlow.Render, DataFlow.Capture];

    private readonly IAppLogger? _logger;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly EndpointNotificationClient _notifications;
    private readonly object _gate = new();

    private bool _disposed;

    public NAudioDeviceCatalog(IAppLogger? logger = null)
    {
        _logger = logger;
        _notifications = new EndpointNotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioEndpointDescriptor>> GetEndpointsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run<IReadOnlyList<AudioEndpointDescriptor>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Enumerate();
            },
            cancellationToken);
    }

    public Task<IReadOnlySet<int>> GetActiveProcessIdsAsync(
        string renderEndpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderEndpointId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run<IReadOnlySet<int>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReadSessionProcessIds(renderEndpointId);
            },
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_notifications);
        }
        catch (Exception exception)
        {
            // Shutting down is not the moment to fail: the callback is going away with the process
            // either way.
            _logger?.Write(LogLevel.Debug, "audio.devices.unregisterFailed", exception: exception);
        }

        _enumerator.Dispose();
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<AudioEndpointDescriptor> Enumerate()
    {
        List<AudioEndpointDescriptor> endpoints = [];

        foreach (DataFlow flow in Flows)
        {
            if (TryEnumerateFlow(flow, endpoints) == false)
            {
                continue;
            }
        }

        return endpoints;
    }

    private bool TryEnumerateFlow(DataFlow flow, List<AudioEndpointDescriptor> endpoints)
    {
        MMDeviceCollection devices;

        try
        {
            // Active only. A disabled or unplugged endpoint still has an ID and a friendly name, and
            // offering one in the panel would only lead to a route that fails validation later.
            devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        }
        catch (Exception exception)
        {
            _logger?.Write(
                LogLevel.Warning,
                "audio.devices.enumerateFailed",
                new Dictionary<string, object?> { ["flow"] = flow.ToString() },
                exception);

            return false;
        }

        for (int index = 0; index < devices.Count; index++)
        {
            MMDevice? device = null;

            try
            {
                device = devices[index];
                endpoints.Add(Describe(device, flow));
            }
            catch (Exception exception)
            {
                // An endpoint can be removed between the enumeration and this read. Skipping it is
                // correct: it is not there any more.
                _logger?.Write(
                    LogLevel.Debug,
                    "audio.devices.describeFailed",
                    new Dictionary<string, object?> { ["flow"] = flow.ToString(), ["index"] = index },
                    exception);
            }
            finally
            {
                device?.Dispose();
            }
        }

        return true;
    }

    private AudioEndpointDescriptor Describe(MMDevice device, DataFlow flow)
    {
        (int channels, int sampleRate) = ReadMixFormat(device);

        return new AudioEndpointDescriptor(
            Id: device.ID,
            FriendlyName: device.FriendlyName,
            Flow: flow == DataFlow.Render ? AudioDataFlow.Render : AudioDataFlow.Capture,
            // Everything enumerated here was requested as active, but the state is read anyway
            // rather than assumed, so a device that went away mid-enumeration is reported honestly.
            IsActive: device.State == DeviceState.Active,
            Channels: channels,
            SampleRate: sampleRate);
    }

    /// <summary>
    /// Reads the endpoint's mix format, which is the format a shared-mode stream will be handed.
    /// </summary>
    private (int Channels, int SampleRate) ReadMixFormat(MMDevice device)
    {
        AudioClient? client = null;

        try
        {
            client = device.AudioClient;
            WaveFormat format = client.MixFormat;
            return (format.Channels, format.SampleRate);
        }
        catch (Exception exception)
        {
            // Reported as zero channels, which validation refuses. An endpoint whose format cannot
            // be read is not one this toolkit can reason about.
            _logger?.Write(LogLevel.Debug, "audio.devices.mixFormatUnavailable", exception: exception);
            return (0, 0);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private IReadOnlySet<int> ReadSessionProcessIds(string renderEndpointId)
    {
        HashSet<int> processIds = [];

        MMDevice? device = FindActiveRenderEndpoint(renderEndpointId);

        if (device is null)
        {
            _logger?.Write(
                LogLevel.Debug,
                "audio.devices.endpointMissing",
                new Dictionary<string, object?> { ["endpointId"] = renderEndpointId });

            return processIds;
        }

        try
        {
            AudioSessionManager sessions = device.AudioSessionManager;

            // The session list is a snapshot taken when the manager was created, so it has to be
            // refreshed before it can be trusted to name what is playing right now.
            sessions.RefreshSessions();

            SessionCollection collection = sessions.Sessions;

            for (int index = 0; index < collection.Count; index++)
            {
                CollectSessionProcessId(collection, index, renderEndpointId, processIds);
            }
        }
        catch (Exception exception)
        {
            _logger?.Write(
                LogLevel.Warning,
                "audio.devices.sessionsFailed",
                new Dictionary<string, object?> { ["endpointId"] = renderEndpointId },
                exception);
        }
        finally
        {
            device.Dispose();
        }

        return processIds;
    }

    private void CollectSessionProcessId(
        SessionCollection collection,
        int index,
        string renderEndpointId,
        HashSet<int> processIds)
    {
        AudioSessionControl? session = null;

        try
        {
            session = collection[index];

            // System sounds belong to no application the user picked, and an expired session names
            // a process that has already gone.
            if (session.IsSystemSoundsSession ||
                session.State == AudioSessionState.AudioSessionStateExpired)
            {
                return;
            }

            uint processId = session.GetProcessID;

            if (processId != 0)
            {
                processIds.Add((int)processId);
            }
        }
        catch (Exception exception)
        {
            // A session can end between being listed and being read, and a session whose process is
            // protected returns no ID at all. Neither is worth more than a debug line: the set is
            // rebuilt on the next poll.
            _logger?.Write(
                LogLevel.Debug,
                "audio.devices.sessionUnreadable",
                new Dictionary<string, object?> { ["endpointId"] = renderEndpointId },
                exception);
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>Finds one active render endpoint by ID, leaving the caller to dispose it.</summary>
    private MMDevice? FindActiveRenderEndpoint(string endpointId)
    {
        MMDeviceCollection devices;

        try
        {
            devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        }
        catch (Exception exception)
        {
            _logger?.Write(
                LogLevel.Warning,
                "audio.devices.enumerateFailed",
                new Dictionary<string, object?> { ["flow"] = nameof(DataFlow.Render) },
                exception);

            return null;
        }

        try
        {
            for (int index = 0; index < devices.Count; index++)
            {
                MMDevice device = devices[index];

                if (string.Equals(device.ID, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }

                device.Dispose();
            }
        }
        catch (Exception exception)
        {
            // A device that disappears while the list is being walked. The endpoint being looked for
            // is simply no longer available.
            _logger?.Write(LogLevel.Debug, "audio.devices.describeFailed", exception: exception);
        }

        return null;
    }

    private void RaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            // This arrives on a COM thread, where an escaping exception would be swallowed by the
            // operating system and take the notification with it. It is logged instead.
            _logger?.Write(LogLevel.Error, "audio.devices.devicesChangedHandlerFailed", exception: exception);
        }
    }

    /// <summary>
    /// Reports endpoint changes. Windows calls these on its own thread, and none of them opens or
    /// re-reads a stream: they only say that the list of endpoints is no longer current.
    /// </summary>
    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly NAudioDeviceCatalog _owner;

        internal EndpointNotificationClient(NAudioDeviceCatalog owner)
        {
            _owner = owner;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.RaiseDevicesChanged();

        public void OnDeviceAdded(string pwstrDeviceId) => _owner.RaiseDevicesChanged();

        public void OnDeviceRemoved(string deviceId) => _owner.RaiseDevicesChanged();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.RaiseDevicesChanged();

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Property changes include per-endpoint volume and mute, which change constantly and say
            // nothing about whether the route is still usable. Reacting to them would re-validate
            // the route every time the user touches the volume wheel.
        }
    }
}
