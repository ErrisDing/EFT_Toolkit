<#
.SYNOPSIS
    Applies a preset through the toolkit and reports the properties of the ramp it produced.

.DESCRIPTION
    Answers "is this ramp strong enough to see?" with numbers rather than opinion, and at the same
    time shows what the display driver accepted. A ramp the panel takes is one that satisfies its
    contract; a ramp whose shape barely deviates from the identity is one that will be hard to see
    even though everything worked.

    Read-only against the display apart from the preset, which is the toolkit's own.
#>
[CmdletBinding()]
param(
    [ValidateSet('Original', 'Low', 'Medium', 'High')]
    [string]$Preset = 'High',

    [ValidateSet('Original', 'Low', 'Medium', 'High')]
    [string]$Compare = 'Original'
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class RampAnalyse
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
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

    public static ushort[] Read()
    {
        IntPtr dc = CreateDC("DISPLAY", null, null, IntPtr.Zero);

        try
        {
            ushort[] ramp = new ushort[768];

            return GetDeviceGammaRamp(dc, ramp) ? ramp : null;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    public static string Describe(ushort[] ramp)
    {
        if (ramp == null)
        {
            return "the ramp could not be read";
        }

        string[] names = { "red", "green", "blue" };
        string report = "";

        for (int channel = 0; channel < 3; channel++)
        {
            int offset = channel * 256;
            int largestStep = 0;
            int firstStep = ramp[offset + 1] - ramp[offset];
            bool steady = true;
            int deviation = 0;

            for (int index = 1; index < 256; index++)
            {
                int step = ramp[offset + index] - ramp[offset + index - 1];

                if (step < 0)
                {
                    steady = false;
                }

                if (Math.Abs(step) > largestStep)
                {
                    largestStep = Math.Abs(step);
                }

                deviation += Math.Abs(ramp[offset + index] - (index * 256));
            }

            report += "  " + names[channel].PadRight(5)
                + " start=" + ramp[offset]
                + " end=" + ramp[offset + 255]
                + " firstStep=" + firstStep
                + " largestStep=" + largestStep
                + " nonDecreasing=" + steady
                + " deviationFromIdentity=" + deviation
                + Environment.NewLine;
        }

        return report;
    }
}
'@

$app = Get-Process EftToolkit.App -ErrorAction SilentlyContinue

if (-not $app) {
    "The toolkit is not running. Start it first."
    exit 1
}

$sink = [RampAnalyse]::FindSink([uint32]$app.Id)

if ($sink -eq [IntPtr]::Zero) {
    "No message window found."
    exit 1
}

$identifiers = @{ Original = 0xEF20; Low = 0xEF21; Medium = 0xEF22; High = 0xEF23 }

function Apply([string]$which) {
    [void][RampAnalyse]::PostMessage($sink, 0x0312, [IntPtr]$identifiers[$which], [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}

Apply $Compare
$baseline = [RampAnalyse]::Read()

Apply $Preset
$tested = [RampAnalyse]::Read()

"preset applied : $Preset   (compared against $Compare)"
""
"$Compare ramp:"
[RampAnalyse]::Describe($baseline)
"$Preset ramp:"
[RampAnalyse]::Describe($tested)

# The visible question is how far the mid-tones move, which is where a gamma curve is felt.
"mid-tone movement (index 128), the part of the curve the eye judges:"
""
foreach ($channel in 0, 1, 2) {
    $name = @('red', 'green', 'blue')[$channel]
    $before = $baseline[($channel * 256) + 128]
    $after = $tested[($channel * 256) + 128]

    "  $name : $before -> $after   ($([Math]::Round((($after - $before) / 65535.0) * 100, 2))% of full range)"
}
