using System.Linq;
using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Routing;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// One audio endpoint in a picker.
/// </summary>
/// <remarks>
/// <para>
/// A device the toolkit cannot carry is still offered, and says why next to its name. Hiding the
/// user's own hardware from them is worse than refusing it: they would be looking at a list that
/// does not match the one Windows shows them, with nothing to say what happened to the missing
/// entry.
/// </para>
/// <para>
/// Whether an endpoint is supported is decided by the same rule the route validator applies, so a
/// picker cannot offer something the module would then refuse.
/// </para>
/// </remarks>
public sealed class AudioEndpointViewModel
{
    public AudioEndpointViewModel(AudioEndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Id = endpoint.Id;
        FriendlyName = endpoint.FriendlyName;
        Flow = endpoint.Flow;
        IsActive = endpoint.IsActive;
        Channels = endpoint.Channels;
        SampleRate = endpoint.SampleRate;

        string? reason = DescribeUnsupportedReason(endpoint);
        IsSupported = reason is null;
        DisplayName = reason is null ? FriendlyName : FriendlyName + "（不支持：" + reason + "）";
    }

    public string Id { get; }

    public string FriendlyName { get; }

    public AudioDataFlow Flow { get; }

    public bool IsActive { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>Whether the toolkit could open a stream on this endpoint.</summary>
    public bool IsSupported { get; }

    /// <summary>The name shown in the picker, with the reason when the device cannot be carried.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// What the picker shows. The combo box renders the items with their <c>ToString</c>, so this is
    /// the one place the displayed name is decided.
    /// </summary>
    public override string ToString() => DisplayName;

    private static string? DescribeUnsupportedReason(AudioEndpointDescriptor endpoint)
    {
        if (!endpoint.IsActive)
        {
            return "设备当前不可用";
        }

        if (endpoint.Channels != AudioRouteValidator.StereoChannelCount)
        {
            return "不是立体声（" + endpoint.Channels + " 声道）";
        }

        if (!AudioRouteValidator.SupportedSampleRates.Contains(endpoint.SampleRate))
        {
            return "采样率 " + endpoint.SampleRate + " Hz，仅支持 "
                + string.Join(" / ", AudioRouteValidator.SupportedSampleRates.Order()) + " Hz";
        }

        return null;
    }
}
