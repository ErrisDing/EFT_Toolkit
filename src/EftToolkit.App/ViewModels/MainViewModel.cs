using System.IO;
using EftToolkit.App.Commands;
using EftToolkit.App.Lifecycle;
using EftToolkit.Audio;
using EftToolkit.Audio.Devices;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Display;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// The whole panel: the two halves, the footer, and the one flag the close button consults.
/// </summary>
/// <remarks>
/// <para>
/// The two view models are built here and never talk to each other, which is what keeps a fault in
/// one half from reaching the other. The only thing they share is the window's timer that asks them
/// to refresh, and a refresh that throws is reported by the caller rather than propagated.
/// </para>
/// <para>
/// Everything shown is read from the coordinator and the modules at the moment it is asked for. This
/// object holds no state of its own beyond the message the user was last shown, so there is no
/// second copy of the truth to disagree with the first.
/// </para>
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly ToolkitCoordinator _coordinator;
    private readonly IAppLogger? _logger;
    private readonly Action<string> _open;

    private bool _isShuttingDown;
    private string? _latestWarning;

    /// <param name="open">
    /// How a link in the panel is opened. Injected so that a test can say which target a command
    /// used without a browser or a Settings page appearing.
    /// </param>
    public MainViewModel(
        ToolkitCoordinator coordinator,
        DisplayModule display,
        AudioModule audio,
        IAudioDeviceCatalog catalog,
        Action<string>? open = null,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(catalog);

        _coordinator = coordinator;
        _logger = logger;
        _open = open ?? ShellLauncher.Open;

        Display = new DisplayViewModel(display, coordinator, ReportError);
        Audio = new AudioViewModel(audio, coordinator, catalog, _open, ReportError, timeProvider);

        OpenLogsFolderCommand = new AsyncRelayCommand(OpenLogsFolderAsync, ReportError);
    }

    public DisplayViewModel Display { get; }

    public AudioViewModel Audio { get; }

    /// <summary>Opens the folder the log is written to, creating it if this is the first run.</summary>
    public AsyncRelayCommand OpenLogsFolderCommand { get; }

    /// <summary>
    /// Whether the application is on its way out. Set by the exit path, and the only thing that
    /// makes the close button close rather than hide.
    /// </summary>
    public bool IsShuttingDown
    {
        get => _isShuttingDown;
        set => SetProperty(ref _isShuttingDown, value);
    }

    /// <summary>
    /// The last thing that went wrong, or <see langword="null"/> when nothing has. Sticky: a failure
    /// that scrolled away with the next refresh would be a failure the user never read.
    /// </summary>
    public string? LatestWarning => _latestWarning;

    /// <summary>The state of both halves in one line, for the header.</summary>
    public string SummaryText =>
        "显示：" + Display.StatusText + "　音频：" + Audio.StatusText;

    public bool HasWarning => _latestWarning is not null;

    /// <summary>Reads the monitors and the audio endpoints once, before the window is shown.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Display.InitializeAsync(cancellationToken).ConfigureAwait(true);
        await Audio.InitializeAsync(cancellationToken).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Re-reads what the panel shows. Called from the window's timer.</summary>
    public void Refresh()
    {
        Display.Refresh();
        Audio.Refresh();

        OnPropertiesChanged(nameof(SummaryText), nameof(LatestWarning), nameof(HasWarning));
    }

    /// <summary>Moves the meters. Called from the window's timer, more often than they change.</summary>
    public void Sample() => Audio.Sample();

    /// <summary>
    /// Puts a failure in front of the user rather than throwing it at a binding. Every command in
    /// the panel reports through here, which is why there is no copy of a failure anywhere else.
    /// </summary>
    public void ReportError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _logger?.Write(
            LogLevel.Error,
            "panel.commandFailed",
            new Dictionary<string, object?> { ["message"] = exception.Message },
            exception);

        _latestWarning = "操作失败：" + exception.Message;
        OnPropertiesChanged(nameof(LatestWarning), nameof(HasWarning));
    }

    /// <summary>
    /// Shuts the toolkit down. The flag is set before the coordinator is asked, so a close that
    /// arrives while shutdown is running does not get turned into a hide.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        IsShuttingDown = true;

        await _coordinator.ShutdownAsync(cancellationToken).ConfigureAwait(true);
    }

    private Task OpenLogsFolderAsync()
    {
        // Created first: on a first run the log directory does not exist yet, and the explorer
        // cannot open a folder that is not there.
        Directory.CreateDirectory(JsonLineLogger.DefaultRoot);
        _open(JsonLineLogger.DefaultRoot);

        return Task.CompletedTask;
    }
}
