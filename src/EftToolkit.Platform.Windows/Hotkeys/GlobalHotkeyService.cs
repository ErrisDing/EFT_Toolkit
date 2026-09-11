using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Display;
using EftToolkit.Core.Platform;
using EftToolkit.Platform.Windows.Messaging;
using EftToolkit.Platform.Windows.Processes;

namespace EftToolkit.Platform.Windows.Hotkeys;

/// <summary>
/// Holds the F2-F5 shortcuts and turns them into preset requests. No keyboard hook is installed:
/// the shortcuts are registered with Windows and delivered as ordinary messages to
/// <see cref="IWindowsMessageSource"/>.
/// </summary>
public sealed class GlobalHotkeyService : IHotkeyService
{
    private readonly IWindowsMessageSource _messageSource;
    private readonly IHotkeyRegistrar _registrar;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Executables whose elevation deprives the shortcuts of the keyboard while they have focus.
    /// </summary>
    /// <remarks>
    /// The toolkit's reason to exist is one game, so the names are stated rather than derived: an
    /// elevated process that is not the game is not a conflict the user needs told about, and the
    /// audio profiles that also name these executables belong to a module the display half must not
    /// depend on.
    /// </remarks>
    private static readonly string[] GamesWatchedByTheToolkit = ["EscapeFromTarkov", "EscapeFromTarkov_BE"];

    /// <summary>Per-preset registration outcome, read by the panel and written by the message thread.</summary>
    private readonly Dictionary<DisplayPresetKind, bool> _registrations = [];

    private bool _registered;

    public GlobalHotkeyService(
        IWindowsMessageSource messageSource,
        IHotkeyRegistrar registrar,
        IAppLogger? logger = null)
    {
        _messageSource = messageSource ?? throw new ArgumentNullException(nameof(messageSource));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _logger = logger;

        // Every preset is present from the start, so a caller reading Registrations before the
        // shortcuts are taken sees "not held" rather than a missing key.
        foreach (DisplayPresetKind preset in WindowsMessageDecoder.Presets)
        {
            _registrations[preset] = false;
        }
    }

    /// <summary>Wires the real message window and the real Win32 registrations.</summary>
    public static GlobalHotkeyService Create(IAppLogger? logger = null) =>
        new(new WindowsMessageSink(logger), new Win32HotkeyRegistrar(), logger);

    public event EventHandler<DisplayPresetKind>? PresetRequested;

