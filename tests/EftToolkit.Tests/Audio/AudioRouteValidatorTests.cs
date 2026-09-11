using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Routing;
using EftToolkit.Core.Configuration;

namespace EftToolkit.Tests.Audio;

public class AudioRouteValidatorTests
{
    private const string VirtualRenderId = "virtual-render";
    private const string VirtualCaptureId = "virtual-capture";
    private const string PhysicalRenderId = "headphones";

    private static AudioEndpointDescriptor Render(
        string id,
        string friendlyName = "Speakers",
        bool isActive = true,
        int channels = 2,
        int sampleRate = 48_000) =>
        new(id, friendlyName, AudioDataFlow.Render, isActive, channels, sampleRate);

    private static AudioEndpointDescriptor Capture(
        string id,
        string friendlyName = "Cable Output",
        bool isActive = true,
        int channels = 2,
        int sampleRate = 48_000) =>
        new(id, friendlyName, AudioDataFlow.Capture, isActive, channels, sampleRate);

    private static AudioProfileOptions Profile(
        string? virtualRender = VirtualRenderId,
        string? virtualCapture = VirtualCaptureId,
        string? physicalRender = PhysicalRenderId,
        string executableName = "EscapeFromTarkov") =>
        new("eft", "Escape from Tarkov", executableName, virtualRender, virtualCapture, physicalRender);

    /// <summary>The three endpoints a working route needs, with any one of them overridable.</summary>
    private static IReadOnlyList<AudioEndpointDescriptor> Endpoints(
        AudioEndpointDescriptor? virtualRender = null,
        AudioEndpointDescriptor? virtualCapture = null,
        AudioEndpointDescriptor? physicalRender = null) =>
        [
            virtualRender ?? Render(VirtualRenderId, "CABLE Input"),
            virtualCapture ?? Capture(VirtualCaptureId),
            physicalRender ?? Render(PhysicalRenderId, "Headphones"),
        ];

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public void Validate_accepts_a_virtual_render_capture_pair_and_a_physical_render()
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(Profile(), Endpoints());

        Assert.True(result.IsValid);
        Assert.Equal(VirtualRenderId, result.Route!.VirtualRenderEndpointId);
        Assert.Equal(VirtualCaptureId, result.Route.VirtualCaptureEndpointId);
        Assert.Equal(PhysicalRenderId, result.Route.PhysicalRenderEndpointId);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void An_accepted_route_looks_up_the_configured_executable_by_its_bare_name()
    {
        // The user picks a program, not a path, and the process monitor matches on the image name
        // without its extension. Both forms have to end up as the same value.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(executableName: "EscapeFromTarkov.exe"),
            Endpoints());

