using EftToolkit.Platform.Windows.Hotkeys;
using EftToolkit.Platform.Windows.Interop;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Tests.Platform;

/// <summary>
/// Packages the tests that drive a real hidden window and real <c>RegisterHotKey</c> bindings.
/// </summary>
/// <remarks>
/// They are grouped so xunit runs them one at a time. A hotkey is a machine-wide resource and the
/// function-key bindings these tests take are visible to every other test in the process, so two of
/// them running side by side would see each other's keys as already held and fail for a reason that
/// has nothing to do with the code.
/// </remarks>
[CollectionDefinition(Name)]
public class WindowsMessageCollection
{
    public const string Name = "windows-message-window";
}

/// <summary>
/// Exercises the real hidden window. Unlike the rest of the platform tests these use no
/// substitutes, because the thing being checked is precisely the interop: a wrong struct layout or
/// an unreachable message loop would leave every shortcut silently dead in production.
/// </summary>
[Collection(WindowsMessageCollection.Name)]
public class WindowsMessageSinkTests
{
    /// <summary>A message with no meaning to Windows, used to prove delivery works.</summary>
    private const uint WmApp = 0x8000;

    [Fact]
    public async Task A_message_posted_to_the_window_is_reported()
    {
        await using WindowsMessageSink sink = new();
        await sink.StartAsync(CancellationToken.None);

        Assert.NotEqual(0, sink.WindowHandle);

        TaskCompletionSource<WindowsMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.MessageReceived += (_, message) => received.TrySetResult(message);

        Assert.True(NativeMethods.PostMessage(sink.WindowHandle, WmApp, 7, 11));

        WindowsMessage message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WmApp, message.Id);
        Assert.Equal((nuint)7, message.WParam);
        Assert.Equal((nint)11, message.LParam);
        Assert.Equal(sink.WindowHandle, message.WindowHandle);
    }

    [Fact]
    public async Task Stopping_closes_the_window_and_ends_the_message_thread()
    {
        WindowsMessageSink sink = new();
        await sink.StartAsync(CancellationToken.None);
        nint handle = sink.WindowHandle;

        await sink.StopAsync(CancellationToken.None);

        Assert.Equal(0, sink.WindowHandle);

        // StopAsync only returns once the message thread has exited, so the window is already gone
        // and cannot accept another message.
        Assert.False(NativeMethods.PostMessage(handle, WmApp, 0, 0));
    }

    [Fact]
    public async Task A_second_window_can_be_opened_after_the_first_is_closed()
    {
        // The class registration is unregistered on the way out, so a restart must not collide with
        // a stale registration.
        WindowsMessageSink first = new();
        await first.StartAsync(CancellationToken.None);
        await first.StopAsync(CancellationToken.None);

        await using WindowsMessageSink second = new();
        await second.StartAsync(CancellationToken.None);

        Assert.NotEqual(0, second.WindowHandle);
    }

    [Fact]
    public async Task The_same_sink_can_be_started_again_after_it_is_stopped()
    {
        // A restart has to produce a window that works, not just a non-zero handle. The completion
        // source that publishes the handle used to be created once, so a restart returned the handle
        // of the window the stop had destroyed: the handle was not zero, and posting to it failed.
        await using WindowsMessageSink sink = new();

        await sink.StartAsync(CancellationToken.None);
        nint first = sink.WindowHandle;
        await sink.StopAsync(CancellationToken.None);

        await sink.StartAsync(CancellationToken.None);

        Assert.NotEqual(0, sink.WindowHandle);
        Assert.NotEqual(first, sink.WindowHandle);

        TaskCompletionSource<WindowsMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.MessageReceived += (_, message) => received.TrySetResult(message);

        Assert.True(NativeMethods.PostMessage(sink.WindowHandle, WmApp, 7, 11));

        WindowsMessage message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WmApp, message.Id);
    }

    [Fact]
    public async Task Start_and_stop_are_idempotent()
    {
        await using WindowsMessageSink sink = new();

        await sink.StartAsync(CancellationToken.None);
        nint handle = sink.WindowHandle;

        await sink.StartAsync(CancellationToken.None);
        Assert.Equal(handle, sink.WindowHandle);

        await sink.StopAsync(CancellationToken.None);
        await sink.StopAsync(CancellationToken.None);

        Assert.Equal(0, sink.WindowHandle);
    }

    [Fact]
    public async Task The_window_can_hold_a_real_hotkey_for_as_long_as_it_lives()
    {
        // A regression guard, not just a signature check. Windows refuses to register a hotkey
        // against a message-only window, and demands the call come from the thread that owns the
        // window. Either mistake would leave every shortcut dead at runtime while the substituted
        // tests stayed green.
        await using WindowsMessageSink sink = new();
        await sink.StartAsync(CancellationToken.None);

        // A combination nothing else claims, so the test does not take a shortcut away from the
        // machine running it. No key is ever pressed: the manual checklist covers the key actually
        // reaching the application.
        const int HotkeyId = 0xEF01;
        const uint Modifiers = 0x4007; // MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT
        const uint VirtualKeyF12 = 0x7B;

        Win32HotkeyRegistrar registrar = new();

        bool held = await sink.InvokeAsync(
            () => registrar.TryRegister(sink.WindowHandle, HotkeyId, Modifiers, VirtualKeyF12),
            CancellationToken.None);

        Assert.True(held);

        await sink.InvokeAsync(
            () =>
            {
                registrar.Unregister(sink.WindowHandle, HotkeyId);
                return true;
            },
            CancellationToken.None);

        // Re-registering the same combination succeeds, which shows the release actually landed.
        bool reheld = await sink.InvokeAsync(
            () => registrar.TryRegister(sink.WindowHandle, HotkeyId, Modifiers, VirtualKeyF12),
            CancellationToken.None);

        Assert.True(reheld);
    }

    [Fact]
    public async Task Invoke_returns_the_callback_result_and_surfaces_its_failure()
    {
        await using WindowsMessageSink sink = new();
        await sink.StartAsync(CancellationToken.None);

        Assert.Equal(42, await sink.InvokeAsync(() => 42, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InvokeAsync<bool>(() => throw new InvalidOperationException("from the message thread"), CancellationToken.None));
    }

    [Fact]
    public async Task Invoke_before_the_window_exists_is_refused_rather_than_hanging()
    {
        await using WindowsMessageSink sink = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.InvokeAsync(() => true, CancellationToken.None));
    }
}
