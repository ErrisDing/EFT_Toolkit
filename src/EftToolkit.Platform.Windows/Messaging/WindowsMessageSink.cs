using System.Globalization;
using System.Runtime.InteropServices;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Platform.Windows.Interop;

namespace EftToolkit.Platform.Windows.Messaging;

/// <summary>
/// A hidden top-level window on its own STA thread, used to receive global hotkeys and the
/// environment messages that a hidden or recreated UI window could not hold reliably.
/// </summary>
/// <remarks>
/// <para>
/// The window is a hidden top-level window rather than a message-only one, which is not a
/// stylistic choice. Windows refuses to register a hotkey against a <c>HWND_MESSAGE</c> window,
/// and broadcast messages such as <c>WM_DISPLAYCHANGE</c> and <c>WM_POWERBROADCAST</c> are only
/// sent to top-level windows, so a message-only window would receive neither the shortcuts nor
/// the notifications that a display was reconfigured.
/// </para>
/// <para>
/// Session notifications are registered here rather than by the caller that happens to need them.
/// They belong to the window handle, and tying them to the hotkey lifecycle would silently stop
/// session-unlock detection whenever the display shortcuts were disabled.
/// </para>
/// </remarks>
public sealed class WindowsMessageSink : IWindowsMessageSource
{
    private readonly IAppLogger? _logger;
    private readonly string _className = string.Create(
        CultureInfo.InvariantCulture,
        $"EftToolkit.MessageSink.{Guid.NewGuid():N}");

    // Held for the lifetime of the class registration: Windows keeps the function pointer, so a
    // collected delegate would be called as freed memory.
    private readonly NativeMethods.WindowProcedure _windowProcedure;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Replaced on every start, not created once. A completion source is single-use, so a sink that
    // was stopped and started again would await the completed one and be handed the handle of the
    // window the stop had already destroyed - after which every queued callback failed. Nothing in
    // the application stops the sink any more, but a sink that cannot be restarted is a trap for
    // whatever calls StopAsync next.
    private TaskCompletionSource<nint> _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private nint _instance;
    private nint _windowHandle;
    private GCHandle _selfHandle;
    private bool _sessionNotificationRegistered;
    private bool _started;
    private bool _disposed;

    public WindowsMessageSink(IAppLogger? logger = null)
    {
        _logger = logger;
        _windowProcedure = HandleMessage;
    }

    public event EventHandler<WindowsMessage>? MessageReceived;

    public nint WindowHandle => _windowHandle;

    public Task<T> InvokeAsync<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        ObjectDisposedException.ThrowIf(_disposed, this);

        nint window = _windowHandle;

        if (window == 0)
        {
            throw new InvalidOperationException("The message window is not running, so nothing can run on its thread.");
        }

        Invocation<T> invocation = new(callback);
        GCHandle handle = GCHandle.Alloc(invocation);

        if (!NativeMethods.PostMessage(window, NativeMethods.WmInvoke, 0, GCHandle.ToIntPtr(handle)))
        {
            handle.Free();
            throw new InvalidOperationException("The callback could not be queued to the message window's thread.");
        }

