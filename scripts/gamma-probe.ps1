<#
.SYNOPSIS
    Reads the gamma ramp the display is actually using, right now.

.DESCRIPTION
    For diagnosing "the shortcut fires but nothing changes on screen". Run it once, press a preset
    shortcut, then run it again: a preset that reached the panel changes the first entries, while a
    preset that was applied and ignored leaves them identical. The values are the raw red ramp, which
    is monotonic on every display, so two runs can be compared by eye.

    This is a read-only diagnostic. It never writes a ramp.
#>
[CmdletBinding()]
param()

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class EftToolkitGammaProbe
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDC(string driver, string device, string port, IntPtr data);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool GetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}
'@

$dc = [EftToolkitGammaProbe]::CreateDC("DISPLAY", $null, $null, [IntPtr]::Zero)

if ($dc -eq [IntPtr]::Zero) {
    "CreateDC failed: win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    exit 1
}

try {
    $ramp = [System.UInt16[]]::new(768)

    if (-not [EftToolkitGammaProbe]::GetDeviceGammaRamp($dc, $ramp)) {
        "GetDeviceGammaRamp failed: win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
        exit 1
    }

    # The red channel only: three channels of the same curve would triple the output for no gain.
    $red = $ramp[0..255]

    "red ramp samples 0,1,2,3,64,128,192,255 : " + (@(0, 1, 2, 3, 64, 128, 192, 255) | ForEach-Object { $red[$_] }) -join ','
    "first sixteen: " + ($red[0..15] -join ',')
    "checksum: " + (($red | Measure-Object -Sum).Sum)
}
finally {
    [void][EftToolkitGammaProbe]::DeleteDC($dc)
}
