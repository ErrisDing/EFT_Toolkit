<#
.SYNOPSIS
    Brings the game to the foreground and runs the ramp-hold probe against it.

.DESCRIPTION
    The failing configuration is "the game has focus", so the probe has to be run while it does. This
    focuses the game's own window and then leaves the sampling to ramp-hold-probe.ps1, which reads the
    display rather than trusting the application. A preset that is applied and immediately taken back
    is visible as a ramp sum that returns to where it started.

    The game must be running and the toolkit must be running.
#>
[CmdletBinding()]
param(
    [string]$GameProcess = 'EscapeFromTarkov',

    [string]$Preset = 'High',

    [int]$Samples = 40,

    [int]$IntervalMs = 125
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class GameFocus
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static string Foreground()
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);

        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            return "<gone>";
        }
    }
}
'@

$game = Get-Process $GameProcess -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }

if (-not $game) {
    "$GameProcess is not running with a window."
    exit 1
}

$game = $game | Select-Object -First 1
$handle = $game.MainWindowHandle

"game pid  : $($game.Id)"
"game hwnd : 0x$($handle.ToInt64().ToString('X'))"
""

# Restored before focusing: a minimised window cannot take the foreground.
[void][GameFocus]::ShowWindow($handle, 9)
Start-Sleep -Milliseconds 400
[void][GameFocus]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 1500

"foreground now: $([GameFocus]::Foreground())"
""

& (Join-Path $PSScriptRoot 'ramp-hold-probe.ps1') -Preset $Preset -Samples $Samples -IntervalMs $IntervalMs
