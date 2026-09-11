namespace EftToolkit.Platform.Windows.Messaging;

/// <summary>
/// The parts of a <c>MSG</c> this toolkit acts on. A message-only window sees nothing else, so the
/// cursor position, time, and private fields are not carried across.
/// </summary>
/// <param name="WindowHandle">The window the message was delivered to.</param>
/// <param name="Id">The message identifier, such as <c>WM_HOTKEY</c>.</param>
/// <param name="WParam">For <c>WM_HOTKEY</c>, the identifier passed to <c>RegisterHotKey</c>.</param>
/// <param name="LParam">For <c>WM_HOTKEY</c>, the modifiers in the low word and the virtual key in the high word.</param>
public readonly record struct WindowsMessage(nint WindowHandle, uint Id, nuint WParam, nint LParam);
