using System.IO;
using System.Windows;
using System.Windows.Threading;
using EftToolkit.App.Dialogs;
using EftToolkit.App.Lifecycle;
using EftToolkit.App.Tray;
using EftToolkit.App.ViewModels;
using EftToolkit.Audio;
using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Processes;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Display;
using EftToolkit.Display.Devices;
using EftToolkit.Display.Recovery;
using EftToolkit.Platform.Windows.Events;
using EftToolkit.Platform.Windows.Hotkeys;
using EftToolkit.Platform.Windows.Messaging;
using EftToolkit.Platform.Windows.SingleInstance;

namespace EftToolkit.App;

/// <summary>
/// WPF entry point and the one place that knows how the application is put together.
/// </summary>
/// <remarks>
/// <para>
/// Both <c>System.Windows</c> and <c>System.Windows.Forms</c> are imported because the tray icon uses
/// WinForms, so the base type is always spelled out here and WinForms types are reached through the
/// tray host rather than by name.
/// </para>
/// <para>
/// The order below is the whole point of this class. The single-instance claim comes before any
/// hardware is touched, so a second launch does not briefly capture a monitor's ramp or open an
/// audio stream. The configuration is read once and validated before the modules are built from it,
/// because both modules take their options in their constructors. The toolkit is started before the
/// panel is built, because the panel reports what the toolkit is doing rather than deciding it.
/// </para>
/// <para>
/// Nothing here holds a device back if something else failed. A half-built application is torn down
/// by the same exit path as a fully built one, which is why every field is nullable and every
/// disposal is a no-op when the step before it never ran.
/// </para>
/// </remarks>
public partial class App : System.Windows.Application
{
    /// <summary>The name the single-instance claim is made under.</summary>
    public const string InstanceName = "EftToolkit";

    /// <summary>What a fatal error says when there is nothing more specific to say.</summary>
    public const string UnexpectedFailureMessage = "EFT Toolkit 遇到无法继续的错误，即将退出。";

    private readonly WpfUserDialogService _dialogs = new();

    private JsonLineLogger? _logger;
    private NamedPipeSingleInstanceGate? _instanceGate;
    private WindowsMessageSink? _messageSink;
    private GlobalHotkeyService? _hotkeys;
    private PlatformEventSource? _platformEvents;
    private NAudioDeviceCatalog? _deviceCatalog;
    private ToolkitCoordinator? _coordinator;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private NotifyIconHost? _tray;

    /// <summary>Guards the once-only exit, which the tray, a fatal error, and the session can all ask for.</summary>
    private bool _exiting;

    /// <summary>Whether a fatal error has already been put in front of the user.</summary>
    private bool _fatalReported;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        JsonLineLogger logger = new(JsonLineLogger.DefaultRoot, TimeProvider.System);
        _logger = logger;