    public IReadOnlyDictionary<DisplayPresetKind, bool> Registrations
    {
        get
        {
            lock (_registrations)
            {
                return new Dictionary<DisplayPresetKind, bool>(_registrations);
            }
        }
    }

    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_registered)
            {
                return;
            }

            await _messageSource.StartAsync(cancellationToken).ConfigureAwait(false);
            _messageSource.MessageReceived += OnMessageReceived;

            foreach (DisplayPresetKind preset in WindowsMessageDecoder.Presets)
            {
                int hotkeyId = WindowsMessageDecoder.HotkeyIdFor(preset);
                uint virtualKey = WindowsMessageDecoder.VirtualKeyFor(preset);
                nint window = _messageSource.WindowHandle;

                bool held;
                try
                {
                    // Registered on the window's own thread, which Windows requires of RegisterHotKey.
                    held = await _messageSource
                        .InvokeAsync(
                            () => _registrar.TryRegister(window, hotkeyId, WindowsMessageDecoder.ModNoRepeat, virtualKey),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One preset that could not be queued must not cost the user the other three.
                    // Left to propagate, the first failure abandons the loop and the toolkit ends up
                    // with no shortcuts at all while claiming to have registered them.
                    SetRegistration(preset, false);

                    _logger?.Write(
                        LogLevel.Warning,
                        "platform.hotkey.registerFailed",
                        new Dictionary<string, object?> { ["preset"] = preset.ToString(), ["hotkeyId"] = hotkeyId },
                        exception);

                    continue;
                }

                SetRegistration(preset, held);

                if (!held)
                {
                    // A collision costs the user one shortcut, not all four, so it is reported and
                    // the loop continues.
                    _logger?.Write(
                        LogLevel.Warning,
                        "platform.hotkey.rejected",
                        new Dictionary<string, object?>
                        {
                            ["preset"] = preset.ToString(),
                            ["hotkeyId"] = hotkeyId,
                        });
                }
            }

            _registered = true;

            ReportElevationConflict();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records the one failure this service cannot see for itself.
    /// </summary>
    /// <remarks>
    /// A shortcut registered against this process is not delivered while an elevated window holds
    /// the foreground, and nothing here can detect that: <c>RegisterHotKey</c> reports success and
    /// the message is withheld below the application. Rather than leave the user with four working
    /// registrations and no explanation, the conflict is stated once, at the moment the shortcuts
    /// are taken.
    /// </remarks>
    private void ReportElevationConflict()
    {
        if (_logger is null || ProcessElevationProbe.IsCurrentProcessElevated())
        {
            // Running elevated is the case where the shortcuts do keep working, so there is nothing
            // to report - and a warning that fires on the working configuration trains the user to
            // ignore it.
            return;
        }

        IReadOnlyList<string> elevated = ProcessElevationProbe.FindElevatedProcesses(GamesWatchedByTheToolkit);

        if (elevated.Count == 0)
        {
            return;
        }

        _logger.Write(
            LogLevel.Warning,
            "platform.hotkey.elevationConflict",
            new Dictionary<string, object?>
            {
                ["elevatedProcesses"] = string.Join(", ", elevated),
                ["detail"] = "The shortcuts are registered but Windows withholds them while an elevated window has focus. "
                    + "Run this toolkit elevated as well, or run the game without elevation.",
            });
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_registered)
            {
                return;
            }

            _messageSource.MessageReceived -= OnMessageReceived;

            // Released before the window is destroyed: the registration is bound to the handle and
            // to the thread that made it, so tearing the window down first would leave Windows
            // holding a stale pair.
            foreach (DisplayPresetKind preset in WindowsMessageDecoder.Presets)
            {
                if (!IsRegistered(preset))
                {
                    continue;
                }

                int hotkeyId = WindowsMessageDecoder.HotkeyIdFor(preset);
                nint window = _messageSource.WindowHandle;

                await _messageSource
                    .InvokeAsync(
                        () =>
                        {
                            _registrar.Unregister(window, hotkeyId);
                            return true;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                SetRegistration(preset, false);
            }

            // The window is deliberately left running. It is shared with the platform event source,
            // which has to keep receiving display-change and session-unlock notifications while the
            // display shortcuts are off, and it is owned by the application rather than by this
            // service - the composition root disposes it once, after everything that uses it.
            //
            // Stopping it here also broke every later RegisterAsync: the sink hands back the handle
            // of the window its stop destroyed, so the four registrations failed one by one and the
            // user was left with no shortcuts until the application was restarted.
            _registered = false;
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
            await UnregisterAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "platform.hotkey.unregisterFailed", exception: exception);
        }

        // No DisposeAsync on the message source: it is not this service's to dispose, and disposing
        // it here would take the window away from the event source still holding it.
    }

    private void OnMessageReceived(object? sender, WindowsMessage message)
    {
        if (message.Id != WindowsMessageDecoder.WmHotkey)
        {
            return;
        }

        if (!WindowsMessageDecoder.TryDecodeHotkey((int)message.WParam, out DisplayPresetKind preset))
        {
            return;
        }

        if (!IsRegistered(preset))
        {
            // The identifier belongs to this toolkit but Windows refused the registration, so the
            // user never asked for this preset. Another application's message cannot be actioned.
            // Logged because it is the only way to tell this apart from a keypress that never
            // arrived, and the two look identical to the user: nothing happens.
            _logger?.Write(
                LogLevel.Warning,
                "platform.hotkey.pressedWhileUnregistered",
                new Dictionary<string, object?> { ["preset"] = preset.ToString() });

            return;
        }

        // The shortcut was pressed. Recorded at Information because it is the one point that
        // separates "the key was not captured" from "the key was captured and the ramp write did
        // not happen", and a log that omits it leaves both possibilities open.
        _logger?.Write(
            LogLevel.Information,
            "platform.hotkey.pressed",
            new Dictionary<string, object?> { ["preset"] = preset.ToString() });

        PresetRequested?.Invoke(this, preset);
    }

    private bool IsRegistered(DisplayPresetKind preset)
    {
        lock (_registrations)
        {
            return _registrations[preset];
        }
    }

    private void SetRegistration(DisplayPresetKind preset, bool held)
    {
        lock (_registrations)
        {
            _registrations[preset] = held;
        }
    }
}
