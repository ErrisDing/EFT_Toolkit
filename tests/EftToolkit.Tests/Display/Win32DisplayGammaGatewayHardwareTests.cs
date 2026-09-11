using EftToolkit.Display.Devices;
using EftToolkit.Display.Gamma;

namespace EftToolkit.Tests.Display;

/// <summary>
/// Exercises the real display driver. Excluded from the default run because it needs a machine with
/// displays attached and because a gamma write is visible to whoever is looking at the screen. Run it
/// deliberately with <c>dotnet test --filter "Category=Hardware"</c>.
/// <para>
/// This test never writes a gamma ramp. Writing is covered by the manual checklist, where a person can
/// confirm what they see on the panel.
/// </para>
/// </summary>
[Trait("Category", "Hardware")]
public class Win32DisplayGammaGatewayHardwareTests
{
    [Fact]
    public async Task Enumerate_finds_at_least_one_display()
    {
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
