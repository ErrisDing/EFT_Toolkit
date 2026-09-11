<#
.SYNOPSIS
    Presses a real function key with the game focused and watches whether the desktop ramp follows.

.DESCRIPTION
    The toolkit's own log says the key arrived and the preset was applied. This asks the separate
    question of whether the desktop gamma ramp is the ramp the game is drawn with: it samples the
    display before, during and after the keypress, so the three explanations are told apart by
    measurement rather than argument.

      never moves                - the write is not reaching the display
      moves and comes back       - something else is overriding it
      moves and stays            - the desktop ramp is being applied and the game is drawn with it
#>
[CmdletBinding()]
param(
    [string]$GameProcess = 'EscapeFromTarkov',

    [ValidateSet('Low', 'Medium', 'High', 'Original')]
    [string]$Preset = 'High',

    [ValidateSet('Low', 'Medium', 'High', 'Original')]
    [string]$Baseline = 'Original',

    [int]$Samples = 12,

    [int]$IntervalMs = 120
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class InGameEffect
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateDC(string driver, string device, string port, IntPtr data);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool GetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    private const uint KeyUp = 0x0002;

    public static void Press(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
    }

    public static int RampSum
    {
        get
        {
            IntPtr dc = CreateDC("DISPLAY", null, null, IntPtr.Zero);

            try
            {
                ushort[] ramp = new ushort[768];

                if (!GetDeviceGammaRamp(dc, ramp))
                {
                    return -1;
                }

                int sum = 0;

                for (int index = 0; index < 256; index++)
                {
                    sum += ramp[index];
                }

                return sum;
            }
            finally
            {
                DeleteDC(dc);
            }
        }
    }

    public static string ForegroundProcess
    {
        get
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
}
'@

$virtualKeys = @{ Original = 0x71; Low = 0x72; Medium = 0x73; High = 0x74 }

$game = Get-Process $GameProcess -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1

if (-not $game) {
    "$GameProcess is not running with a window."
    exit 1
}

[void][InGameEffect]::ShowWindow($game.MainWindowHandle, 9)
Start-Sleep -Milliseconds 400
[void][InGameEffect]::SetForegroundWindow($game.MainWindowHandle)
Start-Sleep -Milliseconds 1200

"foreground   : $([InGameEffect]::ForegroundProcess)"
""

# Parked on a known baseline through the same path the preset under test uses, so the two sums are
# comparable and the difference between them is the preset and nothing else.
"baseline preset : $Baseline"
[InGameEffect]::Press([byte]$virtualKeys[$Baseline])
Start-Sleep -Milliseconds 900

$baselineSum = [InGameEffect]::RampSum
"baseline ramp   : $baselineSum"
""

"pressing $Preset..."
[InGameEffect]::Press([byte]$virtualKeys[$Preset])
""

$seen = @()

for ($sample = 1; $sample -le $Samples; $sample++) {
    Start-Sleep -Milliseconds $IntervalMs
    $sum = [InGameEffect]::RampSum
    $seen += $sum
    "  t=$($sample * $IntervalMs)ms  ramp sum = $sum"
}

""
"foreground after: $([InGameEffect]::ForegroundProcess)"
""

$distinct = $seen | Select-Object -Unique

if ($distinct.Count -eq 1 -and $distinct[0] -eq $baselineSum) {
    "VERDICT: the ramp never moved. The keypress did not reach the display."
}
elseif ($distinct[-1] -eq $baselineSum -and $distinct.Count -gt 1) {
    "VERDICT: the ramp moved and came back to $baselineSum. Something is overriding it."
}
else {
    "VERDICT: the ramp moved from $baselineSum to $($distinct[-1]) and stayed there."
    "         The desktop ramp holds while the game is focused. If the screen still does not change,"
    "         the game is not being drawn through the desktop ramp."
}
