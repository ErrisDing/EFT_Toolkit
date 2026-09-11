using EftToolkit.Display.Devices;
using EftToolkit.Display.Gamma;

namespace EftToolkit.Tests.Display;

/// <summary>
/// Exercises the real display driver. Excluded from the default run because it needs a machine with
/// displays attached and because a gamma write is visible to whoever is looking at the screen. Run it
/// deliberately with <c>dotnet test --filter "Category=Hardware"</c>.
/// <para>
/// The category filter alone is not the whole gate: a CI job or a developer can select the category
/// without meaning to touch hardware, so the environment variable
/// <c>EFT_TOOLKIT_HARDWARE_TESTS=1</c> must also be set. Without it these tests report as skipped
/// rather than passing without having checked anything.
/// </para>
/// <para>
/// This test never writes a gamma ramp. Writing is covered by the manual checklist, where a person can
/// confirm what they see on the panel.
/// </para>
/// </summary>
[Trait("Category", "Hardware")]
public class Win32DisplayGammaGatewayHardwareTests
{
    private const string EnableVariable = "EFT_TOOLKIT_HARDWARE_TESTS";

    /// <summary>Skips the calling test unless hardware testing has been explicitly enabled.</summary>
    private static void RequireHardware()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(EnableVariable) == "1",
            $"Set {EnableVariable}=1 to run tests that touch real display hardware.");
    }

    [Fact]
    public async Task Enumerate_finds_at_least_one_display()
    {
        RequireHardware();

        Win32DisplayGammaGateway gateway = new();

        IReadOnlyList<DisplayDescriptor> displays = await gateway.EnumerateAsync(CancellationToken.None);

        Assert.NotEmpty(displays);
        Assert.All(displays, display =>
        {
            Assert.False(string.IsNullOrWhiteSpace(display.StableId));
            Assert.False(string.IsNullOrWhiteSpace(display.FriendlyName));
            Assert.True(display.IsConnected);
        });
    }

    [Fact]
    public async Task Enumerate_reports_stable_ids_that_do_not_change_between_calls()
    {
        RequireHardware();

        Win32DisplayGammaGateway gateway = new();

        IReadOnlyList<DisplayDescriptor> first = await gateway.EnumerateAsync(CancellationToken.None);
        IReadOnlyList<DisplayDescriptor> second = await gateway.EnumerateAsync(CancellationToken.None);

        Assert.Equal(
            first.Select(display => display.StableId).OrderBy(id => id, StringComparer.Ordinal),
            second.Select(display => display.StableId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_display_that_supports_gamma_reports_a_well_formed_ramp()
    {
        RequireHardware();

        Win32DisplayGammaGateway gateway = new();

        IReadOnlyList<DisplayDescriptor> displays = await gateway.EnumerateAsync(CancellationToken.None);

        foreach (DisplayDescriptor display in displays)
        {
            if (!display.SupportsGammaRamp)
            {
                continue;
            }

            GammaRamp ramp = await gateway.ReadAsync(display, CancellationToken.None);

            Assert.Equal(GammaRamp.ChannelLength, ramp.Red.Count);
            Assert.Equal(GammaRamp.ChannelLength, ramp.Green.Count);
            Assert.Equal(GammaRamp.ChannelLength, ramp.Blue.Count);
        }
    }

    [Fact]
    public async Task A_display_that_does_not_support_gamma_cannot_be_read()
    {
        RequireHardware();

        Win32DisplayGammaGateway gateway = new();

        IReadOnlyList<DisplayDescriptor> displays = await gateway.EnumerateAsync(CancellationToken.None);
        DisplayDescriptor? unsupported = displays.FirstOrDefault(display => !display.SupportsGammaRamp);

        if (unsupported is null)
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.ReadAsync(unsupported, CancellationToken.None));
    }
}
