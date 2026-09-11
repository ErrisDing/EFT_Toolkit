using EftToolkit.Platform.Windows.Events;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Tests.Platform;

public class PlatformEventSourceTests
{
    /// <summary>
    /// Short enough that the tests are quick, long enough that a burst fired in a tight loop lands
    /// inside one window on a loaded machine.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(150);

    private readonly FakeWindowsMessageSource _source = new();

    private PlatformEventSource CreateSource() => new(_source, Window, TimeProvider.System);

    [Fact]
    public async Task Start_opens_the_message_window()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        Assert.True(_source.Started);
    }

    [Fact]
    public async Task A_display_change_is_reported()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        _source.Deliver(WindowsMessageDecoder.WmDisplayChange);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the display change was never reported");
    }

    [Fact]
    public async Task An_automatic_resume_and_a_session_unlock_are_reported()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        _source.Deliver(WindowsMessageDecoder.WmPowerBroadcast, WindowsMessageDecoder.PbtApmResumeAutomatic);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the resume was never reported");

        _source.Deliver(WindowsMessageDecoder.WmWtssessionChange, WindowsMessageDecoder.WtsSessionUnlock);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 2, "the unlock was never reported");
    }

    [Fact]
    public async Task A_burst_of_events_collapses_into_one_notification()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        // Windows reports a single monitor change as several messages. Each one must not cost a
        // full re-enumeration and rewrite of every selected ramp.
        for (int index = 0; index < 8; index++)
        {
            _source.Deliver(WindowsMessageDecoder.WmDisplayChange);
        }

        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the burst was never reported");
        await Task.Delay(Window + Window);

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task An_event_after_the_window_elapses_is_reported_again()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        _source.Deliver(WindowsMessageDecoder.WmDisplayChange);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the first change was never reported");

        await Task.Delay(Window + Window);

        _source.Deliver(WindowsMessageDecoder.WmDisplayChange);
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 2, "the later change was never reported");
    }

    [Fact]
    public async Task An_event_while_the_change_is_still_settling_joins_the_same_notification()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        // Windows reports one monitor change as several messages spread over tens of milliseconds.
        // Each has to extend the quiet period rather than earn a pass of its own, so the preset is
        // reapplied once to the arrangement that settled instead of to a half-built one.
        _source.Deliver(WindowsMessageDecoder.WmDisplayChange);
        await Task.Delay(Window / 3);
        _source.Deliver(WindowsMessageDecoder.WmDisplayChange);

        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "the change was never reported");

        await Task.Delay(Window + Window);

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Unrelated_messages_are_ignored()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        _source.Deliver(WindowsMessageDecoder.WmHotkey, (nuint)WindowsMessageDecoder.HotkeyIdHigh);
        _source.Deliver(WindowsMessageDecoder.WmPowerBroadcast, 0x0004);
        _source.Deliver(WindowsMessageDecoder.WmWtssessionChange, 0x0007);

        await Task.Delay(Window + Window);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Stop_closes_the_message_window()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);
        await events.StopAsync(CancellationToken.None);

        Assert.True(_source.Stopped);
    }

    [Fact]
    public async Task An_event_after_stopping_is_not_reported()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        int raised = 0;
        events.DisplayEnvironmentChanged += (_, _) => Interlocked.Increment(ref raised);

        await events.StopAsync(CancellationToken.None);
        _source.Deliver(WindowsMessageDecoder.WmDisplayChange);

        await Task.Delay(Window + Window);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Dispose_stops_the_source()
    {
        PlatformEventSource events = CreateSource();
        await events.StartAsync(CancellationToken.None);

        await events.DisposeAsync();

        Assert.True(_source.Stopped);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting until {because}.");
    }
}
