namespace EftToolkit.App.Lifecycle;

/// <summary>What the window's close button does.</summary>
public enum WindowCloseAction
{
    /// <summary>Cancel the close and hide the window.</summary>
    Hide,

    /// <summary>Let the window close and the application end.</summary>
    Close,
}

/// <summary>
/// Decides what the close button does.
/// </summary>
/// <remarks>
/// Closing the window is how the window disappears, not how the application ends: both modules keep
/// running in the notification area. That is what makes a rule necessary at all, because the exit
/// path has to be able to close a window that the close button would otherwise refuse to close —
/// <see cref="MainWindow.Closing"/> consults this, and the tray's Exit command sets the flag that
/// lets the window go.
/// </remarks>
public static class WindowClosePolicy
{
    public static WindowCloseAction Decide(bool isShuttingDown) =>
        isShuttingDown ? WindowCloseAction.Close : WindowCloseAction.Hide;

    /// <summary>
    /// Whether the exit path has to ask before it stops the audio.
    /// </summary>
    /// <remarks>
    /// Both halves are required. Exiting closes the stream, and the toolkit never reroutes the
    /// application itself, so the user is left with a game whose audio goes nowhere until they put it
    /// back on a physical device — which is worth a question while the game is actually playing
    /// through the toolkit, and is not worth one when there is nothing to interrupt.
    /// </remarks>
    public static bool RequiresExitConfirmation(bool isTargetRunning, bool isRouteOpen) =>
        isTargetRunning && isRouteOpen;
}
