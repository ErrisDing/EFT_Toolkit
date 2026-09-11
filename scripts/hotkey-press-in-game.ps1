<#
.SYNOPSIS
    Presses a real function key while the game has focus and reports whether the toolkit saw it.

.DESCRIPTION
    Separate from the preset probe. That one posts WM_HOTKEY straight to the toolkit's window, which
    proves the apply path and bypasses the keyboard entirely. This one goes through the keyboard, so
    it is the only test that can tell "the key was taken by the game" apart from "the preset did not
    apply": the log entry platform.hotkey.pressed is written only when Windows delivers the
    shortcut, and its absence is as informative as its presence.

    The key is sent as real hardware input, which is what the OS delivers to the game.
#>
[CmdletBinding()]
param(
    [string]$GameProcess = 'EscapeFromTarkov',

    [ValidateSet('Low', 'Medium', 'High', 'Original')]
    [string]$Preset = 'High',

    [int]$Repeats = 3,

    [int]$SettleMs = 1200
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class KeyPress
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    public static void Press(byte virtualKey)
    {
        const uint KeyUp = 0x0002;

        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
    }
}
'@

$log = Join-Path $env:LOCALAPPDATA 'EftToolkit\logs\eft-toolkit.log'

if (-not (Test-Path $log)) {
    "No log at $log. Has the toolkit ever run?"
    exit 1
}

$game = Get-Process $GameProcess -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1

if (-not $game) {
    "$GameProcess is not running with a window."
    exit 1
}

$virtualKeys = @{ Original = 0x71; Low = 0x72; Medium = 0x73; High = 0x74 }
$functions   = @{ Original = 'F2'; Low = 'F3'; Medium = 'F4'; High = 'F5' }

# The log is read by length rather than parsed as JSON: everything already written is skipped, so the
# only lines that count are the ones this run produced.
$alreadyWritten = (Get-Item $log).Length

[void][KeyPress]::ShowWindow($game.MainWindowHandle, 9)
Start-Sleep -Milliseconds 400
[void][KeyPress]::SetForegroundWindow($game.MainWindowHandle)
Start-Sleep -Milliseconds 1500

"game pid : $($game.Id)"
"pressing : $($functions[$Preset]) ($Repeats times, $($Preset) preset)"
"log      : $log"
""
"waiting for the toolkit to react..."

Start-Sleep -Milliseconds $SettleMs

for ($press = 1; $press -le $Repeats; $press++) {
    [KeyPress]::Press([byte]$virtualKeys[$Preset])
    Start-Sleep -Milliseconds 700
}

Start-Sleep -Milliseconds $SettleMs

# A fresh read: the stream is positioned past everything written before the keys were pressed.
$stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
try {
    [void]$stream.Seek($alreadyWritten, 'Begin')

    $reader = New-Object System.IO.StreamReader($stream)
    $produced = @($reader.ReadToEnd() -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
}
finally {
    $stream.Dispose()
}

$pressed = @($produced | Where-Object { $_ -match '"event":"platform\.hotkey\.pressed"' })
$applied = @($produced | Where-Object { $_ -match '"event":"display\.preset\.applied"' })
$unreg   = @($produced | Where-Object { $_ -match 'pressedWhileUnregistered' })

"new log lines      : $($produced.Count)"
"hotkey.pressed     : $($pressed.Count)"
"display.preset.appl: $($applied.Count)"
"pressed-unregistered: $($unreg.Count)"
""

if ($pressed.Count -eq 0) {
    "VERDICT: the toolkit never saw the key. The game is taking $($functions[$Preset]) before Windows"
    "         delivers the shortcut."
    exit 1
}

if ($applied.Count -eq 0) {
    "VERDICT: the key arrived but no preset was applied. The failure is inside the toolkit."
    exit 1
}

"VERDICT: the key reached the toolkit and the preset was applied, $($applied.Count) time(s)."
"         If the screen did not change, the ramp is not what is on screen."
