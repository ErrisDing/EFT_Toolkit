using System.Drawing;
using System.Windows.Forms;

namespace EftToolkit.App.Tray;

/// <summary>
/// The notification-area icon, its menu, and the two things it can ask the application to do.
/// </summary>
/// <remarks>
/// <para>
/// WinForms in a WPF process, which is the arrangement the tray forces on any WPF application:
/// <see cref="NotifyIcon"/> is the supported way to put an icon in the notification area, and it
/// needs a message pump. The window is created on the WPF dispatcher's thread, so the dispatcher's
/// pump is what delivers the icon's messages, and every callback below therefore runs on the UI
/// thread.
/// </para>
/// <para>
/// What the icon means is decided by whoever built it: this type knows about a notification area and
/// a context menu, and nothing about windows, modules, or shutdown.
/// </para>
/// </remarks>
public sealed class NotifyIconHost : IDisposable
{
    /// <summary>
    /// The text a hover shows. Short because the notification area truncates it, and the version is
    /// left out because the user found this by running it.
    /// </summary>
    public const string TooltipText = "EFT Toolkit";

    /// <summary>What the user clicks to get the window back.</summary>
    public const string OpenMenuText = "打开面板";

    /// <summary>The only command that ends the application.</summary>
    public const string ExitMenuText = "退出";

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    private bool _disposed;

    /// <param name="open">Brings the window back. Runs on the UI thread.</param>
    /// <param name="exit">Ends the application. Runs on the UI thread.</param>
    public NotifyIconHost(Action open, Action exit)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(exit);

        ToolStripMenuItem openItem = new(OpenMenuText);
        openItem.Click += (_, _) => open();

        ToolStripMenuItem exitItem = new(ExitMenuText);
        exitItem.Click += (_, _) => exit();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(openItem);
        _menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            // The shared system icon is deliberately not disposed: Windows hands out the same
            // instance to everyone who asks for it.
            Icon = SystemIcons.Application,
            Text = TooltipText,
            ContextMenuStrip = _menu,
            Visible = true,
        };

        // The same thing the menu's first item does, because double-clicking an icon is the gesture
        // people reach for first.
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>Whether the icon is currently in the notification area.</summary>
    public bool IsVisible => !_disposed && _icon.Visible;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Removed from the notification area before anything is released. An icon whose owner has
        // gone leaves a ghost in the tray until the user hovers over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
