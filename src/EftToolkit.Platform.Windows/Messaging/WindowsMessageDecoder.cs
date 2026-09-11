using EftToolkit.Core.Display;

namespace EftToolkit.Platform.Windows.Messaging;

/// <summary>
/// Translates raw window messages into the two things this toolkit cares about: a preset request
/// from a shortcut, and a change in the display environment that invalidates a ramp.
/// </summary>
/// <remarks>
/// Every function here is pure so the mapping can be tested without a window, a message loop, or a
/// display. The constants mirror the Windows headers exactly; a wrong value would not fail loudly,
/// it would simply never match.
/// </remarks>
public static class WindowsMessageDecoder
{
    public const uint WmHotkey = 0x0312;
    public const uint WmDisplayChange = 0x007E;
    public const uint WmPowerBroadcast = 0x0218;
    public const uint WmWtssessionChange = 0x02B1;

    public const nuint PbtApmResumeAutomatic = 0x0012;
    public const nuint WtsSessionUnlock = 0x0008;

    /// <summary>
    /// Stops a held key from repeating. Without it, resting a finger on F5 would queue a preset
    /// request every few milliseconds.
    /// </summary>
    public const uint ModNoRepeat = 0x4000;

    // The hotkey identifier range this toolkit owns. RegisterHotKey only requires that the ID be
    // unique within the process, but keeping them together makes a stray ID obvious in a log.
    public const int HotkeyIdOriginal = 0xEF20;
    public const int HotkeyIdLow = 0xEF21;
    public const int HotkeyIdMedium = 0xEF22;
    public const int HotkeyIdHigh = 0xEF23;

    private const uint VirtualKeyF2 = 0x71;
    private const uint VirtualKeyF3 = 0x72;
    private const uint VirtualKeyF4 = 0x73;
    private const uint VirtualKeyF5 = 0x74;

    /// <summary>The presets in shortcut order, so callers iterate them without repeating the list.</summary>
    public static readonly IReadOnlyList<DisplayPresetKind> Presets =
    [
        DisplayPresetKind.Original,
        DisplayPresetKind.Low,
        DisplayPresetKind.Medium,
        DisplayPresetKind.High,
    ];

    public static int HotkeyIdFor(DisplayPresetKind preset) => preset switch
    {
        DisplayPresetKind.Original => HotkeyIdOriginal,
        DisplayPresetKind.Low => HotkeyIdLow,
        DisplayPresetKind.Medium => HotkeyIdMedium,
        DisplayPresetKind.High => HotkeyIdHigh,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Only the four fixed presets have a shortcut."),
    };

    public static uint VirtualKeyFor(DisplayPresetKind preset) => preset switch
    {
        DisplayPresetKind.Original => VirtualKeyF2,
        DisplayPresetKind.Low => VirtualKeyF3,
        DisplayPresetKind.Medium => VirtualKeyF4,
        DisplayPresetKind.High => VirtualKeyF5,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Only the four fixed presets have a shortcut."),
    };

    /// <summary>
    /// Maps the identifier carried by <c>WM_HOTKEY</c>. Throws for an unrecognised identifier; the
    /// message path uses <see cref="TryDecodeHotkey"/>, which reports it instead.
    /// </summary>
    public static DisplayPresetKind DecodeHotkey(int hotkeyId)
    {
        if (TryDecodeHotkey(hotkeyId, out DisplayPresetKind preset))
        {
            return preset;
        }

        throw new ArgumentOutOfRangeException(
            nameof(hotkeyId),
            hotkeyId,
            "This identifier does not belong to a preset registered by this toolkit.");
    }

    public static bool TryDecodeHotkey(int hotkeyId, out DisplayPresetKind preset)
    {
        switch (hotkeyId)
        {
            case HotkeyIdOriginal:
                preset = DisplayPresetKind.Original;
                return true;
            case HotkeyIdLow:
                preset = DisplayPresetKind.Low;
                return true;
            case HotkeyIdMedium:
                preset = DisplayPresetKind.Medium;
                return true;
            case HotkeyIdHigh:
                preset = DisplayPresetKind.High;
                return true;
            default:
                preset = default;
                return false;
        }
    }

    /// <summary>
    /// Whether the message means the ramps on screen are no longer the ones this toolkit wrote, so
    /// the current preset has to be reapplied.
    /// </summary>
    public static bool IsDisplayEnvironmentChanged(in WindowsMessage message) => message.Id switch
    {
        WmDisplayChange => true,

        // Only the automatic resume. A user-initiated resume and the other power transitions do not
        // imply the display was reconfigured.
        WmPowerBroadcast => message.WParam == PbtApmResumeAutomatic,

        WmWtssessionChange => message.WParam == WtsSessionUnlock,

        _ => false,
    };
}
