using EftToolkit.Core.Display;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Tests.Platform;

public class WindowsMessageDecoderTests
{
    // The values are asserted rather than merely used, because a wrong constant is a silent no-op:
    // the sink would simply never match a message and the shortcut would appear broken.
    [Fact]
    public void The_message_and_modifier_constants_match_the_windows_headers()
    {
        Assert.Equal(0x0312u, WindowsMessageDecoder.WmHotkey);
        Assert.Equal(0x007Eu, WindowsMessageDecoder.WmDisplayChange);
        Assert.Equal(0x0218u, WindowsMessageDecoder.WmPowerBroadcast);
        Assert.Equal(0x02B1u, WindowsMessageDecoder.WmWtssessionChange);
        Assert.Equal(0x0012u, WindowsMessageDecoder.PbtApmResumeAutomatic);
        Assert.Equal(0x0008u, WindowsMessageDecoder.WtsSessionUnlock);
        Assert.Equal(0x4000u, WindowsMessageDecoder.ModNoRepeat);
    }

    [Theory]
    [InlineData(0xEF20, DisplayPresetKind.Original)]
    [InlineData(0xEF21, DisplayPresetKind.Low)]
    [InlineData(0xEF22, DisplayPresetKind.Medium)]
    [InlineData(0xEF23, DisplayPresetKind.High)]
    public void DecodeHotkey_maps_registered_ids(int id, DisplayPresetKind expected)
    {
        Assert.Equal(expected, WindowsMessageDecoder.DecodeHotkey(id));
    }

    [Theory]
    [InlineData(DisplayPresetKind.Original, 0xEF20)]
    [InlineData(DisplayPresetKind.Low, 0xEF21)]
    [InlineData(DisplayPresetKind.Medium, 0xEF22)]
    [InlineData(DisplayPresetKind.High, 0xEF23)]
    public void HotkeyIdFor_and_DecodeHotkey_agree_in_both_directions(DisplayPresetKind preset, int id)
    {
        Assert.Equal(id, WindowsMessageDecoder.HotkeyIdFor(preset));
        Assert.Equal(preset, WindowsMessageDecoder.DecodeHotkey(id));
    }

    [Theory]
    [InlineData(DisplayPresetKind.Original, 0x71u)]
    [InlineData(DisplayPresetKind.Low, 0x72u)]
    [InlineData(DisplayPresetKind.Medium, 0x73u)]
    [InlineData(DisplayPresetKind.High, 0x74u)]
    public void VirtualKeyFor_maps_the_fixed_f2_to_f5_bindings(DisplayPresetKind preset, uint expected)
    {
        Assert.Equal(expected, WindowsMessageDecoder.VirtualKeyFor(preset));
    }

    [Theory]
    [InlineData(0xEF1F)]
    [InlineData(0xEF24)]
    [InlineData(0)]
    public void TryDecodeHotkey_reports_an_id_this_toolkit_did_not_register(int id)
    {
        Assert.False(WindowsMessageDecoder.TryDecodeHotkey(id, out _));
    }

    [Theory]
    [InlineData(0xEF20, DisplayPresetKind.Original)]
    [InlineData(0xEF23, DisplayPresetKind.High)]
    public void TryDecodeHotkey_reports_a_registered_id(int id, DisplayPresetKind expected)
    {
        Assert.True(WindowsMessageDecoder.TryDecodeHotkey(id, out DisplayPresetKind preset));
        Assert.Equal(expected, preset);
    }

    [Fact]
    public void DecodeHotkey_rejects_an_id_this_toolkit_did_not_register()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsMessageDecoder.DecodeHotkey(0x1234));
    }

    [Fact]
    public void VirtualKeyFor_rejects_a_preset_that_has_no_shortcut()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsMessageDecoder.VirtualKeyFor((DisplayPresetKind)99));
    }

    [Theory]
    [InlineData(DisplayPresetKind.Original)]
    [InlineData(DisplayPresetKind.Low)]
    [InlineData(DisplayPresetKind.Medium)]
    [InlineData(DisplayPresetKind.High)]
    public void Every_preset_has_a_distinct_shortcut(DisplayPresetKind preset)
    {
        Assert.True(WindowsMessageDecoder.TryDecodeHotkey(WindowsMessageDecoder.HotkeyIdFor(preset), out _));
        Assert.Contains(preset, WindowsMessageDecoder.Presets);
    }

    [Fact]
    public void All_four_presets_are_bound_exactly_once()
    {
        Assert.Equal(4, WindowsMessageDecoder.Presets.Count);

        Assert.Equal(
            WindowsMessageDecoder.Presets.Count,
            WindowsMessageDecoder.Presets.Select(WindowsMessageDecoder.HotkeyIdFor).Distinct().Count());

        Assert.Equal(
            WindowsMessageDecoder.Presets.Count,
            WindowsMessageDecoder.Presets.Select(WindowsMessageDecoder.VirtualKeyFor).Distinct().Count());
    }

    [Fact]
    public void A_display_change_is_an_environment_change()
    {
        Assert.True(WindowsMessageDecoder.IsDisplayEnvironmentChanged(Message(WindowsMessageDecoder.WmDisplayChange)));
    }

    [Fact]
    public void An_automatic_resume_is_an_environment_change()
    {
        Assert.True(WindowsMessageDecoder.IsDisplayEnvironmentChanged(
            Message(WindowsMessageDecoder.WmPowerBroadcast, WindowsMessageDecoder.PbtApmResumeAutomatic)));
    }

    [Fact]
    public void A_session_unlock_is_an_environment_change()
    {
        Assert.True(WindowsMessageDecoder.IsDisplayEnvironmentChanged(
            Message(WindowsMessageDecoder.WmWtssessionChange, WindowsMessageDecoder.WtsSessionUnlock)));
    }

    [Theory]
    // A resume broadcast that is not the automatic one, and a session change that is a lock rather
    // than an unlock: both look similar and must not be treated as "reapply the preset now".
    [InlineData(0x0218u, 0x0004u)]
    [InlineData(0x02B1u, 0x0007u)]
    public void A_related_but_different_notification_is_not_an_environment_change(uint id, uint wParam)
    {
        Assert.False(WindowsMessageDecoder.IsDisplayEnvironmentChanged(Message(id, wParam)));
    }

    [Theory]
    [InlineData(0x0312u, 0xEF23u)]
    [InlineData(0x0001u, 0u)]
    public void An_unrelated_message_is_not_an_environment_change(uint id, uint wParam)
    {
        Assert.False(WindowsMessageDecoder.IsDisplayEnvironmentChanged(Message(id, wParam)));
    }

    private static WindowsMessage Message(uint id, nuint wParam = 0) => new(0, id, wParam, 0);
}
