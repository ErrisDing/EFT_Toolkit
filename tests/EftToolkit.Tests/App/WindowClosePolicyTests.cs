using EftToolkit.App.Lifecycle;

namespace EftToolkit.Tests.App;

/// <summary>
/// One rule, and the reason it exists: the close button hides the window instead of ending the
/// application, so the only way out is the tray's Exit command — which has to be able to close the
/// window it just shut down behind.
/// </summary>
public class WindowClosePolicyTests
{
    [Fact]
    public void A_close_while_the_toolkit_is_running_hides_the_window()
    {
        Assert.Equal(WindowCloseAction.Hide, WindowClosePolicy.Decide(isShuttingDown: false));
    }

    [Fact]
    public void A_close_during_shutdown_closes_the_window()
    {
        Assert.Equal(WindowCloseAction.Close, WindowClosePolicy.Decide(isShuttingDown: true));
    }

    [Fact]
    public void The_exit_question_is_asked_only_while_audio_is_passing_through_the_toolkit()
    {
        // Both halves have to hold: exiting stops the forwarding, and the user has to be the one who
        // puts the game back on a physical device afterwards.
        Assert.True(WindowClosePolicy.RequiresExitConfirmation(isTargetRunning: true, isRouteOpen: true));

        // A game that is not running has nothing playing to silence, and a route that is not open is
        // not carrying anything, so both are exits without a question.
        Assert.False(WindowClosePolicy.RequiresExitConfirmation(isTargetRunning: true, isRouteOpen: false));
        Assert.False(WindowClosePolicy.RequiresExitConfirmation(isTargetRunning: false, isRouteOpen: true));
        Assert.False(WindowClosePolicy.RequiresExitConfirmation(isTargetRunning: false, isRouteOpen: false));
    }
}
