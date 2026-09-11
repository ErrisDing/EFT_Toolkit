using System.Runtime.InteropServices;

namespace EftToolkit.Display.Interop;

/// <summary>
/// Structures from the Windows SDK display-configuration API. Sizes and field offsets mirror the
/// SDK headers exactly; the test project asserts every size so a drift here fails the build.
/// </summary>
internal enum DisplayConfigDeviceInfoType
{
    GetSourceName = 1,
    GetTargetName = 2,
    GetAdapterName = 4,
    GetAdvancedColorInfo = 9,
}

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;

    public readonly long ToInt64() => ((long)HighPart << 32) | LowPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigRational
{
    public uint Numerator;
    public uint Denominator;
}

/// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER: four fields covering 20 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigDeviceInfoHeader
{
    public DisplayConfigDeviceInfoType Type;
    public uint Size;
    public Luid AdapterId;
    public uint Id;
}

/// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME: header plus a 32 character GDI device name.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayConfigSourceDeviceName
{
    public const int NameLength = 32;

    public DisplayConfigDeviceInfoHeader Header;
    public fixed char ViewGdiDeviceName[NameLength];
}

/// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME: header plus the EDID identifiers and the monitor path.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayConfigTargetDeviceName
{
    public const int FriendlyNameLength = 64;
    public const int DevicePathLength = 128;

    public DisplayConfigDeviceInfoHeader Header;
    public uint Flags;
    public uint OutputTechnology;
    public ushort EdidManufactureId;
    public ushort EdidProductCodeId;
    public uint ConnectorInstance;
    public fixed char MonitorFriendlyDeviceName[FriendlyNameLength];
    public fixed char MonitorDevicePath[DevicePathLength];
}

/// <summary>
/// DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO. <see cref="Value"/> is a bitfield: bit 0 is
/// advancedColorSupported, bit 1 is advancedColorEnabled, bit 2 is wideColorEnforced and bit 3 is
/// advancedColorForceDisabled.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigAdvancedColorInfo
{
    public const uint AdvancedColorSupported = 0x1;
    public const uint AdvancedColorEnabled = 0x2;

    public DisplayConfigDeviceInfoHeader Header;
    public uint Value;
    public uint ColorEncoding;
    public uint BitsPerColorChannel;

    public readonly bool IsSupported => (Value & AdvancedColorSupported) != 0;
    public readonly bool IsEnabled => (Value & AdvancedColorEnabled) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathSourceInfo
{
    public Luid AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathTargetInfo
{
    public Luid AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public DisplayConfigRational RefreshRate;
    public uint ScanLineOrdering;

    /// <summary>A Win32 BOOL, which is four bytes wide rather than one.</summary>
    public int TargetAvailable;

    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathInfo
{
    public DisplayConfigPathSourceInfo SourceInfo;
    public DisplayConfigPathTargetInfo TargetInfo;
    public uint Flags;
}

/// <summary>
/// DISPLAYCONFIG_MODE_INFO. Only the header is ever read, so the trailing union of mode
/// descriptions is represented as opaque padding sized to its largest member
/// (DISPLAYCONFIG_TARGET_MODE, 48 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct DisplayConfigModeInfo
{
    public uint InfoType;
    public uint Id;
    public Luid AdapterId;
}

internal static class DisplayConfigConstants
{
    /// <summary>
    /// Set in <see cref="DisplayConfigPathInfo.Flags"/> for a path that is driving a display. This is
    /// deliberately not the target's status flags: <c>DISPLAYCONFIG_TARGET_IN_USE</c> is a different
    /// bit at the same value in the other structure, so the two are easy to confuse.
    /// </summary>
    internal const uint PathActive = 0x00000001;

    /// <summary>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED, an indirect (network or virtual) display.</summary>
    internal const uint OutputTechnologyIndirectWired = 0x80000002;

    internal const uint OutputTechnologyIndirectVirtual = 0x80000003;

    internal static bool IsIndirect(uint outputTechnology) =>
        outputTechnology is OutputTechnologyIndirectWired or OutputTechnologyIndirectVirtual;
}