        return invocation.Task;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            // Before the thread starts, so the message loop cannot publish its window into the
            // previous run's completion source. StopAsync joins the thread before it returns, so no
            // earlier loop is still alive to overwrite this one.
            _windowReady = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);

            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "EftToolkit message sink",
            };

            // An STA thread, because session notifications are delivered to a message queue created
            // by a thread that Windows treats as a UI thread.
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            _windowHandle = await _windowReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_windowHandle == 0)
            {
                throw new InvalidOperationException("The message-only window could not be created.");
            }

            RegisterSessionNotification();
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

            // Unregistered before the window is destroyed, while the handle is still valid.
            UnregisterSessionNotification();

            nint window = _windowHandle;

            if (window != 0)
            {
                _ = NativeMethods.PostMessage(window, NativeMethods.WmClose, 0, 0);
            }

            Thread? thread = _thread;

            if (thread is not null)
            {
                await Task.Run(() => thread.Join(), cancellationToken).ConfigureAwait(false);
            }

            _windowHandle = 0;
            _thread = null;
            _started = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "platform.messageSink.stopFailed", exception: exception);
        }

        _disposed = true;
    }

    private void RegisterSessionNotification()
    {
        _sessionNotificationRegistered = NativeMethods.WTSRegisterSessionNotification(
            _windowHandle,
            NativeMethods.NotifyForThisSession);

        if (!_sessionNotificationRegistered)
        {
            // Losing this costs session-unlock reapplication but nothing else, so it is reported and
            // the shortcuts continue to work.
            _logger?.Write(
                LogLevel.Warning,
                "platform.sessionNotification.failed",
                new Dictionary<string, object?> { ["win32Error"] = Marshal.GetLastWin32Error() });
        }
    }

    private void UnregisterSessionNotification()
    {
        if (!_sessionNotificationRegistered)
        {
            return;
        }

        _ = NativeMethods.WTSUnRegisterSessionNotification(_windowHandle);
        _sessionNotificationRegistered = false;
    }

    private void RunMessageLoop()
    {
        try
        {
            _instance = NativeMethods.GetModuleHandle(null);

            if (!RegisterWindowClass())
            {
                _windowReady.TrySetResult(0);
                return;
            }

            _selfHandle = GCHandle.Alloc(this);

            nint window = NativeMethods.CreateWindowEx(
                extendedStyle: NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
                _className,
                windowName: string.Empty,
                style: NativeMethods.WsPopup,
                x: 0,
                y: 0,
                width: 0,
                height: 0,
                parent: 0,
                menu: 0,
                _instance,
                parameter: 0);

            if (window == 0)
            {
                _logger?.Write(
                    LogLevel.Error,
                    "platform.messageWindow.createFailed",
                    new Dictionary<string, object?> { ["win32Error"] = Marshal.GetLastWin32Error() });

                _windowReady.TrySetResult(0);
                return;
            }

            _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GwlpUserData, GCHandle.ToIntPtr(_selfHandle));

            // Published only once the window is fully configured, so a caller that registers a
            // hotkey immediately cannot race the user-data assignment.
            // Deliberately never shown: the window is a receiver, not a piece of UI.
            _windowReady.TrySetResult(window);

            PumpMessages();

            UnregisterWindowClass();

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Error, "platform.messageWindow.failed", exception: exception);
            _windowReady.TrySetResult(0);
        }
    }

    private unsafe bool RegisterWindowClass()
    {
        fixed (char* className = _className)
        {
            NativeMethods.WndClassEx windowClass = new()
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
                Instance = _instance,
                WndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                ClassName = (nint)className,
            };

            return NativeMethods.RegisterClassEx(ref windowClass) != 0;
        }
    }

    private void UnregisterWindowClass() => _ = NativeMethods.UnregisterClass(_className, _instance);

    private void PumpMessages()
    {
        int result;

        // GetMessage returns 0 for WM_QUIT and -1 for an error; either one ends the loop.
        while ((result = NativeMethods.GetMessage(out NativeMethods.Msg message, 0, 0, 0)) > 0)
        {
            _ = NativeMethods.TranslateMessage(in message);
            _ = NativeMethods.DispatchMessage(in message);
        }

        if (result < 0)
        {
            _logger?.Write(
                LogLevel.Error,
                "platform.messageLoop.failed",
                new Dictionary<string, object?> { ["win32Error"] = Marshal.GetLastWin32Error() });
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WmClose:
                _ = NativeMethods.DestroyWindow(hwnd);
                return 0;

            case NativeMethods.WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return 0;

            case NativeMethods.WmInvoke:
                RunInvocation(lParam);
                return 0;
        }

        MessageReceived?.Invoke(this, new WindowsMessage(hwnd, message, wParam, lParam));
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void RunInvocation(nint parameter)
    {
        GCHandle handle = GCHandle.FromIntPtr(parameter);

        try
        {
            if (handle.Target is Invocation invocation)
            {
                invocation.Execute();
            }
        }
        finally
        {
            // Freed whatever the callback did, so a throwing callback cannot leak the handle.
            handle.Free();
        }
    }

    /// <summary>A unit of work to run on the window's thread. Non-generic so the message carries one type.</summary>
    private abstract class Invocation
    {
        public abstract void Execute();
    }

    private sealed class Invocation<T> : Invocation
    {
        private readonly Func<T> _callback;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Invocation(Func<T> callback) => _callback = callback;

        public Task<T> Task => _completion.Task;

        public override void Execute()
        {
            try
            {
                _completion.TrySetResult(_callback());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
