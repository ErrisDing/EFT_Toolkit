using System.Runtime.InteropServices;

namespace EftToolkit.Display.Interop;

/// <summary>
/// The GDI display-context and gamma-ramp entry points. Every call here takes a device context
/// owned and released by the caller: a device context is never shared across calls or threads.
/// </summary>
internal static partial class Gdi32
{
    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateDC(string? driver, string device, string? output, nint initData);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDeviceGammaRamp(nint hdc, nint ramp);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDeviceGammaRamp(nint hdc, nint ramp);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);
}
