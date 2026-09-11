using System.Runtime.InteropServices;

namespace EftToolkit.Display.Interop;

/// <summary>
/// Display topology entry points. These are exported from user32, which is why the display-config
/// declarations live here rather than beside the GDI ones.
/// </summary>
internal static partial class User32
{
    internal const uint QdcOnlyActivePaths = 0x00000002;

    internal const uint DisplayDeviceAttachedToDesktop = 0x00000001;
    internal const uint DisplayDevicePrimaryDevice = 0x00000004;
    internal const uint DisplayDeviceMirroringDriver = 0x00000008;

    /// <summary>One-based success indicator; zero is <c>ERROR_SUCCESS</c>, any other value is a Win32 error code.</summary>
    private const int ErrorSuccess = 0;

    /// <summary>
    /// DISPLAY_DEVICE. The string fields are declared with <see cref="MarshalAsAttribute"/> because
    /// source-generated marshalling does not support inline by-value string buffers, so this one
    /// entry point stays on the classic marshaller.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public const int DeviceNameLength = 32;
        public const int DeviceStringLength = 128;
        public const int DeviceIdLength = 128;
        public const int DeviceKeyLength = 128;

        /// <summary>The caller must set this to the structure size before every call.</summary>
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameLength)]
        public string? DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceStringLength)]
        public string? DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceIdLength)]
        public string? DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceKeyLength)]
        public string? DeviceKey;

        public static DisplayDevice Create() => new() { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeArrayElements);

    /// <summary>
    /// Buffers are passed as raw pointers rather than marshalled arrays so the caller controls
    /// allocation and the structures are read back explicitly.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        nint pathInfoArray,
        ref uint numModeInfoArrayElements,
        nint modeInfoArray,
        out uint currentTopologyId);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int DisplayConfigGetDeviceInfo(nint requestPacket);

    internal static bool IsSuccess(int result) => result == ErrorSuccess;
}
