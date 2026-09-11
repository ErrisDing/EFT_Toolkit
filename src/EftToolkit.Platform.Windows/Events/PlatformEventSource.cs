using System.Threading.Channels;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Platform;
using EftToolkit.Platform.Windows.Messaging;

namespace EftToolkit.Platform.Windows.Events;

/// <summary>
/// Turns the Windows messages that invalidate a gamma ramp into one debounced notification.
/// </summary>
/// <remarks>
/// The subscription runs on the message-window thread, so it only drops an entry into a channel and
/// returns. A worker raises the event once the messages stop arriving, which both keeps gamma I/O
/// off the message thread and lets a change that arrives in several pieces settle before the preset
/// is reapplied. A <c>WM_DISPLAYCHANGE</c> storm would otherwise trigger a full re-enumeration and
/// rewrite for every message in it.
/// </remarks>
public sealed class PlatformEventSource : IPlatformEventSource
{
    /// <summary>The quiet period the plan fixes for collapsing a burst of environment events.</summary>
    public static readonly TimeSpan DefaultDebounceWindow = TimeSpan.FromMilliseconds(500);

    private readonly IWindowsMessageSource _messageSource;
    private readonly TimeSpan _debounceWindow;
    private readonly TimeProvider _timeProvider;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Channel<bool>? _signals;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private bool _started;

    public PlatformEventSource(
        IWindowsMessageSource messageSource,
        TimeSpan? debounceWindow = null,
        TimeProvider? timeProvider = null,
        IAppLogger? logger = null)
    {
        _messageSource = messageSource ?? throw new ArgumentNullException(nameof(messageSource));
        _debounceWindow = debounceWindow ?? DefaultDebounceWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_debounceWindow, TimeSpan.Zero);
    }

    /// <summary>Wires the real message window.</summary>
    public static PlatformEventSource Create(IAppLogger? logger = null) =>
        new(new WindowsMessageSink(logger), DefaultDebounceWindow, TimeProvider.System, logger);

    public event EventHandler? DisplayEnvironmentChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            _workerCancellation = new CancellationTokenSource();
            _messageSource.MessageReceived += OnMessageReceived;
            _worker = Task.Run(() => RunAsync(_signals.Reader, _workerCancellation.Token));

            await _messageSource.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _messageSource.MessageReceived -= OnMessageReceived;

            if (_workerCancellation is not null)
            {
                await _workerCancellation.CancelAsync().ConfigureAwait(false);
            }

            if (_worker is not null)
            {
                try
                {
                    await _worker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
            }

            _workerCancellation?.Dispose();
            _workerCancellation = null;
            _worker = null;
            _signals = null;

            await _messageSource.StopAsync(cancellationToken).ConfigureAwait(false);
            _started = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "platform.events.stopFailed", exception: exception);
        }

        await _messageSource.DisposeAsync().ConfigureAwait(false);
    }

    private void OnMessageReceived(object? sender, WindowsMessage message)
    {
        if (!WindowsMessageDecoder.IsDisplayEnvironmentChanged(in message))
        {
            return;
        }

        // Non-blocking by contract: this runs on the message-window thread.
        _signals?.Writer.TryWrite(true);
    }

    private async Task RunAsync(ChannelReader<bool> signals, CancellationToken cancellationToken)
    {
        try
        {
            while (await signals.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (signals.TryRead(out _))
                {
                    // Drain the burst that is already queued; one wait covers all of it.
                }

                // Keep waiting while more keep arriving, so a change that Windows reports in stages
                // is applied once, against the arrangement it settled on.
                while (true)
                {
                    await Task.Delay(_debounceWindow, _timeProvider, cancellationToken).ConfigureAwait(false);

                    if (!signals.TryRead(out _))
                    {
                        break;
                    }

                    while (signals.TryRead(out _))
                    {
                        // Drained above; this swallows the rest of the settling burst.
                    }
                }

                _logger?.Write(LogLevel.Information, "platform.events.displayEnvironmentChanged");
                DisplayEnvironmentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Error, "platform.events.failed", exception: exception);
        }
    }
}
