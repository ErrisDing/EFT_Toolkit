using EftToolkit.App.Lifecycle;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Core.Modules;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Recovery;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Integration;

/// <summary>
/// What the toolkit leaves behind, and what it does with what a previous run left behind.
/// </summary>
/// <remarks>
/// The two are the same question asked from opposite ends. A shutdown that restores every ramp it
/// wrote is why an ungraceful kill is recoverable at all: the recovery file is the only thing that
/// knows a ramp is still applied, so it has to keep exactly the entries the toolkit still owes the
/// user a restore for, and no others.
/// </remarks>
public class ShutdownRecoveryTests
{
    [Fact]
    public async Task Exiting_restores_the_original_ramp_before_releasing_the_shortcuts()
    {
        await using ToolkitHarness harness = await ToolkitHarness.StartAsync();

        await harness.PressPresetAsync(DisplayPresetKind.High, ToolkitHarness.FirstDisplayId);

        harness.Journal.Clear();

        ShutdownResult result = await harness.Coordinator.ShutdownAsync(CancellationToken.None);

        Assert.True(result.Succeeded);

        // The shortcuts are only released after the ramps are back, and the remainder of the
        // teardown only after that. A display left enhanced by a process that has already given up
        // the keys that would undo it is the failure this order exists to prevent.
        Assert.Equal(
            [
                $"display.write.{ToolkitHarness.FirstDisplayId}",
                "hotkeys.unregister",
                "platformEvents.stop",
            ],
            harness.Journal);

        // And the display really is back to what was on it, not merely reported as such.
        Assert.Equal(
            GammaRampFingerprint.Compute(ToolkitHarness.FirstOriginal),
            GammaRampFingerprint.Compute(harness.Gateway.CurrentRamp(ToolkitHarness.FirstDisplayId)));

        // Nothing is owed a restore any more, so the next launch finds nothing to undo.
        Assert.Null(await RecoverySnapshotAsync(harness));
    }

    [Fact]
    public async Task Hiding_the_window_does_not_tear_anything_down()
    {
        await using ToolkitHarness harness = await ToolkitHarness.StartAsync();

        await harness.PressPresetAsync(DisplayPresetKind.Medium, ToolkitHarness.FirstDisplayId);

        int writesBefore = harness.Gateway.WriteCountByDisplay[ToolkitHarness.FirstDisplayId];

        // Hiding is the close policy's answer, and the policy is the whole of it: nothing in the
        // exit path is reached, so the shortcuts still work and the preset is still in force.
        Assert.Equal(WindowCloseAction.Hide, WindowClosePolicy.Decide(isShuttingDown: false));
        Assert.True(harness.Coordinator.IsDisplayEnabled);
        Assert.Equal(DisplayPresetKind.Medium, harness.Display.CurrentPreset);

        // Reading the same state the panel's timer reads changes nothing either.
        _ = harness.Display.Displays;

        Assert.Equal(writesBefore, harness.Gateway.WriteCountByDisplay[ToolkitHarness.FirstDisplayId]);
        Assert.DoesNotContain("hotkeys.unregister", harness.Journal);

        // The window only closes once the exit path has said the application is going away.
        Assert.Equal(WindowCloseAction.Close, WindowClosePolicy.Decide(isShuttingDown: true));
    }

    [Fact]
    public async Task A_run_that_was_killed_before_it_restored_puts_the_ramp_back_on_the_next_launch()
    {
        // The first run writes the preset and is then killed, so nothing is restored and the
        // recovery file is exactly what a crash leaves behind.
        string directory = await KilledRunAsync();

        try
        {
            // The second run finds that file, on a display that still holds what the killed run
            // wrote.
            await using ToolkitHarness harness = await ResumeAsync(
                directory,
                first: EnhancedOnFirstDisplay,
                second: ToolkitHarness.SecondOriginal);

            Assert.Equal(
                GammaRampFingerprint.Compute(ToolkitHarness.FirstOriginal),
                GammaRampFingerprint.Compute(harness.Gateway.CurrentRamp(ToolkitHarness.FirstDisplayId)));
        }
        finally
        {
            ToolkitHarness.Release(directory);
        }
    }

