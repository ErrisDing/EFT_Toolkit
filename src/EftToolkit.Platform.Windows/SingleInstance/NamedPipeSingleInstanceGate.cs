using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using EftToolkit.Core.Diagnostics;

namespace EftToolkit.Platform.Windows.SingleInstance;

/// <summary>
/// Claims the instance with a named mutex and hears from later launches over a named pipe.
/// </summary>
/// <remarks>
/// <para>
/// Both names carry the current user's SID and are session-local. Two copies running as different
/// users are two different users' toolkits and must not interfere with each other, and a name that
/// a different process on the machine could claim would be a way to stop the toolkit from starting
/// at all.
/// </para>
/// <para>
/// The pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>, so the operating system
/// refuses a connection from any other user's process. What is left is a same-user process, which
/// already has the means to interfere; the server still treats the payload as data rather than as
/// an instruction, and accepts exactly one command.
/// </para>
/// <para>
/// The claim is made on a thread of its own, which then stays alive holding the mutex, rather than
/// on whichever thread happened to call <see cref="AcquireAsync"/>. A mutex belongs to the thread
/// that acquired it, and waiting on it twice from that same thread succeeds rather than failing, so
/// a claim made on a caller's thread would let a second gate in this process claim the same name
/// whenever its continuation ran on the same thread. Owning the mutex from a dedicated thread also
/// makes the ownership outlive any particular caller, which is what it is supposed to model.
/// </para>
/// <para>
/// The mutex is closed rather than released. If the process exits without disposing this gate, the
/// thread dies with the mutex held and Windows abandons it, which is how a crashed instance lets
/// the next one in.
/// </para>
/// </remarks>
public sealed class NamedPipeSingleInstanceGate : ISingleInstanceGate
{
    /// <summary>The one command the server accepts: a later launch asking the primary to show up.</summary>
    internal const string ActivateCommand = "activate";

    /// <summary>
    /// The longest command that will be read. Anything past this is refused rather than read to
    /// completion, so a peer that never sends a newline cannot hold the server in a read loop.
    /// </summary>
    internal const int MaxCommandBytes = 256;

    /// <summary>How long a secondary waits for the primary's server before giving up on it.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How much of an unrecognised command is kept for the log line.</summary>
    private const int LoggedPayloadLength = 64;

    private const byte LineFeed = (byte)'\n';

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly IAppLogger? _logger;

    private readonly object _gate = new();

    /// <summary>Completes with whether this gate got the claim. Answered once, by the claim thread.</summary>
    private readonly TaskCompletionSource<bool> _claim =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set on dispose to let the claim thread end and give the mutex up.</summary>
    private readonly ManualResetEventSlim _claimRelease = new(false);

    private Thread? _claimThread;
    private CancellationTokenSource? _serverCancellation;
    private Task? _server;
    private bool _isPrimary;
    private volatile bool _disposed;

