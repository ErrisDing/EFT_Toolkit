using System.Runtime.InteropServices;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Interop;

namespace EftToolkit.Tests.Display;

/// <summary>
/// These interop structures are never marshalled by the runtime: the gateway allocates raw buffers and
/// reads fields back by pointer. That means a wrong size or offset would not throw, it would silently
/// read the wrong bytes out of the display driver's response. The sizes and offsets below are taken
/// from the Windows SDK headers, and asserting them is what keeps the two in step.
/// </summary>
public class Win32StructureLayoutTests
{
    [Fact]
    public void Luid_is_eight_bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<Luid>());
    }

    [Fact]
    public void Rational_is_two_thirty_two_bit_words()
    {
        Assert.Equal(8, Marshal.SizeOf<DisplayConfigRational>());
    }

    [Fact]
    public void DeviceInfoHeader_is_twenty_bytes()
    {
        // DISPLAYCONFIG_DEVICE_INFO_HEADER: type (4) + size (4) + adapterId (8) + id (4).
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigDeviceInfoHeader>());
        Assert.Equal(0, Marshal.OffsetOf<DisplayConfigDeviceInfoHeader>(nameof(DisplayConfigDeviceInfoHeader.Type)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<DisplayConfigDeviceInfoHeader>(nameof(DisplayConfigDeviceInfoHeader.Size)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DisplayConfigDeviceInfoHeader>(nameof(DisplayConfigDeviceInfoHeader.AdapterId)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<DisplayConfigDeviceInfoHeader>(nameof(DisplayConfigDeviceInfoHeader.Id)).ToInt32());
    }

    [Fact]
    public void SourceDeviceName_is_twenty_bytes_of_header_plus_thirty_two_wide_characters()
    {
        Assert.Equal(84, Marshal.SizeOf<DisplayConfigSourceDeviceName>());
        Assert.Equal(
            20,
            Marshal.OffsetOf<DisplayConfigSourceDeviceName>(nameof(DisplayConfigSourceDeviceName.ViewGdiDeviceName)).ToInt32());
    }

    [Fact]
    public void TargetDeviceName_matches_the_sdk_layout()
    {
        // 20 header + 4 flags + 4 outputTechnology + 2 edidManufacture + 2 edidProduct + 4 connector
        // + 128 friendly name + 256 device path.
        Assert.Equal(420, Marshal.SizeOf<DisplayConfigTargetDeviceName>());
        Assert.Equal(20, Marshal.OffsetOf<DisplayConfigTargetDeviceName>(nameof(DisplayConfigTargetDeviceName.Flags)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<DisplayConfigTargetDeviceName>(nameof(DisplayConfigTargetDeviceName.EdidManufactureId)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<DisplayConfigTargetDeviceName>(nameof(DisplayConfigTargetDeviceName.ConnectorInstance)).ToInt32());
        Assert.Equal(
            36,
            Marshal.OffsetOf<DisplayConfigTargetDeviceName>(nameof(DisplayConfigTargetDeviceName.MonitorFriendlyDeviceName)).ToInt32());
        Assert.Equal(
            164,
            Marshal.OffsetOf<DisplayConfigTargetDeviceName>(nameof(DisplayConfigTargetDeviceName.MonitorDevicePath)).ToInt32());
    }

    [Fact]
    public void AdvancedColorInfo_is_header_plus_three_thirty_two_bit_words()
    {
        Assert.Equal(32, Marshal.SizeOf<DisplayConfigAdvancedColorInfo>());
        Assert.Equal(20, Marshal.OffsetOf<DisplayConfigAdvancedColorInfo>(nameof(DisplayConfigAdvancedColorInfo.Value)).ToInt32());
    }

    [Fact]
    public void PathSourceInfo_is_twenty_bytes()
    {
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigPathSourceInfo>());
    }

    [Fact]
    public void PathTargetInfo_includes_the_rational_and_a_four_byte_bool()
    {
        // 8 adapter + 4 id + 4 modeIdx + 4 outputTechnology + 4 rotation + 4 scaling + 8 refreshRate
        // + 4 scanLineOrdering + 4 targetAvailable + 4 statusFlags.
        Assert.Equal(48, Marshal.SizeOf<DisplayConfigPathTargetInfo>());
        Assert.Equal(28, Marshal.OffsetOf<DisplayConfigPathTargetInfo>(nameof(DisplayConfigPathTargetInfo.RefreshRate)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<DisplayConfigPathTargetInfo>(nameof(DisplayConfigPathTargetInfo.TargetAvailable)).ToInt32());
    }

    [Fact]
    public void PathInfo_is_source_plus_target_plus_flags()
    {
        Assert.Equal(72, Marshal.SizeOf<DisplayConfigPathInfo>());
        Assert.Equal(20, Marshal.OffsetOf<DisplayConfigPathInfo>(nameof(DisplayConfigPathInfo.TargetInfo)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<DisplayConfigPathInfo>(nameof(DisplayConfigPathInfo.Flags)).ToInt32());
    }

    [Fact]
    public void ModeInfo_is_padded_to_its_largest_union_member()
    {
        // infoType (4) + id (4) + adapterId (8) plus DISPLAYCONFIG_TARGET_MODE (48): the mode array is
        // only ever allocated, never read, so the union itself is opaque padding. Note the adapter LUID
        // follows the id here, unlike the device-info header where it precedes it.
        Assert.Equal(64, Marshal.SizeOf<DisplayConfigModeInfo>());
        Assert.Equal(8, Marshal.OffsetOf<DisplayConfigModeInfo>(nameof(DisplayConfigModeInfo.AdapterId)).ToInt32());
    }

    [Fact]
    public void The_gamma_ramp_buffer_is_exactly_fifteen_hundred_and_thirty_six_bytes()
    {
        // Three channels of 256 sixteen-bit entries, which is the fixed size SetDeviceGammaRamp expects.
        Assert.Equal(1536, 3 * GammaRamp.ChannelLength * sizeof(ushort));
    }

    [Fact]
    public void DisplayDevice_matches_the_sdk_layout()
    {
        // cb (4) + DeviceName[32] (64) + DeviceString[128] (256) + StateFlags (4)
        // + DeviceID[128] (256) + DeviceKey[128] (256).
        Assert.Equal(840, Marshal.SizeOf<User32.DisplayDevice>());
        Assert.Equal(0, Marshal.OffsetOf<User32.DisplayDevice>(nameof(User32.DisplayDevice.Size)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<User32.DisplayDevice>(nameof(User32.DisplayDevice.DeviceName)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<User32.DisplayDevice>(nameof(User32.DisplayDevice.DeviceString)).ToInt32());
        Assert.Equal(324, Marshal.OffsetOf<User32.DisplayDevice>(nameof(User32.DisplayDevice.StateFlags)).ToInt32());
        Assert.Equal(328, Marshal.OffsetOf<User32.DisplayDevice>(nameof(User32.DisplayDevice.DeviceId)).ToInt32());
        Assert.Equal(584, Marshal.OffsetOf<User32.DisplayDevice>(nameof(User32.DisplayDevice.DeviceKey)).ToInt32());
    }

    [Fact]
    public void DisplayDevice_sets_the_size_field_the_api_requires()
    {
        Assert.Equal((uint)Marshal.SizeOf<User32.DisplayDevice>(), User32.DisplayDevice.Create().Size);
    }

    [Fact]
    public void Indirect_output_technologies_are_recognised()
    {
        Assert.True(DisplayConfigConstants.IsIndirect(DisplayConfigConstants.OutputTechnologyIndirectWired));
        Assert.True(DisplayConfigConstants.IsIndirect(DisplayConfigConstants.OutputTechnologyIndirectVirtual));
        Assert.False(DisplayConfigConstants.IsIndirect(10)); // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL
        Assert.False(DisplayConfigConstants.IsIndirect(0)); // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER
    }
}
