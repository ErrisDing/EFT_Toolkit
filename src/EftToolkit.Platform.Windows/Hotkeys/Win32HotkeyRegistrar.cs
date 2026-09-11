using EftToolkit.Platform.Windows.Interop;

namespace EftToolkit.Platform.Windows.Hotkeys;

/// <summary>The real <c>RegisterHotKey</c> calls. See <see cref="IHotkeyRegistrar"/> for the test seam.</summary>
public sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    public bool TryRegister(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey) =>
        NativeMethods.RegisterHotKey(windowHandle, hotkeyId, modifiers, virtualKey);

    public void Unregister(nint windowHandle, int hotkeyId) =>
        NativeMethods.UnregisterHotKey(windowHandle, hotkeyId);
}
