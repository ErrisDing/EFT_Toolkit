<#
.SYNOPSIS
    Reports whether advanced colour (HDR) is on for each display.

.DESCRIPTION
    Worth asking before blaming anything else. With advanced colour enabled the display output stops
    being an 8-bit LUT the desktop applies, and the classic gamma ramp is not the thing the screen is
    drawn with - which would look exactly like a toolkit that captures the key, writes the ramp, and
    changes nothing on screen.

    Read-only: queries DisplayConfig and reports.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class HdrState
{
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] public struct SourceMode { public LUID AdapterId; public uint Id; public uint ModeInfoIdx; }
    [StructLayout(LayoutKind.Sequential)] public struct PathInfo
    {
        public LUID AdapterId; public uint SourceId; public uint TargetId; public uint Flags;
        public SourceMode SourceInfo; public uint TargetInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct ModeInfo
    {
        public uint InfoType; public uint Size; public LUID AdapterId; public uint Id;
        public uint Flags; public uint Union1; public uint Union2; public uint Union3; public uint Union4;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Header
    {
        public uint Type; public uint Size; public LUID AdapterId; public uint Id; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct AdvancedColor
    {
        public Header Header; public uint Value; public uint ColorEncoding; public uint BitsPerColorChannel;
    }

    private const uint QdcOnlyActivePaths = 2;
    private const uint GetAdvancedColorInfo = 9;

    public static string Dump()
    {
        uint pathCount = 0;
        uint modeCount = 0;

        int error = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount);

        if (error != 0)
        {
            return "GetDisplayConfigBufferSizes -> " + error;
        }

        PathInfo[] paths = new PathInfo[pathCount];
        ModeInfo[] modes = new ModeInfo[modeCount];

        error = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

        if (error != 0)
        {
            return "QueryDisplayConfig -> " + error;
        }

        string report = "";

        for (uint index = 0; index < pathCount; index++)
        {
            AdvancedColor info = new AdvancedColor();
            info.Header.Type = GetAdvancedColorInfo;
            info.Header.Size = (uint)Marshal.SizeOf(typeof(AdvancedColor));
            info.Header.AdapterId = paths[index].AdapterId;
            info.Header.Id = paths[index].TargetInfo;

            int call = DisplayConfigGetDeviceInfo(ref info);

            report += "target " + paths[index].TargetId
                + " : err=" + call
                + " advancedColorEnabled=" + (info.Value & 1)
                + " wideColorEnforced=" + ((info.Value >> 1) & 1)
                + " bitsPerChannel=" + info.BitsPerColorChannel
                + " encoding=" + info.ColorEncoding
                + Environment.NewLine;
        }

        return report;
    }

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);
}
'@

[HdrState]::Dump()
