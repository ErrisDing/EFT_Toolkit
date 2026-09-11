namespace EftToolkit.App.Dialogs;

/// <summary>
/// The questions and messages that interrupt the user. Behind an interface so the exit rules can be
/// tested without a message box, and so the answers are explicit rather than implied by a return
/// value nobody reads.
/// </summary>
public interface IUserDialogService
{
    /// <summary>
    /// Asks whether to exit while the target application is still playing through the virtual
    /// endpoint. Returns <see langword="false"/> when the user cancels, which leaves everything
    /// running.
    /// </summary>
    bool ConfirmExitWithActiveAudioRoute();

    /// <summary>
    /// Reports a failure the application could not continue past. There is no return value: the only
    /// thing that follows is shutdown.
    /// </summary>
    void ShowFatalError(string message);
}