        Assert.True(result.IsValid);
        Assert.Equal("EscapeFromTarkov", result.Route!.ExecutableName);
    }

    [Fact]
    public void An_accepted_route_carries_the_endpoint_id_rather_than_the_configured_spelling()
    {
        // Windows reports endpoint IDs with inconsistent casing depending on which API produced
        // them, so a stored profile may spell one differently from the device. Matching is
        // case-insensitive, but the route is built from what the machine actually reported.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(virtualRender: VirtualRenderId.ToUpperInvariant()),
            Endpoints());

        Assert.True(result.IsValid);
        Assert.Equal(VirtualRenderId, result.Route!.VirtualRenderEndpointId);
    }

    [Fact]
    public void An_endpoint_id_that_differs_only_in_case_still_finds_its_device()
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(physicalRender: "HEADPHONES"),
            Endpoints());

        Assert.True(result.IsValid);
        Assert.Equal(PhysicalRenderId, result.Route!.PhysicalRenderEndpointId);
    }

    // ---------------------------------------------------------------- unusable configuration

    [Theory]
    [InlineData(null, VirtualCaptureId, PhysicalRenderId)]
    [InlineData(VirtualRenderId, null, PhysicalRenderId)]
    [InlineData(VirtualRenderId, VirtualCaptureId, null)]
    [InlineData("", VirtualCaptureId, PhysicalRenderId)]
    [InlineData("   ", VirtualCaptureId, PhysicalRenderId)]
    public void A_route_missing_any_of_its_three_endpoints_is_rejected(
        string? virtualRender,
        string? virtualCapture,
        string? physicalRender)
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(virtualRender, virtualCapture, physicalRender),
            Endpoints());

        Assert.False(result.IsValid);
        Assert.Null(result.Route);
        Assert.Equal(AudioRouteErrorCodes.MissingEndpointId, result.ErrorCode);
    }

    [Fact]
    public void A_route_pointing_at_an_endpoint_that_is_not_present_is_rejected()
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(virtualRender: "a-cable-that-was-uninstalled"),
            Endpoints());

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.EndpointNotFound, result.ErrorCode);
    }

    [Fact]
    public void A_route_pointing_at_an_endpoint_that_is_not_active_is_rejected()
    {
        // A device that is present but disabled or unplugged still enumerates; opening a stream on
        // it is what fails. The route has to be refused here rather than at the first audio callback.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(physicalRender: Render(PhysicalRenderId, "Headphones", isActive: false)));

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.EndpointNotActive, result.ErrorCode);
    }

    [Fact]
    public void A_virtual_render_endpoint_that_is_really_a_capture_endpoint_is_rejected()
    {
        // Swapping the two halves of the cable is the single most likely misconfiguration: it looks
        // plausible in a device list and produces feedback rather than audio.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(virtualRender: Capture("virtual-render")));

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.WrongDataFlow, result.ErrorCode);
    }

    [Fact]
    public void A_virtual_capture_endpoint_that_is_really_a_render_endpoint_is_rejected()
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(virtualCapture: Render("virtual-capture")));

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.WrongDataFlow, result.ErrorCode);
    }

    [Fact]
    public void A_physical_render_endpoint_that_is_really_a_capture_endpoint_is_rejected()
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(physicalRender: Capture(PhysicalRenderId)));

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.WrongDataFlow, result.ErrorCode);
    }

    [Fact]
    public void A_route_whose_virtual_render_is_the_physical_render_is_rejected()
    {
        // Sending the boosted stream back into the cable it came out of is a loop, not a route.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(physicalRender: VirtualRenderId),
            Endpoints());

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.SameRenderEndpoint, result.ErrorCode);
    }

    [Fact]
    public void A_non_stereo_endpoint_is_rejected()
    {
        // The limiter links two channels and the panel reports a single stereo gain reduction. A
        // mono or multichannel endpoint would need a different signal path, which is not this one.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(physicalRender: Render(PhysicalRenderId, "Headphones", channels: 6)));

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.UnsupportedChannelCount, result.ErrorCode);
    }

    [Theory]
    [InlineData(22_050)]
    [InlineData(32_000)]
    [InlineData(88_200)]
    [InlineData(192_000)]
    public void An_unsupported_sample_rate_is_rejected(int sampleRate)
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(virtualCapture: Capture(VirtualCaptureId, sampleRate: sampleRate)));

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.UnsupportedSampleRate, result.ErrorCode);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    public void The_sample_rates_the_first_release_supports_are_accepted(int sampleRate)
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(),
            Endpoints(
                virtualRender: Render(VirtualRenderId, "CABLE Input", sampleRate: sampleRate),
                virtualCapture: Capture(VirtualCaptureId, sampleRate: sampleRate),
                physicalRender: Render(PhysicalRenderId, "Headphones", sampleRate: sampleRate)));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"sub\EscapeFromTarkov")]
    [InlineData("sub/EscapeFromTarkov")]
    [InlineData(@"C:\Games\EscapeFromTarkov.exe")]
    public void An_executable_name_that_is_not_a_bare_file_name_is_rejected(string executableName)
    {
        // The name is what the process monitor matches against the running image, so a directory
        // here names something that can never match and would fail silently at the audio callback.
        AudioRouteValidation result = AudioRouteValidator.Validate(
            Profile(executableName: executableName),
            Endpoints());

        Assert.False(result.IsValid);
        Assert.Null(result.Route);
        Assert.Equal(AudioRouteErrorCodes.InvalidExecutableName, result.ErrorCode);
    }

    [Fact]
    public void Validation_tolerates_an_endpoint_list_that_contains_other_devices()
    {
        IReadOnlyList<AudioEndpointDescriptor> endpoints =
        [
            Render("unrelated-speakers", "Desk speakers"),
            Capture("unrelated-microphone", "Microphone"),
            Render(VirtualRenderId, "CABLE Input"),
            Capture(VirtualCaptureId),
            Render(PhysicalRenderId, "Headphones"),
        ];

        AudioRouteValidation result = AudioRouteValidator.Validate(Profile(), endpoints);

        Assert.True(result.IsValid);
        Assert.Equal(PhysicalRenderId, result.Route!.PhysicalRenderEndpointId);
    }

    [Fact]
    public void An_empty_endpoint_list_is_refused_rather_than_throwing()
    {
        AudioRouteValidation result = AudioRouteValidator.Validate(Profile(), []);

        Assert.False(result.IsValid);
        Assert.Equal(AudioRouteErrorCodes.EndpointNotFound, result.ErrorCode);
    }
}