    /// <param name="name">
    /// Distinguishes this application's claim from any other's. It becomes part of both names, so it
    /// must be a plain identifier.
    /// </param>
    public NamedPipeSingleInstanceGate(string name, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The name becomes part of a mutex and pipe name and cannot contain a backslash.",
                nameof(name));
        }

        string suffix = CurrentUserSid();
        _mutexName = $@"Local\EftToolkit.SingleInstance.{name}.{suffix}";
        _pipeName = $"EftToolkit.SingleInstance.{name}.{suffix}";
        _logger = logger;
    }

    public bool IsPrimary
    {
        get
        {
            lock (_gate)
            {
                return _isPrimary;
            }
        }
    }

    public event EventHandler? ActivationRequested;

    public async Task<bool> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_claimThread is null)
            {
                // The claim is answered once. Asking again after losing it would only take a second
                // wait that cannot succeed while the primary is alive.
                _claimThread = new Thread(Claim) { IsBackground = true, Name = "EftToolkit single instance" };
                _claimThread.Start();
            }
        }

        return await _claim.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the claim thread: takes the mutex, reports whether it got it, and holds it until the
    /// gate is disposed.
    /// </summary>
    private void Claim()
    {
        Mutex mutex;

        try
        {
            mutex = new Mutex(initiallyOwned: false, _mutexName);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Error, "singleInstance.claimFailed", exception: exception);
            _claim.TrySetResult(false);
            return;
        }

        bool owned;

        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance died holding it. Windows hands the mutex over on that exception,
            // and a crashed instance must not keep the next one out.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            _claim.TrySetResult(false);
            return;
        }

        bool hold;

        lock (_gate)
        {
            // A dispose that got here first has already given up the release event, so there is
            // nothing to hold and nothing to wait on. The mutex is closed on this thread either way,
            // because this is the thread that owns it.
            hold = !_disposed;

            if (hold)
            {
                _isPrimary = true;
                StartServer();
            }
        }

        if (!hold)
        {
            _claim.TrySetResult(false);
            mutex.Dispose();
            return;
        }

        _claim.TrySetResult(true);

        _claimRelease.Wait();
        mutex.Dispose();
    }

    public Task NotifyPrimaryAsync(CancellationToken cancellationToken) =>
        SendCommandAsync(ActivateCommand, cancellationToken);

    /// <summary>
    /// Writes one command line to the primary. Exposed to tests so the server's handling of a
    /// command it does not recognise can be exercised through the real pipe.
    /// </summary>
    internal async Task SendCommandAsync(string payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);

        await using NamedPipeClientStream client = new(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.Asynchronous);

        await client.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);

        byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");

        await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource? cancellation;
        Task? server;
        Thread? claimThread;
        bool wasPrimary;

        lock (_gate)
        {
            cancellation = _serverCancellation;
            server = _server;
            claimThread = _claimThread;
            wasPrimary = _isPrimary;

            _serverCancellation = null;
            _server = null;
            _claimThread = null;
            _isPrimary = false;
        }

        cancellation?.Cancel();

        if (server is not null)
        {
            try
            {
                await server.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The expected way for the server loop to end.
            }
        }

        cancellation?.Dispose();

        if (wasPrimary && claimThread is not null)
        {
            // Letting the claim thread end is what closes the mutex and gives the claim up, so the
            // next instance can take it.
            _claimRelease.Set();

            Thread thread = claimThread;

            await Task.Run(thread.Join).ConfigureAwait(false);
        }

        _claimRelease.Dispose();
    }

    /// <summary>
    /// The SID rather than the user name: two accounts can share a display name, and the names have
    /// to be unique to the user whose process is allowed to talk to them.
    /// </summary>
    private static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
    }

    private void StartServer()
    {
        CancellationTokenSource cancellation = new();

        _serverCancellation = cancellation;

        // Deliberately not awaited: the claim is what the caller asked for, and the server only has
        // to be listening before another launch connects, which DisposeAsync guarantees.
        _server = Task.Run(() => ServeAsync(cancellation.Token), CancellationToken.None);
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            try
            {
                server = CreateServer();
            }
            catch (Exception exception)
            {
                // A pipe name that is already taken by a leftover server would loop forever here.
                _logger?.Write(LogLevel.Warning, "singleInstance.pipeUnavailable", exception: exception);
                return;
            }

            await using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger?.Write(LogLevel.Warning, "singleInstance.connectionFailed", exception: exception);
                    continue;
                }

                (string payload, bool tooLong) = await ReadCommandAsync(server, cancellationToken).ConfigureAwait(false);

                if (!tooLong && string.Equals(payload, ActivateCommand, StringComparison.Ordinal))
                {
                    RaiseActivationRequested();
                }
                else
                {
                    _logger?.Write(
                        LogLevel.Warning,
                        "singleInstance.invalidCommand",
                        new Dictionary<string, object?> { ["payload"] = Sanitize(payload) });
                }
            }
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.In,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// Reads one newline-terminated command. A command with no newline before the cap is reported as
    /// too long rather than as whatever it happened to contain so far.
    /// </summary>
    private static async Task<(string Payload, bool TooLong)> ReadCommandAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxCommandBytes];
        byte[] chunk = new byte[64];
        int count = 0;

        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                // The peer closed without a newline. What it sent is still treated as its command.
                return (Decode(buffer, count), false);
            }

            for (int index = 0; index < read; index++)
            {
                if (chunk[index] == LineFeed)
                {
                    // A Windows client may write CRLF. Accepting the trailing carriage return costs
                    // nothing and makes the protocol work for a plain text writer.
                    return (Decode(buffer, count).TrimEnd('\r'), false);
                }

                if (count == buffer.Length)
                {
                    return (Decode(buffer, count), true);
                }

                buffer[count++] = chunk[index];
            }
        }
    }

    private static string Decode(byte[] buffer, int count) =>
        count == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, count);

    /// <summary>
    /// Makes a command safe to put in a log line. This is another process's bytes: it is truncated,
    /// and anything that would move a cursor in whatever reads the log is replaced.
    /// </summary>
    private static string Sanitize(string payload)
    {
        string bounded = payload.Length <= LoggedPayloadLength ? payload : payload[..LoggedPayloadLength];

        return string.Create(
            bounded.Length,
            bounded,
            static (span, source) =>
            {
                for (int index = 0; index < source.Length; index++)
                {
                    span[index] = char.IsControl(source[index]) ? ' ' : source[index];
                }
            });
    }

    private void RaiseActivationRequested()
    {
        EventHandler? handler = ActivationRequested;

        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            // The pipe server outlives a bad handler: a launch that could not be shown still has to
            // leave the server able to hear the next one.
            _logger?.Write(LogLevel.Error, "singleInstance.activationHandlerFailed", exception: exception);
        }
    }
}
