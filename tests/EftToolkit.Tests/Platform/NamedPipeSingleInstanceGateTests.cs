using EftToolkit.Core.Diagnostics;
using EftToolkit.Platform.Windows.SingleInstance;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Platform;

/// <summary>
/// Exercises the real mutex and the real named pipe. The names carry a fresh GUID per test, so the
/// tests neither collide with each other nor with a copy of the application running on the machine.
/// </summary>
public class NamedPipeSingleInstanceGateTests
{
    private readonly RecordingLogger _logger = new();

    // ---------------------------------------------------------------- who is primary

    [Fact]
    public async Task The_first_gate_becomes_the_primary()
    {
        await using NamedPipeSingleInstanceGate gate = CreateGate(UniqueName());

        Assert.True(await gate.AcquireAsync(CancellationToken.None));
        Assert.True(gate.IsPrimary);
    }

    [Fact]
    public async Task A_second_gate_for_the_same_name_is_a_secondary()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        await using NamedPipeSingleInstanceGate secondary = CreateGate(name);

        Assert.True(await primary.AcquireAsync(CancellationToken.None));
        Assert.False(await secondary.AcquireAsync(CancellationToken.None));
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public async Task A_gate_with_a_different_name_is_primary_regardless()
    {
        await using NamedPipeSingleInstanceGate first = CreateGate(UniqueName());
        await using NamedPipeSingleInstanceGate second = CreateGate(UniqueName());

        Assert.True(await first.AcquireAsync(CancellationToken.None));
        Assert.True(await second.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Acquiring_twice_on_one_gate_stays_primary()
    {
        await using NamedPipeSingleInstanceGate gate = CreateGate(UniqueName());

        Assert.True(await gate.AcquireAsync(CancellationToken.None));
        Assert.True(await gate.AcquireAsync(CancellationToken.None));
        Assert.True(gate.IsPrimary);
    }

    [Fact]
    public async Task A_gate_reports_secondary_before_it_has_acquired_anything()
    {
        await using NamedPipeSingleInstanceGate gate = CreateGate(UniqueName());

        Assert.False(gate.IsPrimary);
    }

    [Fact]
    public async Task Disposing_the_primary_lets_a_new_gate_become_primary()
    {
        string name = UniqueName();

        NamedPipeSingleInstanceGate first = CreateGate(name);
        Assert.True(await first.AcquireAsync(CancellationToken.None));
        await first.DisposeAsync();

        await using NamedPipeSingleInstanceGate second = CreateGate(name);

        // The whole point of the mutex: an instance that is gone must not keep the next one out.
        Assert.True(await second.AcquireAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disposing_a_secondary_does_not_release_the_primary()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        NamedPipeSingleInstanceGate secondary = CreateGate(name);
        Assert.False(await secondary.AcquireAsync(CancellationToken.None));
        await secondary.DisposeAsync();

        await using NamedPipeSingleInstanceGate third = CreateGate(name);
        Assert.False(await third.AcquireAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- activation

    [Fact]
    public async Task A_secondary_notifying_the_primary_raises_one_activation()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        int raised = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref raised);

        await using NamedPipeSingleInstanceGate secondary = CreateGate(name);
        Assert.False(await secondary.AcquireAsync(CancellationToken.None));

        await secondary.NotifyPrimaryAsync(CancellationToken.None);

        await AsyncWait.UntilAsync(() => Volatile.Read(ref raised) == 1, "the primary was told to activate");

        // One command, one activation: a second launch must not enqueue anything the panel has to
        // de-duplicate.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Two_notifications_raise_two_activations()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        int raised = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref raised);

        await using NamedPipeSingleInstanceGate secondary = CreateGate(name);
        Assert.False(await secondary.AcquireAsync(CancellationToken.None));

        await secondary.NotifyPrimaryAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => Volatile.Read(ref raised) == 1, "the first notification arrived");

        // Each launch is a separate command, so the server has to accept the next connection rather
        // than serve one and stop listening.
        await secondary.NotifyPrimaryAsync(CancellationToken.None);
        await AsyncWait.UntilAsync(() => Volatile.Read(ref raised) == 2, "the second notification arrived");
    }

    [Fact]
    public async Task An_unrecognised_command_is_logged_and_raises_nothing()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        int raised = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref raised);

        await using NamedPipeSingleInstanceGate other = CreateGate(name);
        await other.SendCommandAsync("something-else", CancellationToken.None);

        await AsyncWait.UntilAsync(
            () => _logger.Logged("singleInstance.invalidCommand"),
            "the invalid command was reported");

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task A_payload_that_is_too_long_is_refused_rather_than_read_forever()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        int raised = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref raised);

        await using NamedPipeSingleInstanceGate other = CreateGate(name);

        try
        {
            // Comfortably longer than the server's read cap. The server stops reading at the cap and
            // closes, so the sender may see the pipe break part-way through writing, and whether it
            // does depends on how much of the payload the pipe buffered. That is the sender's
            // problem, not the server's: what matters is that the server refused it.
            await other.SendCommandAsync(new string('a', 1024), CancellationToken.None);
        }
        catch (IOException)
        {
        }

        await AsyncWait.UntilAsync(
            () => _logger.Logged("singleInstance.invalidCommand"),
            "the oversized command was reported");

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task The_logged_payload_is_bounded_and_stripped_of_control_characters()
    {
        string name = UniqueName();

        await using NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        await using NamedPipeSingleInstanceGate other = CreateGate(name);
        await other.SendCommandAsync("bad\r\ncommand", CancellationToken.None);

        await AsyncWait.UntilAsync(
            () => _logger.Logged("singleInstance.invalidCommand"),
            "the invalid command was reported");

        // Whatever the other process sent lands in a log line and, from there, in whatever reads
        // the log. It is treated as data: truncated, and with nothing in it that moves a cursor.
        LogEntry entry = Assert.Single(_logger.Entries, item => item.EventName == "singleInstance.invalidCommand");
        string payload = Assert.IsType<string>(entry.Properties!["payload"]);

        Assert.True(payload.Length <= 64, $"the payload was {payload.Length} characters");
        Assert.All(payload, character => Assert.False(char.IsControl(character)));
    }

    [Fact]
    public async Task Disposing_the_primary_stops_listening()
    {
        string name = UniqueName();

        NamedPipeSingleInstanceGate primary = CreateGate(name);
        Assert.True(await primary.AcquireAsync(CancellationToken.None));

        int raised = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref raised);

        await primary.DisposeAsync();

        await using NamedPipeSingleInstanceGate secondary = CreateGate(name);

        // The server is gone with the instance, so there is nothing left to notify. A secondary that
        // finds no primary has to be able to tell the difference, which it does by the failure.
        await Assert.ThrowsAnyAsync<Exception>(() => secondary.NotifyPrimaryAsync(CancellationToken.None));

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        NamedPipeSingleInstanceGate gate = CreateGate(UniqueName());
        Assert.True(await gate.AcquireAsync(CancellationToken.None));

        await gate.DisposeAsync();
        await gate.DisposeAsync();

        Assert.False(gate.IsPrimary);
    }

    private NamedPipeSingleInstanceGate CreateGate(string name) => new(name, _logger);

    private static string UniqueName() => $"EftToolkit.Tests.{Guid.NewGuid():N}";
}
