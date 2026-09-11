using EftToolkit.Audio;
using EftToolkit.Core.Display;
using EftToolkit.Core.Modules;
using EftToolkit.Display.Gamma;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Integration;

/// <summary>
/// The two halves share a process, a configuration file, a coordinator and a log, and nothing else.
/// These tests are about that: a fault on one side has to leave the other one working, and neither
/// half may need the other to be usable.
/// </summary>
/// <remarks>
/// The faults are the ones the manual checklist raises by hand — the virtual cable is not installed,
/// a driver refuses a ramp — and the assertion is always the same shape: what broke says so, and
/// what did not break keeps working.
/// </remarks>
public class ModuleIsolationTests
{
    [Fact]
    public async Task Audio_with_no_virtual_device_configured_leaves_the_displays_working()
    {
        // Audio starts off — as it does on a machine where the user has not configured it — and the
        // cable is then absent: the catalog reports no endpoint, let alone the three the profile
        // names.
        await using ToolkitHarness harness = await ToolkitHarness.StartAsync(audioEnabled: false);
        harness.Catalog.Endpoints = [];

        await harness.Coordinator.SetAudioEnabledAsync(true, CancellationToken.None);
        Assert.Equal(ModuleState.Faulted, harness.Audio.Status.State);
        Assert.Equal(AudioModuleErrorCodes.MissingVirtualRender, harness.Audio.Status.ErrorCode);
        Assert.Empty(harness.Sessions);

        // The display half was never consulted by any of that, and it still answers a preset.
        await harness.PressPresetAsync(DisplayPresetKind.High, ToolkitHarness.FirstDisplayId);

        Assert.Equal(ModuleState.Active, harness.Display.Status.State);

        // And what it wrote is the high preset composed on the ramp that was already there, which is
        // what a display half that is fully working produces.
        GammaRamp expected = GammaRampComposer.Compose(
            ToolkitHarness.FirstOriginal,
            harness.Options.Display.High);

        Assert.Equal(
            GammaRampFingerprint.Compute(expected),
            GammaRampFingerprint.Compute(harness.Gateway.CurrentRamp(ToolkitHarness.FirstDisplayId)));
    }

    [Fact]
    public async Task A_display_write_that_the_driver_refuses_leaves_the_audio_running()
    {
        await using ToolkitHarness harness = await ToolkitHarness.StartAsync();

        // The audio side is up and forwarding before anything goes wrong on the display side.
        await harness.Coordinator.SetAudioEnabledAsync(true, CancellationToken.None);
        await AsyncWait.UntilAsync(
            () => harness.Audio.IsRouteOpen,
            "the audio stream was opened");

        // The driver rejects the write with an error code rather than throwing, which is the way a
        // gamma-ramp refusal actually arrives.
        harness.Gateway.FailOnWrite.Add(ToolkitHarness.FirstDisplayId);

        harness.Hotkeys.Press(DisplayPresetKind.Low);

        // The refusal arrives on the display's own row rather than as a throw. Waiting on the row is
        // what makes this deterministic: the press returns as soon as it is delivered, and the write
        // it starts happens on the module's worker.
        await AsyncWait.UntilAsync(
            () => harness.Display.Displays.FirstOrDefault()?.LastResult is { Succeeded: false },
            "the refused write was reported on the display's own row");

        // The refusal is per-display and visible rather than fatal: the module says it is degraded
        // rather than claiming to be enhancing a monitor it is not, and the audio half never noticed.
        Assert.Equal(ModuleState.Degraded, harness.Display.Status.State);
        Assert.True(harness.Audio.IsRouteOpen);
        Assert.Single(harness.Sessions);
    }

    [Fact]
    public async Task One_refusing_display_does_not_stop_the_other_one_from_being_enhanced()
    {
        await using ToolkitHarness harness = await ToolkitHarness.StartAsync(
            selectedDisplayIds: [ToolkitHarness.FirstDisplayId, ToolkitHarness.SecondDisplayId]);

        harness.Gateway.FailOnWrite.Add(ToolkitHarness.FirstDisplayId);

        await harness.PressPresetAsync(DisplayPresetKind.Medium, ToolkitHarness.SecondDisplayId);

        // One monitor the driver will not write to is one monitor the user cannot enhance, not a
        // reason to leave the other one at its original ramp.
        Assert.Equal(1, harness.Gateway.WriteCountByDisplay[ToolkitHarness.SecondDisplayId]);
        Assert.False(harness.Gateway.WriteCountByDisplay.ContainsKey(ToolkitHarness.FirstDisplayId));
    }

    [Fact]
    public async Task A_display_that_is_gone_leaves_the_audio_alone_and_vice_versa()
    {
        await using ToolkitHarness harness = await ToolkitHarness.StartAsync();

        // Both halves on, then the display the user selected is unplugged. The module reports the
        // row as unconnected, which is a display-side fact the audio side has no access to.
        await harness.Coordinator.SetAudioEnabledAsync(true, CancellationToken.None);
        harness.Gateway.Displays.Clear();

        await harness.Display.RefreshAndReapplyAsync(CancellationToken.None);

        Assert.True(harness.Audio.IsRouteOpen);
        Assert.Equal(ModuleState.Active, harness.Audio.Status.State);
        Assert.Equal("display.selection.empty", harness.Display.Status.ErrorCode);
    }
}
