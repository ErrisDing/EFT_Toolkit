using EftToolkit.Audio.Dsp;

namespace EftToolkit.Audio.Streaming;

/// <summary>
/// What the audio path is doing right now, for the status panel and the log.
/// </summary>
/// <param name="Underruns">
/// Reads that had to be padded with silence. Rising means the capture side is not keeping up, and
/// what the user hears is dropouts rather than delay.
/// </param>
/// <param name="Overruns">
/// Captured blocks dropped because the ring was full. Rising means the render side is not keeping
/// up, or the two endpoints' clocks are drifting apart faster than the ring can absorb.
/// </param>
/// <param name="CaptureBufferMilliseconds">The buffer the capture endpoint was opened with.</param>
/// <param name="RenderBufferMilliseconds">The buffer the render endpoint was opened with.</param>
/// <param name="EstimatedAdditionalLatencyMilliseconds">
/// The delay the toolkit itself adds, over and above what the endpoints already had.
/// </param>
public sealed record AudioStreamMetrics(
    long Underruns,
    long Overruns,
    int CaptureBufferMilliseconds,
    int RenderBufferMilliseconds,
    double EstimatedAdditionalLatencyMilliseconds,
    AudioProcessorMetrics Processor)
{
    /// <summary>What an idle session reports. Nothing is running, so nothing is being measured.</summary>
    public static AudioStreamMetrics Idle { get; } = new(
        Underruns: 0,
        Overruns: 0,
        CaptureBufferMilliseconds: 0,
        RenderBufferMilliseconds: 0,
        EstimatedAdditionalLatencyMilliseconds: 0.0,
        Processor: default);
}
