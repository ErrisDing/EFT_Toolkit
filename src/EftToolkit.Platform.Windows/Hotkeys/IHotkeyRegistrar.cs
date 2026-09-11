namespace EftToolkit.Platform.Windows.Hotkeys;

/// <summary>
/// The native hotkey registration calls, behind an interface so a test can simulate a key that
/// another application already owns. A real registration would take F2-F5 away from the machine
/// running the tests.
/// </summary>
public interface IHotkeyRegistrar
{
    /// <summary>Returns <see langword="false"/> when Windows refuses, typically because another application holds the key.</summary>
    bool TryRegister(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey);

    void Unregister(nint windowHandle, int hotkeyId);
}
