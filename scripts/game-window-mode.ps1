<#
.SYNOPSIS
    Reports whether the game window is borderless (a decorated/popup window DWM composites) or a
    fullscreen takeover (the display handed to the game).

.DESCRIPTION
    The distinction decides whether the desktop gamma ramp is the ramp the game is drawn with. A
    borderless window is composited like any other, so the desktop ramp applies. A display the game
    has taken for itself is not, so the ramp is written, holds, and is never seen.

    Read-only: reads the window's own style bits and rectangle.
#>
[CmdletBinding()]
param(
    [string]$GameProcess = 'EscapeFromTarkov'
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class WindowMode
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;

    public static string Describe(IntPtr hwnd, int screenWidth, int screenHeight)
    {
        int style = GetWindowLong(hwnd, GwlStyle);
        int exStyle = GetWindowLong(hwnd, GwlExStyle);

        Rect rect;
        GetWindowRect(hwnd, out rect);

        StringBuilder name = new StringBuilder(256);
        GetClassName(hwnd, name, 256);

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        bool coversScreen = rect.Left <= 0 && rect.Top <= 0
            && width >= screenWidth && height >= screenHeight;

        string report = "";
        report += "class         : " + name + Environment.NewLine;
        report += "style         : 0x" + style.ToString("X8")
            + "  caption=" + ((style & WsCaption) != 0)
            + " thickFrame=" + ((style & WsThickFrame) != 0)
            + " popup=" + ((style & WsPopup) != 0) + Environment.NewLine;
        report += "exStyle       : 0x" + exStyle.ToString("X8")
            + "  topmost=" + ((exStyle & WsExTopmost) != 0)
            + " noActivate=" + ((exStyle & WsExNoActivate) != 0) + Environment.NewLine;
        report += "rect          : " + rect.Left + "," + rect.Top + " " + width + "x" + height
            + "  coversScreen=" + coversScreen + Environment.NewLine;

        return report;
    }
}
'@

Add-Type -AssemblyName System.Windows.Forms

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

$game = Get-Process $GameProcess -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1

if (-not $game) {
    "$GameProcess is not running with a window."
    exit 1
}

"screen        : $($screen.Width)x$($screen.Height)"
"process       : $($game.ProcessName) pid=$($game.Id)"
""
[WindowMode]::Describe($game.MainWindowHandle, $screen.Width, $screen.Height)