    [Fact]
    public async Task A_ramp_that_came_from_somewhere_else_survives_a_recovery()
    {
        // The same leftover file, but the display no longer holds what the killed run wrote:
        // something else changed it in the meantime.
        string directory = await KilledRunAsync();

        try
        {
            await using ToolkitHarness harness = await ResumeAsync(
                directory,
                first: SomebodyElsesRamp,
                second: ToolkitHarness.SecondOriginal);

            // The entry is a record of a write this toolkit made. Putting the original back over a
            // ramp the toolkit never wrote would undo a change the user made in another tool.
            Assert.Equal(
                GammaRampFingerprint.Compute(SomebodyElsesRamp),
                GammaRampFingerprint.Compute(harness.Gateway.CurrentRamp(ToolkitHarness.FirstDisplayId)));
        }
        finally
        {
            ToolkitHarness.Release(directory);
        }
    }

    [Fact]
    public async Task Recovery_restores_the_display_it_owes_and_leaves_the_other_one_alone()
    {
        string directory = await KilledRunAsync(selectBothDisplays: true);

        try
        {
            // Only the first display still holds what the toolkit wrote; the second was changed by
            // someone else after the kill. Recovery is per display rather than all or nothing,
            // because one crash can leave the two in different states.
            await using ToolkitHarness harness = await ResumeAsync(
                directory,
                first: EnhancedOnFirstDisplay,
                second: SomebodyElsesRamp);

            Assert.Equal(
                GammaRampFingerprint.Compute(ToolkitHarness.FirstOriginal),
                GammaRampFingerprint.Compute(harness.Gateway.CurrentRamp(ToolkitHarness.FirstDisplayId)));

            Assert.Equal(
                GammaRampFingerprint.Compute(SomebodyElsesRamp),
                GammaRampFingerprint.Compute(harness.Gateway.CurrentRamp(ToolkitHarness.SecondDisplayId)));
        }
        finally
        {
            ToolkitHarness.Release(directory);
        }
    }

    /// <summary>A ramp this toolkit never wrote: the user's own calibration, or another tool's.</summary>
    private static GammaRamp SomebodyElsesRamp { get; } = BuildRamp(300);

    /// <summary>What the high preset writes over the ramp the harness's first display starts with.</summary>
    private static GammaRamp EnhancedOnFirstDisplay { get; } = GammaRampComposer.Compose(
        ToolkitHarness.FirstOriginal,
        ToolkitOptions.CreateDefault().Display.High);

    /// <summary>
    /// Runs the toolkit, applies the high preset, and then disappears without a shutdown — which is
    /// what End task and a power cut both look like from here.
    /// </summary>
    /// <returns>The directory the run left its recovery file in.</returns>
    private static async Task<string> KilledRunAsync(bool selectBothDisplays = false)
    {
        ToolkitHarness harness = ToolkitHarness.Create(
            selectedDisplayIds: selectBothDisplays
                ? [ToolkitHarness.FirstDisplayId, ToolkitHarness.SecondDisplayId]
                : [ToolkitHarness.FirstDisplayId]);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.PressPresetAsync(DisplayPresetKind.High, ToolkitHarness.FirstDisplayId);

        // Deliberately not disposed: disposing shuts down, and a shutdown is the very thing this run
        // stands in for the absence of.
        return harness.Directory;
    }

    /// <summary>
    /// Starts the toolkit over a left-over recovery file, on a machine whose displays hold the ramps
    /// the caller says they hold.
    /// </summary>
    private static async Task<ToolkitHarness> ResumeAsync(string directory, GammaRamp first, GammaRamp second)
    {
        ToolkitHarness harness = ToolkitHarness.CreateOver(
            directory,
            [ToolkitHarness.FirstDisplayId],
            options: null,
            displays:
            [
                (ToolkitHarness.FirstDisplayId, first),
                (ToolkitHarness.SecondDisplayId, second),
            ]);

        await harness.Coordinator.StartAsync(CancellationToken.None);

        return harness;
    }

    private static async Task<DisplayRecoverySnapshot?> RecoverySnapshotAsync(ToolkitHarness harness)
    {
        JsonDisplayRecoveryStore store = new(harness.Directory, harness.Clock);

        return await store.LoadAsync(CancellationToken.None);
    }

    private static GammaRamp BuildRamp(ushort step)
    {
        ushort[] channel = [.. Enumerable.Range(0, GammaRamp.ChannelLength).Select(index => (ushort)(index * step))];

        return new GammaRamp(channel, channel, channel);
    }
}
