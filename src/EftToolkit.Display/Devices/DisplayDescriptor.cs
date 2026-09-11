using System.Globalization;

namespace EftToolkit.Display.Devices;

/// <summary>
/// A display as the toolkit sees it. <see cref="StableId"/> is the value persisted in settings, so it
/// must survive a reboot, a resolution change and a cable swap between identical ports.
/// </summary>
public sealed record DisplayDescriptor(
    string StableId,
    string GdiDeviceName,
    string FriendlyName,
    bool IsConnected,
    bool IsHdr,
    bool SupportsGammaRamp)
{
    /// <summary>
    /// Builds the identifier persisted in settings, strongest source first:
    /// <list type="number">
    /// <item>the monitor device path, derived from the monitor's EDID and stable across reboots;</item>
    /// <item>the adapter LUID and target id, stable until the adapter is re-enumerated; this is what
    /// virtual and remote adapters that report no device path get;</item>
    /// <item>the GDI device name, which is only unique while the display stays on the same port.</item>
    /// </list>
    /// The final tier exists for adapters that report neither a path nor a usable LUID, where the
    /// alternative would be an empty identifier shared by every such display.
    /// </summary>
    public static string CreateStableId(
        string? monitorDevicePath,
        long adapterLuid,
        uint targetId,
        string? gdiDeviceName = null)
    {
        if (!string.IsNullOrWhiteSpace(monitorDevicePath))
        {
            return monitorDevicePath.Trim();
        }

        if (adapterLuid != 0 || targetId != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{adapterLuid:X16}:{targetId}");
        }

        return gdiDeviceName is null ? string.Empty : gdiDeviceName.Trim();
    }

    /// <summary>Falls back to the GDI device name when the driver reports no friendly name.</summary>
    public static string CreateFriendlyName(string? friendlyName, string gdiDeviceName)
    {
        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            return friendlyName.Trim();
        }

        return string.IsNullOrWhiteSpace(gdiDeviceName) ? string.Empty : gdiDeviceName.Trim();
    }
}