        // Installed before anything that can fail, so a failure during start-up is reported the same
        // way as one after it.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnProcessUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            await StartAsync(logger).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Fail(UnexpectedFailureMessage, exception);
        }
    }

    /// <summary>
    /// Claims the instance and, if this is the one that gets to run, builds and shows everything.
    /// </summary>
    private async Task StartAsync(JsonLineLogger logger)
    {
        NamedPipeSingleInstanceGate gate = new(InstanceName, logger);
        _instanceGate = gate;

        if (!await gate.AcquireAsync(CancellationToken.None).ConfigureAwait(true))
        {
            // Being second is a normal outcome for a tray application: the user launched it again
            // because the window is hidden, so the first copy is asked to show it.
            await NotifyPrimaryAsync(gate).ConfigureAwait(true);

            Shutdown(0);
            return;
        }

        // Raised on the pipe server's thread, so the handler marshals to the window's.
        gate.ActivationRequested += OnActivationRequested;

        JsonOptionsStore optionsStore = JsonOptionsStore.CreateDefault(logger);

        // Read here rather than left to the coordinator: both modules take their options when they
        // are constructed, which is before the coordinator is ever started.
        ToolkitOptions options = OptionsValidator.Validate(
            await optionsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true));

        WindowsMessageSink sink = new(logger);
        _messageSink = sink;

        // One message window for both services. Each of them would otherwise build its own, and two
        // hidden top-level windows receiving the same broadcast notifications is one more than the
        // machine needs.
        GlobalHotkeyService hotkeys = new(sink, new Win32HotkeyRegistrar(), logger);
        PlatformEventSource platformEvents = new(sink, logger: logger);
        _hotkeys = hotkeys;
        _platformEvents = platformEvents;

        DisplayModule display = new(
            options.Display,
            new Win32DisplayGammaGateway(logger),
            JsonDisplayRecoveryStore.CreateDefault(logger),
            logger);

        NAudioDeviceCatalog catalog = new(logger);
        _deviceCatalog = catalog;

        AudioModule audio = new(
            options.Audio,
            catalog,
            new PollingProcessMonitor(new WindowsProcessSnapshotProvider(), logger: logger),
            AudioStreamSessionFactory.Create(logger),
            optionsStore,
            logger);

        ToolkitCoordinator coordinator = new(display, audio, hotkeys, platformEvents, optionsStore, logger);
        _coordinator = coordinator;

        await coordinator.StartAsync(CancellationToken.None).ConfigureAwait(true);

        MainViewModel viewModel = new(coordinator, display, audio, catalog, logger: logger);
        _viewModel = viewModel;

        try
        {
            await viewModel.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Reading the monitors or the endpoints can fail on a machine the toolkit has never run
            // on. The panel is shown anyway: it is where the failure is reported, and its controls
            // are the way out of a state the toolkit could not start in.
            viewModel.ReportError(exception);
        }

        MainWindow window = new(viewModel);
        _window = window;

        _tray = new NotifyIconHost(() => ShowWindow(), () => _ = ExitAsync());

        window.Show();
    }

    /// <summary>
    /// Asks the instance that holds the claim to show itself. A primary that is not listening is
    /// reported and otherwise ignored: this copy is on its way out either way.
    /// </summary>
    private async Task NotifyPrimaryAsync(NamedPipeSingleInstanceGate gate)
    {
        try
        {
            await gate.NotifyPrimaryAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
        {
            _logger?.Write(LogLevel.Warning, "app.primaryUnreachable", exception: exception);
        }
    }

    private void OnActivationRequested(object? sender, EventArgs e) => ShowWindow();

    /// <summary>
    /// Shows the window, from whichever thread asked. A second launch arrives on the pipe server's
    /// thread, and the window belongs to the dispatcher's.
    /// </summary>
    private void ShowWindow()
    {
        if (_window is not { } window)
        {
            return;
        }

        if (!window.Dispatcher.CheckAccess())
        {
            _ = window.Dispatcher.InvokeAsync(ShowWindow);
            return;
        }

        window.ShowFromTray();
    }

    /// <summary>
    /// Ends the application: the confirmation, then the toolkit, then the window and the tray icon.
    /// </summary>
    /// <remarks>
    /// The entry the tray's Exit command reaches. Closing the window is not this path — that hides
    /// it — so this is the only place the shutdown happens, and it happens once.
    /// </remarks>
    private async Task ExitAsync(bool skipConfirmation = false)
    {
        if (_exiting)
        {
            return;
        }

        if (!skipConfirmation && NeedsExitConfirmation() && !_dialogs.ConfirmExitWithActiveAudioRoute())
        {
            // Cancelled: everything keeps running, and the next Exit is a fresh question.
            return;
        }

        _exiting = true;

        // Given up before the modules are torn down, so a user who launches the toolkit again while
        // this copy is on its way out gets a new instance rather than a notification to a process
        // that is about to close.
        await DisposeSafelyAsync(_instanceGate).ConfigureAwait(true);
        _instanceGate = null;

        _tray?.Dispose();
        _tray = null;

        try
        {
            if (_viewModel is { } viewModel)
            {
                await viewModel.ShutdownAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            // The coordinator is built to finish whatever happens, so this is the last resort rather
            // than the expected path. The process still ends, which is the point of the exit.
            _logger?.Write(LogLevel.Error, "app.shutdownFailed", exception: exception);
        }

        _window?.Close();
        _window = null;

        await TeardownAsync().ConfigureAwait(true);

        Shutdown(0);
    }

    /// <summary>
    /// Releases what the composition root owns. The modules have already been stopped by the
    /// coordinator's shutdown, so these are the objects it was handed rather than the ones it drove.
    /// </summary>
    private async Task TeardownAsync()
    {
        await DisposeSafelyAsync(_hotkeys).ConfigureAwait(true);
        await DisposeSafelyAsync(_platformEvents).ConfigureAwait(true);
        await DisposeSafelyAsync(_coordinator).ConfigureAwait(true);
        await DisposeSafelyAsync(_deviceCatalog).ConfigureAwait(true);
        await DisposeSafelyAsync(_messageSink).ConfigureAwait(true);
        await DisposeSafelyAsync(_logger).ConfigureAwait(true);

        _hotkeys = null;
        _platformEvents = null;
        _coordinator = null;
        _deviceCatalog = null;
        _messageSink = null;
        _logger = null;
    }

    /// <summary>Whether the exit has to be confirmed, as the audio module currently reports it.</summary>
    private bool NeedsExitConfirmation() => _viewModel is { } viewModel
        && WindowClosePolicy.RequiresExitConfirmation(
            viewModel.Audio.IsTargetRunning,
            viewModel.Audio.IsRouteOpen);

    /// <summary>
    /// Disposes something if it exists, and never lets that disposal become the problem. Every
    /// participant here is already stopping or stopped, so a failure to dispose one of them is not a
    /// reason to leave the rest held.
    /// </summary>
    private async Task DisposeSafelyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger?.Write(LogLevel.Warning, "app.disposeFailed", exception: exception);
        }
    }

    // ---------------------------------------------------------------- fatal failures

    /// <summary>
    /// A failure on the dispatcher's thread. The application is not required to survive one, and
    /// carrying on from a half-finished command would leave the panel describing a state that is no
    /// longer true, so it is reported and the toolkit is shut down.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Fail(UnexpectedFailureMessage, e.Exception);
    }

    /// <summary>
    /// A failure on a thread with nothing above it. The process is ending: what happens next is
    /// Windows tearing it down, so the failure is logged and no cleanup is claimed.
    /// </summary>
    private void OnProcessUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _logger?.Write(
            LogLevel.Error,
            "app.processFailed",
            new Dictionary<string, object?> { ["isTerminating"] = e.IsTerminating },
            e.ExceptionObject as Exception);

    /// <summary>
    /// A task whose failure nobody read. Observed and logged, and otherwise left alone: an abandoned
    /// task is a bug in one caller, not a reason to end the user's session.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();

        _logger?.Write(LogLevel.Warning, "app.unobservedTaskFailed", exception: e.Exception);
    }

    /// <summary>Reports a failure the toolkit cannot continue past, once, and shuts it down.</summary>
    private void Fail(string message, Exception? exception)
    {
        _logger?.Write(
            LogLevel.Error,
            "app.fatal",
            new Dictionary<string, object?> { ["message"] = message },
            exception);

        if (_fatalReported)
        {
            return;
        }

        _fatalReported = true;

        try
        {
            _dialogs.ShowFatalError(
                exception is null ? message : message + Environment.NewLine + Environment.NewLine + exception.Message);
        }
        catch (Exception dialogFailure)
        {
            // A dialog that cannot be shown must not stop the shutdown it was reporting.
            _logger?.Write(LogLevel.Error, "app.fatalDialogFailed", exception: dialogFailure);
        }

        _ = ExitAsync(skipConfirmation: true);
    }
}
