using System.Windows;

namespace EftToolkit.App.Dialogs;

/// <summary>
/// The real message boxes.
/// </summary>
/// <remarks>
/// <see cref="System.Windows.Forms"/> is imported by the tray host in this same assembly, so
/// <c>MessageBox</c> is ambiguous and is always spelled out here.
/// </remarks>
public sealed class WpfUserDialogService : IUserDialogService
{
    /// <summary>
    /// The title the window carries. Repeated rather than read from the window: a message box may be
    /// shown when there is no window to read it from.
    /// </summary>
    public const string Title = "EFT Toolkit";

    /// <summary>
    /// What the user is asked when they exit while the target is still playing through the virtual
    /// endpoint. It is a constant so that the two things it has to say — that forwarding stops, and
    /// that the user must route the target back themselves — can be checked without a desktop.
    /// </summary>
    public const string ExitWithActiveAudioRouteQuestion =
        "音频增强正在运行。\n\n" +
        "现在退出会停止虚拟声卡（Cable）的转发，游戏将不再有声音，直到你把它重新路由到物理声卡。\n\n" +
        "请先在 Windows 的「设置 → 系统 → 声音 → 音量合成器」中，把游戏的输出设备改回物理声卡。\n\n" +
        "仍要退出吗？";

    public bool ConfirmExitWithActiveAudioRoute()
    {
        // The safe answer is the one that changes nothing, so it is what the Enter key takes and
        // what the user has to move away from.
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            ExitWithActiveAudioRouteQuestion,
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    public void ShowFatalError(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
