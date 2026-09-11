using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Interop;

namespace EftToolkit.Display.Devices;

/// <summary>
/// The production gamma gateway. Every entry point runs on the thread pool because the Win32 calls
/// block on the display driver, and every call opens and closes its own device context: a display
/// context is never cached, shared, or reused across calls.
/// </summary>
public sealed class Win32DisplayGammaGateway : IDisplayGammaGateway
{
    /// <summary>Three channels of <see cref="GammaRamp.ChannelLength"/> sixteen-bit entries.</summary>
    private const int ChannelStride = GammaRamp.ChannelLength * sizeof(ushort);

    private const int RampByteLength = 3 * ChannelStride;

    /// <summary>ERROR_INSUFFICIENT_BUFFER, returned when the topology changes mid-query.</summary>
    private const int ErrorInsufficientBuffer = 122;

    private const int QueryAttempts = 3;

    private readonly IAppLogger? _logger;

    public Win32DisplayGammaGateway(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<DisplayDescriptor>> EnumerateAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<DisplayDescriptor>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Enumerate();
            },
            cancellationToken);
    }

    public Task<GammaRamp> ReadAsync(DisplayDescriptor display, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryReadRamp(display.GdiDeviceName, out GammaRamp? ramp))
                {
                    throw new InvalidOperationException(
                        $"The gamma ramp of '{display.GdiDeviceName}' could not be read (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                return ramp;
            },
            cancellationToken);
    }

    public Task<GammaWriteResult> WriteAsync(
        DisplayDescriptor display,
        GammaRamp ramp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(ramp);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                nint buffer = Marshal.AllocHGlobal(RampByteLength);
                try
                {
                    WriteRamp(buffer, ramp);

                    int win32Error = 0;
                    bool accepted = TryWithDeviceContext(
                        display.GdiDeviceName,
                        hdc =>
                        {
                            bool written = Gdi32.SetDeviceGammaRamp(hdc, buffer);
                            if (!written)
                            {
                                // Captured before the context is released, because closing the context
                                // overwrites the thread's last-error value.
                                win32Error = Marshal.GetLastWin32Error();
                            }

                            return written;
                        });

                    if (!accepted)
                    {
                        _logger?.Write(
                            LogLevel.Warning,
                            "display.gamma.writeRejected",
                            new Dictionary<string, object?>
                            {
                                ["displayId"] = display.StableId,
                                ["win32Error"] = win32Error,
                            });

                        return GammaWriteResult.Rejected(
                            win32Error,
                            $"The display driver rejected the gamma ramp for '{display.FriendlyName}' (Win32 error {win32Error}).");
                    }

                    // A driver may accept the call and still clamp the ramp. Reading it back on a fresh
                    // context is the only way to tell a real write from an accepted no-op.
                    if (!TryReadRamp(display.GdiDeviceName, out GammaRamp? readback))
                    {
                        return GammaWriteResult.AcceptedButUnverified(
                            $"The gamma ramp for '{display.FriendlyName}' was written but could not be read back for verification.");
                    }

                    bool matched = GammaRampFingerprint.Compute(readback) == GammaRampFingerprint.Compute(ramp);

                    if (!matched)
                    {
                        _logger?.Write(
                            LogLevel.Warning,
                            "display.gamma.readbackMismatch",
                            new Dictionary<string, object?>
                            {
                                ["displayId"] = display.StableId,
                                ["expected"] = GammaRampFingerprint.Compute(ramp),
                                ["actual"] = GammaRampFingerprint.Compute(readback),
                            });

                        return GammaWriteResult.AcceptedButUnverified(
                            $"The display driver accepted the gamma ramp for '{display.FriendlyName}' but returned a different ramp on readback.");
                    }

                    return GammaWriteResult.Accepted();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            },
            cancellationToken);
    }

    private IReadOnlyList<DisplayDescriptor> Enumerate()
    {
        IReadOnlyList<DisplayDescriptor> displays = EnumerateViaDisplayConfig();

        if (displays.Count > 0)
        {
            return displays;
        }

        // The display-configuration API is not dependable in the presence of third-party virtual
        // display adapters: they can make QueryDisplayConfig fail outright while the GDI device names
        // remain perfectly usable. Falling back to GDI keeps the toolkit working on machines that are
        // otherwise misreported as having no displays at all.
        _logger?.Write(LogLevel.Information, "display.enumerate.displayConfigEmpty");

        return EnumerateViaGdi();
    }

    /// <summary>
    /// Enumerates through the classic GDI display devices. This path cannot report HDR state or
    /// output technology, so displays found this way are reported as non-HDR.
    /// </summary>
    private IReadOnlyList<DisplayDescriptor> EnumerateViaGdi()
    {
        List<DisplayDescriptor> displays = [];

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            User32.DisplayDevice adapter = User32.DisplayDevice.Create();

            if (!User32.EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            // A display that is not attached to the desktop cannot receive a gamma ramp, and the
            // mirroring driver is the pseudo-adapter behind clone mode rather than a real display.
            if ((adapter.StateFlags & User32.DisplayDeviceAttachedToDesktop) == 0 ||
                (adapter.StateFlags & User32.DisplayDeviceMirroringDriver) != 0)
            {
                continue;
            }

            string gdiDeviceName = adapter.DeviceName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(gdiDeviceName))
            {
                continue;
            }

            (string friendlyName, string devicePath) = ReadMonitorIdentity(gdiDeviceName);

            displays.Add(new DisplayDescriptor(
                StableId: DisplayDescriptor.CreateStableId(devicePath, 0, 0, gdiDeviceName),
                GdiDeviceName: gdiDeviceName,
                FriendlyName: DisplayDescriptor.CreateFriendlyName(friendlyName, gdiDeviceName),
                IsConnected: true,
                IsHdr: false,
                SupportsGammaRamp: TryReadRamp(gdiDeviceName, out _)));
        }

        _logger?.Write(
            LogLevel.Information,
            "display.enumerate.gdi",
            new Dictionary<string, object?> { ["displayCount"] = displays.Count });

        return displays;
    }

    /// <summary>Reads the monitor attached to an adapter: its EDID device path and its friendly name.</summary>
    private static (string FriendlyName, string DevicePath) ReadMonitorIdentity(string gdiDeviceName)
    {
        User32.DisplayDevice monitor = User32.DisplayDevice.Create();

        if (!User32.EnumDisplayDevices(gdiDeviceName, 0, ref monitor, 0))
        {
            return (string.Empty, string.Empty);
        }

        return (monitor.DeviceString ?? string.Empty, monitor.DeviceId ?? string.Empty);
    }

    private IReadOnlyList<DisplayDescriptor> EnumerateViaDisplayConfig()
    {
        for (int attempt = 1; attempt <= QueryAttempts; attempt++)
        {
            if (!TryQueryPaths(out DisplayConfigPathInfo[] paths, out bool retryable))
            {
                if (retryable && attempt < QueryAttempts)
                {
                    continue;
                }

                return [];
            }

            List<DisplayDescriptor> displays = new(paths.Length);
            foreach (DisplayConfigPathInfo path in paths)
            {
                // The paths are already filtered by QDC_ONLY_ACTIVE_PATHS, but a display can be
                // unplugged between the query and this loop, so the flag is still worth honouring.
                if ((path.Flags & DisplayConfigConstants.PathActive) == 0)
                {
                    continue;
                }

                displays.Add(Describe(path));
            }

            return displays;
        }

        return [];
    }

    private bool TryQueryPaths(out DisplayConfigPathInfo[] paths, out bool retryable)
    {
        paths = [];
        retryable = false;

        int result = User32.GetDisplayConfigBufferSizes(
            User32.QdcOnlyActivePaths,
            out uint pathCount,
            out uint modeCount);

        if (!User32.IsSuccess(result))
        {
            _logger?.Write(
                LogLevel.Warning,
                "display.enumerate.bufferSizesFailed",
                new Dictionary<string, object?> { ["win32Error"] = result });

            return false;
        }

        int pathStride = Marshal.SizeOf<DisplayConfigPathInfo>();
        int modeStride = Marshal.SizeOf<DisplayConfigModeInfo>();

        nint pathBuffer = 0;
        nint modeBuffer = 0;

        try
        {
            pathBuffer = Marshal.AllocHGlobal(BufferBytes(pathCount, pathStride));
            modeBuffer = Marshal.AllocHGlobal(BufferBytes(modeCount, modeStride));

            uint queriedPaths = pathCount;
            uint queriedModes = modeCount;

            result = User32.QueryDisplayConfig(
                User32.QdcOnlyActivePaths,
                ref queriedPaths,
                pathBuffer,
                ref queriedModes,
                modeBuffer,
                out _);

            if (result == ErrorInsufficientBuffer)
            {
                // The topology changed between sizing the buffers and filling them. Asking again with
                // fresh sizes is the documented remedy; anything else races the user unplugging a cable.
                retryable = true;
                return false;
            }

            if (!User32.IsSuccess(result))
            {
                _logger?.Write(
                    LogLevel.Warning,
                    "display.enumerate.queryFailed",
                    new Dictionary<string, object?> { ["win32Error"] = result });

                return false;
            }

            DisplayConfigPathInfo[] read = new DisplayConfigPathInfo[queriedPaths];
            for (uint index = 0; index < queriedPaths; index++)
            {
                read[index] = Marshal.PtrToStructure<DisplayConfigPathInfo>(pathBuffer + ((int)index * pathStride));
            }

            paths = read;
            return true;
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Error, "display.enumerate.failed", exception: exception);
            return false;
        }
        finally
        {
            if (pathBuffer != 0)
            {
                Marshal.FreeHGlobal(pathBuffer);
            }

            if (modeBuffer != 0)
            {
                Marshal.FreeHGlobal(modeBuffer);
            }
        }
    }

    private DisplayDescriptor Describe(DisplayConfigPathInfo path)
    {
        string gdiDeviceName = ReadSourceGdiName(path);
        (string friendlyName, string devicePath) = ReadTargetNames(path);

        bool isIndirect = DisplayConfigConstants.IsIndirect(path.TargetInfo.OutputTechnology);

        return new DisplayDescriptor(
            StableId: DisplayDescriptor.CreateStableId(
                devicePath,
                path.TargetInfo.AdapterId.ToInt64(),
                path.TargetInfo.Id),
            GdiDeviceName: gdiDeviceName,
            FriendlyName: DisplayDescriptor.CreateFriendlyName(friendlyName, gdiDeviceName),
            // The buffer is requested with QDC_ONLY_ACTIVE_PATHS, so every path returned here is
            // driving a display right now. Displays that disappear while selected are reported by the
            // module, which compares the stored selection against this list.
            IsConnected: true,
            IsHdr: ReadHdrState(path),
            // Indirect (network and virtual) displays accept the ramp call and then ignore it, so they
            // are excluded up front rather than offered and silently failing.
            SupportsGammaRamp: !isIndirect && TryReadRamp(gdiDeviceName, out _));
    }

    private static unsafe string ReadSourceGdiName(DisplayConfigPathInfo path)
    {
        int size = Marshal.SizeOf<DisplayConfigSourceDeviceName>();
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            DisplayConfigSourceDeviceName request = default;
            request.Header.Type = DisplayConfigDeviceInfoType.GetSourceName;
            request.Header.Size = (uint)size;
            request.Header.AdapterId = path.SourceInfo.AdapterId;
            request.Header.Id = path.SourceInfo.Id;

            Marshal.StructureToPtr(request, buffer, fDeleteOld: false);

            if (!User32.IsSuccess(User32.DisplayConfigGetDeviceInfo(buffer)))
            {
                return string.Empty;
            }

            DisplayConfigSourceDeviceName* response = (DisplayConfigSourceDeviceName*)buffer;
            return ReadFixedString(response->ViewGdiDeviceName, DisplayConfigSourceDeviceName.NameLength);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe (string FriendlyName, string DevicePath) ReadTargetNames(DisplayConfigPathInfo path)
    {
        int size = Marshal.SizeOf<DisplayConfigTargetDeviceName>();
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            DisplayConfigTargetDeviceName request = default;
            request.Header.Type = DisplayConfigDeviceInfoType.GetTargetName;
            request.Header.Size = (uint)size;
            request.Header.AdapterId = path.TargetInfo.AdapterId;
            request.Header.Id = path.TargetInfo.Id;

            Marshal.StructureToPtr(request, buffer, fDeleteOld: false);

            if (!User32.IsSuccess(User32.DisplayConfigGetDeviceInfo(buffer)))
            {
                return (string.Empty, string.Empty);
            }

            DisplayConfigTargetDeviceName* response = (DisplayConfigTargetDeviceName*)buffer;
            return (
                ReadFixedString(response->MonitorFriendlyDeviceName, DisplayConfigTargetDeviceName.FriendlyNameLength),
                ReadFixedString(response->MonitorDevicePath, DisplayConfigTargetDeviceName.DevicePathLength));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool ReadHdrState(DisplayConfigPathInfo path)
    {
        int size = Marshal.SizeOf<DisplayConfigAdvancedColorInfo>();
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            DisplayConfigAdvancedColorInfo request = default;
            request.Header.Type = DisplayConfigDeviceInfoType.GetAdvancedColorInfo;
            request.Header.Size = (uint)size;
            request.Header.AdapterId = path.TargetInfo.AdapterId;
            request.Header.Id = path.TargetInfo.Id;

            Marshal.StructureToPtr(request, buffer, fDeleteOld: false);

            if (!User32.IsSuccess(User32.DisplayConfigGetDeviceInfo(buffer)))
            {
                // Windows builds that predate the advanced-colour query return an error here. That is
                // not a reason to hide the display: it is reported as non-HDR and stays fully usable.
                _logger?.Write(
                    LogLevel.Debug,
                    "display.hdr.unavailable",
                    new Dictionary<string, object?> { ["targetId"] = path.TargetInfo.Id });

                return false;
            }

            DisplayConfigAdvancedColorInfo response = Marshal.PtrToStructure<DisplayConfigAdvancedColorInfo>(buffer);
            return response.IsSupported && response.IsEnabled;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadRamp(string gdiDeviceName, [NotNullWhen(true)] out GammaRamp? ramp)
    {
        ramp = null;

        nint buffer = Marshal.AllocHGlobal(RampByteLength);
        try
        {
            if (!TryWithDeviceContext(gdiDeviceName, hdc => Gdi32.GetDeviceGammaRamp(hdc, buffer)))
            {
                return false;
            }

            ramp = ReadRamp(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static GammaRamp ReadRamp(nint buffer)
    {
        ushort[] red = new ushort[GammaRamp.ChannelLength];
        ushort[] green = new ushort[GammaRamp.ChannelLength];
        ushort[] blue = new ushort[GammaRamp.ChannelLength];

        for (int index = 0; index < GammaRamp.ChannelLength; index++)
        {
            int offset = index * sizeof(ushort);
            red[index] = (ushort)Marshal.ReadInt16(buffer, offset);
            green[index] = (ushort)Marshal.ReadInt16(buffer, ChannelStride + offset);
            blue[index] = (ushort)Marshal.ReadInt16(buffer, (2 * ChannelStride) + offset);
        }

        return new GammaRamp(red, green, blue);
    }

    private static void WriteRamp(nint buffer, GammaRamp ramp)
    {
        for (int index = 0; index < GammaRamp.ChannelLength; index++)
        {
            int offset = index * sizeof(ushort);
            Marshal.WriteInt16(buffer, offset, unchecked((short)ramp.Red[index]));
            Marshal.WriteInt16(buffer, ChannelStride + offset, unchecked((short)ramp.Green[index]));
            Marshal.WriteInt16(buffer, (2 * ChannelStride) + offset, unchecked((short)ramp.Blue[index]));
        }
    }

    private static bool TryWithDeviceContext(string gdiDeviceName, Func<nint, bool> body)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return false;
        }

        nint deviceContext = Gdi32.CreateDC(null, gdiDeviceName, null, 0);
        if (deviceContext == 0)
        {
            return false;
        }

        try
        {
            return body(deviceContext);
        }
        finally
        {
            Gdi32.DeleteDC(deviceContext);
        }
    }

    private static unsafe string ReadFixedString(char* value, int capacity)
    {
        ReadOnlySpan<char> span = new(value, capacity);
        int terminator = span.IndexOf('\0');
        return terminator >= 0 ? new string(span[..terminator]) : new string(span);
    }

    private static int BufferBytes(uint count, int stride)
    {
        if (count == 0)
        {
            // AllocHGlobal(0) is not required to return a pointer that can be freed.
            return 1;
        }

        return checked((int)((long)count * stride));
    }
}
