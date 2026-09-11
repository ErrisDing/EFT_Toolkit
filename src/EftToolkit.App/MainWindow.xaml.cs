using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using EftToolkit.App.Lifecycle;
using EftToolkit.App.ViewModels;

namespace EftToolkit.App;

/// <summary>
/// The panel window: it shows the toolkit, hides instead of closing, and keeps the panel's readings
/// up to date while it is on screen.
/// </summary>
/// <remarks>
/// <para>
/// The window holds no state of the application's. Everything it shows comes from the view model, and
/// the only things it decides are the two that belong to a window: what its close button does, and
/// how often to ask for a refresh.
/// </para>
/// <para>
/// Refreshing from a timer rather than from events is what keeps the modules' notifications on their
/// own threads. The modules are read from the thread that paints, at a rate the panel chooses, and no
/// event handler has to marshal anything.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// How often the panel re-reads the toolkit. Ten times a second, which is the rate the meters are
    /// limited to and faster than anything else on the panel can change.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = RefreshInterval,
        };

        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    /// Brings the window back from the notification area. Called by the tray icon and by a second
    /// launch of the application, both already on the dispatcher's thread.
    /// </summary>
    public void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// The close button hides the window rather than ending the application, unless the toolkit is
    /// already shutting down — which is the exit path closing the window it just stopped.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (WindowClosePolicy.Decide(_viewModel.IsShuttingDown) == WindowCloseAction.Hide)
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // The timer belongs to a window that no longer exists. Left running it would keep reading the
        // modules for a panel nobody can see.
        _timer.Stop();
        _timer.Tick -= OnTick;

        base.OnClosed(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _viewModel.Refresh();
        _viewModel.Sample();
    }
}
