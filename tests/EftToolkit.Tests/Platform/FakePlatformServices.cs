using EftToolkit.Platform.Windows.Hotkeys;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Tests.Platform;

/// <summary>
/// A message window that never exists. Tests push messages in by hand, so the assertions are about
/// the hotkey service's behavior rather than about whether Windows delivered anything.
/// </summary>
internal sealed class FakeWindowsMessageSource : IWindowsMessageSource
{
    /// <summary>Shared with the other fakes to assert the order operations happened in.</summary>
    public List<string> Journal { get; } = [];

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public nint WindowHandle => 0x1234;

    public event EventHandler<WindowsMessage>? MessageReceived;

    public Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken) =>
        Task.FromResult(callback());

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started = true;
        Journal.Add("window.create");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        Journal.Add("window.destroy");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Deliver(uint id, nuint wParam = 0, nint lParam = 0) =>
        MessageReceived?.Invoke(this, new WindowsMessage(WindowHandle, id, wParam, lParam));
}

/// <summary>
/// Stands in for <c>RegisterHotKey</c>. A real registration would take F2-F5 away from the machine
/// running the tests and would fail outright on a machine where another application already holds
/// them, so the native call is replaced instead of exercised.
/// </summary>
internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public List<string> Journal { get; } = [];

    /// <summary>Hotkey IDs the fake refuses, standing in for a key another application already owns.</summary>
    public HashSet<int> Refused { get; } = [];

    /// <summary>Every registration attempt, accepted or not.</summary>
    public List<(int HotkeyId, uint Modifiers, uint VirtualKey)> Attempts { get; } = [];

    public HashSet<int> Registered { get; } = [];

    public int UnregisterCount { get; private set; }

    public bool TryRegister(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey)
    {
        Attempts.Add((hotkeyId, modifiers, virtualKey));
        Journal.Add($"hotkey.register.{hotkeyId:X}");

        if (Refused.Contains(hotkeyId))
        {
            return false;
        }

        Registered.Add(hotkeyId);
        return true;
    }

    public void Unregister(nint windowHandle, int hotkeyId)
    {
        UnregisterCount++;
        Journal.Add($"hotkey.unregister.{hotkeyId:X}");
        Registered.Remove(hotkeyId);
    }
}
