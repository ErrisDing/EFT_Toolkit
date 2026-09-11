<#
.SYNOPSIS
    Applies a preset to the running toolkit and watches whether the display keeps it.

.DESCRIPTION
    Answers "the shortcut fired but nothing changed" by separating the two causes: a ramp that is
    written and taken back by something else, and a ramp that is never written at all. The preset is
    posted to the toolkit's own message window, so no key has to be pressed and the test is
    repeatable; the values are read from the display, not from the application's own report.

    A sum that jumps and then returns to its starting value is a ramp something else is overriding.
    A sum that never moves is a write that did not reach the display.

    The app must be running. Read-only against the display: the only ramp written is the preset the
    toolkit itself would have written.
#>
[CmdletBinding()]
param(
    [ValidateSet('Original', 'Low', 'Medium', 'High')]
    [string]$Preset = 'High',

    [int]$Samples = 20,

    [int]$IntervalMs = 100
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class RampHoldProbe
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateDC(string driver, string device, string port, IntPtr data);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool GetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    public static IntPtr FindSink(uint pid)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hwnd, param) =>
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);

            if (owner == pid)
            {
                StringBuilder name = new StringBuilder(256);
                GetClassName(hwnd, name, 256);

                if (name.ToString().StartsWith("EftToolkit.MessageSink", StringComparison.Ordinal))
                {
                    found = hwnd;
                }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    // Properties rather than methods, deliberately. PowerShell 5.1 needs the parentheses on a static
    // method call, and a missing paren yields the method object - which this script would then print
    // as a ramp sum without ever reading a ramp. A property cannot be read any other way.
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
            IntPtr hwnd = GetForegroundWindow();
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);

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

$app = Get-Process EftToolkit.App -ErrorAction SilentlyContinue

if (-not $app) {
    "The toolkit is not running. Start it first."
    exit 1
}

$sink = [RampHoldProbe]::FindSink([uint32]$app.Id)

if ($sink -eq [IntPtr]::Zero) {
    "No message window found for the toolkit (pid $($app.Id))."
    exit 1
}

$identifiers = @{ Original = 0xEF20; Low = 0xEF21; Medium = 0xEF22; High = 0xEF23 }

"toolkit pid   : $($app.Id)"
"message window: 0x$($sink.ToInt64().ToString('X'))"
"foreground    : $([RampHoldProbe]::ForegroundProcess)"
""

$baseline = [RampHoldProbe]::RampSum
"baseline ramp sum: $baseline"
""

[void][RampHoldProbe]::PostMessage($sink, 0x0312, [IntPtr]$identifiers[$Preset], [IntPtr]::Zero)
"posted preset: $Preset"
""

$seen = @()

for ($sample = 1; $sample -le $Samples; $sample++) {
    Start-Sleep -Milliseconds $IntervalMs
    $sum = [RampHoldProbe]::RampSum
    $seen += $sum
    "  t=$($sample * $IntervalMs)ms  ramp sum = $sum"
}

""
$distinct = $seen | Select-Object -Unique

if ($distinct.Count -eq 1 -and $distinct[0] -eq $baseline) {
    "VERDICT: the ramp never moved. The preset did not reach the display."
}
elseif ($distinct[-1] -eq $baseline -and $distinct.Count -gt 1) {
    "VERDICT: the ramp moved and then returned to $baseline. Something else is overriding it."
}
else {
    "VERDICT: the ramp moved and stayed at $($distinct[-1]). The preset is being displayed."
}
