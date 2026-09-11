using EftToolkit.Display.Devices;

namespace EftToolkit.Tests.Display;

public class DisplayDescriptorTests
{
    private const long AdapterLuid = 0x0000000100000002L;

    [Fact]
    public void CreateStableId_prefers_the_monitor_device_path()
    {
        string id = DisplayDescriptor.CreateStableId(
            @"\\?\DISPLAY#DEL41A2#5&2a1b3c4&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            AdapterLuid,
            4);

        Assert.Equal(
            @"\\?\DISPLAY#DEL41A2#5&2a1b3c4&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateStableId_falls_back_to_the_adapter_and_target(string? devicePath)
    {
        string id = DisplayDescriptor.CreateStableId(devicePath, AdapterLuid, 4);

        Assert.Equal("0000000100000002:4", id);
    }

    [Fact]
    public void The_fallback_id_distinguishes_targets_on_the_same_adapter()
    {
        string first = DisplayDescriptor.CreateStableId(null, AdapterLuid, 4);
        string second = DisplayDescriptor.CreateStableId(null, AdapterLuid, 5);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_fallback_id_distinguishes_adapters()
    {
        string first = DisplayDescriptor.CreateStableId(null, 1L, 4);
        string second = DisplayDescriptor.CreateStableId(null, 2L, 4);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateStableId_falls_back_to_the_gdi_name_when_the_adapter_reports_no_luid()
    {
        // The GDI enumeration path has no LUID or target id to offer, and inventing one would give
        // every such display the same identifier.
        string id = DisplayDescriptor.CreateStableId(null, 0, 0, @"\\.\DISPLAY2");

        Assert.Equal(@"\\.\DISPLAY2", id);
    }

    [Fact]
    public void The_gdi_fallback_is_only_used_after_the_adapter_identity()
    {
        Assert.Equal(
            "0000000100000002:4",
            DisplayDescriptor.CreateStableId(null, AdapterLuid, 4, @"\\.\DISPLAY1"));
    }

    [Fact]
    public void A_device_path_still_wins_over_every_other_source()
    {
        Assert.Equal(
            @"\\?\DISPLAY#DEL41A2#5&2a1b3c4&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            DisplayDescriptor.CreateStableId(
                @"\\?\DISPLAY#DEL41A2#5&2a1b3c4&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
                AdapterLuid,
                4,
                @"\\.\DISPLAY1"));
    }

    [Fact]
    public void CreateStableId_is_stable_for_equal_inputs()
    {
        Assert.Equal(
            DisplayDescriptor.CreateStableId(null, AdapterLuid, 4),
            DisplayDescriptor.CreateStableId(null, AdapterLuid, 4));
    }

    [Fact]
    public void CreateFriendlyName_prefers_the_driver_name()
    {
        Assert.Equal("DELL U2720Q", DisplayDescriptor.CreateFriendlyName("DELL U2720Q", @"\\.\DISPLAY1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateFriendlyName_falls_back_to_the_gdi_device_name(string? friendlyName)
    {
        Assert.Equal(@"\\.\DISPLAY1", DisplayDescriptor.CreateFriendlyName(friendlyName, @"\\.\DISPLAY1"));
    }

    [Fact]
    public void CreateFriendlyName_is_empty_when_nothing_is_known()
    {
        Assert.Equal(string.Empty, DisplayDescriptor.CreateFriendlyName(null, string.Empty));
        Assert.Equal(string.Empty, DisplayDescriptor.CreateFriendlyName("  ", "  "));
    }

    [Fact]
    public void Display_descriptors_with_equal_members_are_equal()
    {
        DisplayDescriptor first = new("id", @"\\.\DISPLAY1", "Monitor", true, false, true);
        DisplayDescriptor second = new("id", @"\\.\DISPLAY1", "Monitor", true, false, true);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_write_is_only_successful_when_the_readback_matched()
    {
        Assert.True(GammaWriteResult.Accepted().Succeeded);
        Assert.False(GammaWriteResult.Rejected(87, "invalid parameter").Succeeded);
        Assert.False(GammaWriteResult.AcceptedButUnverified("driver clamped the ramp").Succeeded);

        Assert.Equal(87, GammaWriteResult.Rejected(87, "invalid parameter").Win32Error);
    }
}
