using EftToolkit.Audio.Dsp;
using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Audio;

/// <summary>
/// Exercises the real audio path against real endpoints. Excluded from the default run because it
/// needs a virtual cable installed and a physical device to play through, and because it holds the
/// output device for five seconds. Run it deliberately with
/// <c>dotnet test --filter "Category=Hardware"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The category filter alone is not the whole gate: a CI job or a developer can select the category
/// without meaning to touch hardware, so <c>EFT_TOOLKIT_HARDWARE_TESTS=1</c> must also be set.
/// Without it these tests report as skipped rather than passing without having checked anything.
/// </para>
/// <para>
/// The endpoints are named by ID through the environment rather than discovered. Finding a device
/// by position in the enumeration would pass on this machine and pick the wrong device on the next.
/// </para>
/// </remarks>
[Trait("Category", "Hardware")]
public class NAudioSharedStreamSessionHardwareTests
{
    private const string EnableVariable = "EFT_TOOLKIT_HARDWARE_TESTS";
    private const string VirtualCaptureVariable = "EFT_TOOLKIT_VIRTUAL_CAPTURE_ID";
    private const string PhysicalRenderVariable = "EFT_TOOLKIT_PHYSICAL_RENDER_ID";

    /// <summary>Long enough for both streams to be well past initialisation and running.</summary>
    private static readonly TimeSpan StreamDuration = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Shared_streaming_runs_for_five_seconds_without_a_fault()
    {
        RequireHardware();

        string virtualCaptureId = RequireEndpointId(VirtualCaptureVariable);
        string physicalRenderId = RequireEndpointId(PhysicalRenderVariable);

        RecordingLogger logger = new();
        await using NAudioSharedStreamSession session = new(new NAudioClientFactory(logger), logger);

        List<Exception> faults = [];
        session.Faulted += (_, exception) => faults.Add(exception);

        // The virtual render side belongs to the game's output, not to this stream; only the capture
        // and physical render IDs are used here.
        AudioRoute route = new(
            "hardware-smoke-virtual-render",
            virtualCaptureId,
            physicalRenderId,
            "EftToolkitHardwareTests");

        await session.StartAsync(route, Limiter(), CancellationToken.None);
        Assert.True(session.IsRunning, "the session did not start");

        await Task.Delay(StreamDuration);

        AudioStreamMetrics metrics = session.Metrics;

        Assert.Empty(faults);
        Assert.True(session.IsRunning, "the session stopped on its own");
        Assert.True(metrics.CaptureBufferMilliseconds > 0, "the capture buffer was never reported");
        Assert.True(metrics.RenderBufferMilliseconds > 0, "the render buffer was never reported");
        Assert.True(
            metrics.EstimatedAdditionalLatencyMilliseconds < 250.0,
            $"the toolkit is adding {metrics.EstimatedAdditionalLatencyMilliseconds} ms");

        // Nothing is playing into the cable unless the operator arranged it, so underruns are
        // expected. An overrun would be the toolkit failing to keep up, which is not.
        Assert.Equal(0, metrics.Overruns);

        await session.StopAsync(CancellationToken.None);

        Assert.False(session.IsRunning);
        Assert.Empty(faults);
    }

    [Fact]
    public async Task A_second_start_after_a_stop_reopens_the_same_endpoints()
    {
        RequireHardware();

        string virtualCaptureId = RequireEndpointId(VirtualCaptureVariable);
        string physicalRenderId = RequireEndpointId(PhysicalRenderVariable);

        RecordingLogger logger = new();
        await using NAudioSharedStreamSession session = new(new NAudioClientFactory(logger), logger);

        AudioRoute route = new(
            "hardware-smoke-virtual-render",
            virtualCaptureId,
            physicalRenderId,
            "EftToolkitHardwareTests");

        await session.StartAsync(route, Limiter(), CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        // Reopening is what a device change does, and an endpoint that was not fully released would
        // refuse to open the second time.
        await session.StartAsync(route, Limiter(), CancellationToken.None);

        Assert.True(session.IsRunning);

        await session.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_endpoint_id_that_matches_nothing_stops_the_route_rather_than_substituting_one()
    {
        // No hardware is needed for this one, so it runs everywhere: the factory has to refuse a
        // route it cannot resolve exactly, because falling back to a default device would silently
        // play the game's audio somewhere the user did not choose.
        RecordingLogger logger = new();
        await using NAudioSharedStreamSession session = new(new NAudioClientFactory(logger), logger);

        AudioRoute route = new(
            "hardware-smoke-virtual-render",
            "no-such-capture-endpoint",
            "no-such-render-endpoint",
            "EftToolkitHardwareTests");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAsync(route, Limiter(), CancellationToken.None));

        Assert.Contains("no-such-capture-endpoint", exception.Message, StringComparison.Ordinal);
        Assert.False(session.IsRunning);
    }

    /// <summary>Skips the calling test unless hardware testing has been explicitly enabled.</summary>
    private static void RequireHardware() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(EnableVariable) == "1",
            $"Set {EnableVariable}=1 to run tests that touch real audio hardware.");

    private static string RequireEndpointId(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(value),
            $"Set {variable} to the endpoint ID of the device to stream through.");

        return value!;
    }

    private static AudioLimiterOptions Limiter() => new(
        InputGainDb: 0.0,
        ThresholdDbFs: -3.0,
        Ratio: 4.0,
        KneeDb: 6.0,
        LookAheadMs: 1.5,
        AttackMs: 5.0,
        ReleaseMs: 120.0,
        CeilingDbFs: -0.5);
}
