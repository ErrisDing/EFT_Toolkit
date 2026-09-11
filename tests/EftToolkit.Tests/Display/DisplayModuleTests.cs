using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;
using EftToolkit.Core.Modules;
using EftToolkit.Display;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Recovery;

namespace EftToolkit.Tests.Display;

public class DisplayModuleTests : IDisposable
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 11, 10, 30, 0, TimeSpan.Zero);

    private static readonly DisplayOptions DefaultOptions = new(
        Enabled: true,
        SelectedDisplayIds: ["display-1"],
        Low: new DisplayPresetOptions(1.15, 0.00, 1.00),
        Medium: new DisplayPresetOptions(1.35, 0.01, 1.00),
        High: new DisplayPresetOptions(1.55, 0.02, 1.00));

    private readonly string _directory;
    private readonly JsonDisplayRecoveryStore _store;

    public DisplayModuleTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "eft-toolkit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new JsonDisplayRecoveryStore(_directory, TimeProvider.System);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    // ---------------------------------------------------------------- enable

    [Fact]
    public async Task Enable_captures_the_selected_display_and_writes_recovery_without_touching_it()
    {
        // The display is untouched, so what the module captures as the original is what is on it.
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        // Enabling arms the feature: it records what is on the display so a later preset can be
        // undone, but it must not change the picture by itself.
        Assert.Empty(gateway.WriteLog);

        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        DisplayRecoveryEntry entry = Assert.Single(snapshot!.Displays);
        Assert.Equal("display-1", entry.StableId);
        Assert.True(entry.TryReadOriginalRamp(out GammaRamp? captured));
        Assert.Equal(Original, captured);
    }

    [Fact]
    public async Task Enable_selects_only_the_displays_the_user_chose()
    {
        FakeDisplayGammaGateway gateway = new();
        gateway.AddDisplay("display-1", "One", Original);
        gateway.AddDisplay("display-2", "Two", Second);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        Assert.True(module.Displays.Single(row => row.Display.StableId == "display-1").Selected);
        Assert.False(module.Displays.Single(row => row.Display.StableId == "display-2").Selected);

        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.Equal("display-1", Assert.Single(snapshot!.Displays).StableId);
    }

    [Fact]
    public async Task Enable_leaves_out_a_display_whose_current_ramp_cannot_be_read()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        gateway.FailOnRead.Add("display-1");

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        // Without a readable original there is nothing to restore, so enhancing the display would
        // be a one-way change.
        Assert.False(Assert.Single(module.Displays).Selected);
        Assert.Null(await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enable_reports_a_selected_display_that_is_not_connected()
    {
        FakeDisplayGammaGateway gateway = new();

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        DisplayStatus row = Assert.Single(module.Displays);
        Assert.Equal("display-1", row.Display.StableId);
        Assert.True(row.Selected);
        Assert.False(row.Display.IsConnected);

        Assert.Equal(ModuleState.Bypass, module.Status.State);
        Assert.Equal("display.selection.empty", module.Status.ErrorCode);
    }

    [Fact]
    public async Task Enumerating_reports_the_monitors_without_switching_anything_on()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway, "display-1", "unplugged");

        // The panel has to be able to show a monitor for the user to select before the module is on:
        // asking the user to switch the feature on in order to choose what it acts on is backwards.
        IReadOnlyList<DisplayStatus> rows = await module.EnumerateAsync(CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Selected);

        // The monitor that is not there is still part of the selection: it was chosen, it is simply
        // not plugged in, and the row says so rather than quietly dropping out of the list.
        Assert.True(rows[1].Selected);
        Assert.False(rows[1].Display.IsConnected);
        Assert.Contains("not connected", rows[1].Message, StringComparison.Ordinal);

        // Reading is all it did: nothing is on, no ramp was touched, and there is nothing to recover.
        Assert.Equal(ModuleState.Disabled, module.Status.State);
        Assert.Empty(gateway.WriteLog);
        Assert.Null(await _store.LoadAsync(CancellationToken.None));

        // The report is not a capture, so enabling afterwards still reads what is on the display.
        await module.EnableAsync(CancellationToken.None);

        DisplayRecoveryEntry entry = Assert.Single((await _store.LoadAsync(CancellationToken.None))!.Displays);
        Assert.Equal("display-1", entry.StableId);
        Assert.True(entry.TryReadOriginalRamp(out GammaRamp? captured));
        Assert.Equal(Original, captured);
    }

    // ---------------------------------------------------------------- presets

    [Fact]
    public async Task Applying_a_preset_composes_it_against_the_captured_original()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 1, "the high preset was never written");

        GammaRamp expected = GammaRampComposer.Compose(Original, DefaultOptions.High);
        Assert.Equal(expected, gateway.LastWrittenRamp);
        Assert.Equal(expected, gateway.CurrentRamp("display-1"));
        Assert.Equal(DisplayPresetKind.High, module.CurrentPreset);
    }

    [Fact]
    public async Task F2_writes_the_exact_original_ramp_and_leaves_the_module_enabled()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 1, "the high preset was never written");

        await module.ApplyPresetAsync(DisplayPresetKind.Original, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 2, "the original was never restored");

        Assert.Equal(Original, gateway.LastWrittenRamp);
        Assert.Equal(DisplayPresetKind.Original, module.CurrentPreset);
        Assert.Equal(ModuleState.Active, module.Status.State);
    }

    [Fact]
    public async Task One_failed_display_does_not_block_another()
    {
        FakeDisplayGammaGateway gateway = new();
        gateway.AddDisplay("display-1", "One", Original);
        gateway.AddDisplay("display-2", "Two", Second);
        gateway.FailOnWrite.Add("display-1");

        await using DisplayModule module = CreateModule(gateway, "display-1", "display-2");
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(
            () => gateway.WriteCountByDisplay.GetValueOrDefault("display-2") >= 1,
            "the second display was never written");

        Assert.Equal(0, gateway.WriteCountByDisplay.GetValueOrDefault("display-1"));
        Assert.False(module.Displays.Single(row => row.Display.StableId == "display-1").LastResult!.Succeeded);
        Assert.True(module.Displays.Single(row => row.Display.StableId == "display-2").LastResult!.Succeeded);
    }

    [Fact]
    public async Task A_display_that_throws_while_being_written_does_not_stop_the_others()
    {
        FakeDisplayGammaGateway gateway = new();
        gateway.AddDisplay("display-1", "One", Original);
        gateway.AddDisplay("display-2", "Two", Second);
        gateway.ThrowOnWrite.Add("display-1");

        await using DisplayModule module = CreateModule(gateway, "display-1", "display-2");
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(
            () => gateway.WriteCountByDisplay.GetValueOrDefault("display-2") >= 1,
            "the second display was never written");

        Assert.Equal(ModuleState.Active, module.Status.State);
    }

    [Fact]
    public async Task Queued_presets_coalesce_to_the_last_request()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        gateway.WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        // Hold the worker inside the first write, then queue two more changes behind it. Only the
        // last one is worth showing to the user, so the middle one must never reach the display.
        await module.ApplyPresetAsync(DisplayPresetKind.Low, CancellationToken.None);
        await gateway.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await module.ApplyPresetAsync(DisplayPresetKind.Medium, CancellationToken.None);
        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);

        Assert.Equal(DisplayPresetKind.High, module.CurrentPreset);

        gateway.WriteGate.SetResult();

        await WaitUntilAsync(
            () => module.Displays.All(row => !row.Selected || row.Preset == DisplayPresetKind.High),
            "the module never settled on the high preset");

        Assert.Equal(2, gateway.WriteLog.Count);
        Assert.Equal(GammaRampComposer.Compose(Original, DefaultOptions.High), gateway.LastWrittenRamp);
    }

    [Fact]
    public async Task RefreshAndReapply_writes_the_current_preset_to_the_selected_displays()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 1, "the high preset was never written");

        await module.RefreshAndReapplyAsync(CancellationToken.None);

        Assert.Equal(2, gateway.WriteLog.Count);
        Assert.Equal(GammaRampComposer.Compose(Original, DefaultOptions.High), gateway.LastWrittenRamp);
        Assert.Equal(DisplayPresetKind.High, module.CurrentPreset);
    }

    [Fact]
    public async Task RefreshAndReapply_keeps_the_original_captured_at_enable_time()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 1, "the high preset was never written");

        // The display now holds the preset. Re-enumerating must not mistake it for the user's
        // calibration and adopt it as the new original.
        await module.RefreshAndReapplyAsync(CancellationToken.None);

        Assert.Equal(GammaRampComposer.Compose(Original, DefaultOptions.High), gateway.CurrentRamp("display-1"));

        await module.DisableAsync(CancellationToken.None);
        Assert.Equal(Original, gateway.CurrentRamp("display-1"));
    }

    [Fact]
    public async Task RefreshAndReapply_can_overlap_the_display_worker_without_corrupting_recovery()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);
        gateway.WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await gateway.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The worker is parked mid-write while a topology refresh re-captures and persists. The two
        // paths share the captured originals and the last-written fingerprints, and they both write
        // the recovery file.
        Task refresh = module.RefreshAndReapplyAsync(CancellationToken.None);
        gateway.WriteGate.SetResult();
        await refresh;

        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        DisplayRecoveryEntry entry = Assert.Single(snapshot!.Displays);
        Assert.True(entry.TryReadOriginalRamp(out GammaRamp? captured));
        Assert.Equal(Original, captured);
    }

    // ---------------------------------------------------------------- edited configuration

    [Fact]
    public async Task Updating_options_changes_what_the_next_preset_composes_from()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);
        DisplayPresetOptions brighter = new(Gamma: 1.95, ShadowLift: 0.03, OutputCeiling: 0.9);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        // The module holds the copy it was built with, so adopting the edit is the only way the value
        // the user just typed can reach a display.
        module.UpdateOptions(DefaultOptions with { High = brighter });

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 1, "the high preset was never written");

        Assert.Equal(GammaRampComposer.Compose(Original, brighter), gateway.LastWrittenRamp);
    }

    [Fact]
    public async Task RefreshAndReapply_puts_back_the_ramp_of_a_display_that_left_the_selection()
    {
        FakeDisplayGammaGateway gateway = new();
        gateway.AddDisplay("display-1", "One", Original);
        gateway.AddDisplay("display-2", "Two", Second);

        await using DisplayModule module = CreateModule(gateway, "display-1", "display-2");
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(
            () => gateway.WriteCountByDisplay.GetValueOrDefault("display-2") == 1,
            "both displays were never enhanced");

        module.UpdateOptions(DefaultOptions with { SelectedDisplayIds = ["display-1"] });

        int writesBefore = gateway.WriteLog.Count;

        await module.RefreshAndReapplyAsync(CancellationToken.None);

        // Unticking a monitor is the user asking for it to stop being enhanced. It would keep the
        // preset for the rest of the session otherwise: nothing else puts it back while the module is
        // still on its way to being switched off.
        Assert.Equal(Second, gateway.CurrentRamp("display-2"));
        Assert.Equal(GammaRampComposer.Compose(Original, DefaultOptions.High), gateway.CurrentRamp("display-1"));

        // One write to put the ramp of display-2 back, one to reapply the preset to display-1.
        Assert.Equal(writesBefore + 2, gateway.WriteLog.Count);

        // The display that was put back is no longer owed a restore.
        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.Equal("display-1", Assert.Single(snapshot!.Displays).StableId);

        await module.DisableAsync(CancellationToken.None);

        // Disable has nothing left to do for the monitor that left the selection, so the ramp it
        // already had back is not written to it a second time.
        Assert.Equal(Second, gateway.CurrentRamp("display-2"));
        Assert.Equal(2, gateway.WriteCountByDisplay["display-2"]);
        Assert.Equal(3, gateway.WriteCountByDisplay["display-1"]);
    }

    [Fact]
    public async Task A_deselected_display_keeps_its_recovery_entry_when_the_restore_is_refused()
    {
        FakeDisplayGammaGateway gateway = new();
        gateway.AddDisplay("display-1", "One", Original);
        gateway.AddDisplay("display-2", "Two", Second);

        await using DisplayModule module = CreateModule(gateway, "display-1", "display-2");
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(
            () => gateway.WriteCountByDisplay.GetValueOrDefault("display-2") == 1,
            "both displays were never enhanced");

        gateway.FailOnWrite.Add("display-2");
        module.UpdateOptions(DefaultOptions with { SelectedDisplayIds = ["display-1"] });

        await module.RefreshAndReapplyAsync(CancellationToken.None);

        // Still enhanced, so the entry has to survive for the next launch to retry: dropping it would
        // leave the preset on the display with nothing anywhere recording what it replaced.
        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.Equal(2, snapshot!.Displays.Count);
    }

    // ---------------------------------------------------------------- disable

    [Fact]
    public async Task Disable_restores_the_captured_original_and_is_idempotent()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.ApplyPresetAsync(DisplayPresetKind.High, CancellationToken.None);
        await WaitUntilAsync(() => gateway.WriteLog.Count >= 1, "the high preset was never written");

        await module.DisableAsync(CancellationToken.None);
        await module.DisableAsync(CancellationToken.None);

        Assert.Equal(Original, gateway.CurrentRamp("display-1"));
        Assert.Equal(2, gateway.WriteLog.Count);
        Assert.Equal(ModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Disable_drops_the_recovery_entry_once_the_original_is_back()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);
        await module.DisableAsync(CancellationToken.None);

        Assert.Null(await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disable_keeps_the_recovery_entry_when_the_original_could_not_be_restored()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        gateway.FailOnWrite.Add("display-1");

        await using DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);
        await module.DisableAsync(CancellationToken.None);

        // The display is still enhanced, so the entry has to survive for the next launch to retry.
        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Displays);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_restores_the_original()
    {
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        DisplayModule module = CreateModule(gateway);
        await module.EnableAsync(CancellationToken.None);

        await module.DisposeAsync();
        await module.DisposeAsync();

        Assert.Equal(Original, gateway.CurrentRamp("display-1"));
        Assert.Equal(1, gateway.WriteCountByDisplay["display-1"]);
    }

    // ---------------------------------------------------------------- recovery

    [Fact]
    public async Task Recover_restores_the_original_when_the_display_still_holds_the_last_write()
    {
        GammaRamp toolkitRamp = GammaRampComposer.Compose(Original, DefaultOptions.High);

        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(toolkitRamp);

        await SaveRecoveryAsync("display-1", Original, GammaRampFingerprint.Compute(toolkitRamp));

        await using DisplayModule module = CreateModule(gateway);
        await module.RecoverAsync(CancellationToken.None);

        Assert.Equal(Original, gateway.LastWrittenRamp);
        Assert.Null(await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Recover_leaves_a_display_that_someone_else_changed_alone()
    {
        GammaRamp toolkitRamp = GammaRampComposer.Compose(Original, DefaultOptions.High);

        // The display holds neither the original nor the preset the toolkit wrote: something else
        // has driven it since, and that newer value is the one worth keeping.
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(GammaRamp.CreateIdentity());

        // The snapshot says the toolkit wrote the high preset, but the display no longer holds it.
        await SaveRecoveryAsync("display-1", Original, GammaRampFingerprint.Compute(toolkitRamp));

        await using DisplayModule module = CreateModule(gateway);
        await module.RecoverAsync(CancellationToken.None);

        Assert.Null(gateway.LastWrittenRamp);
    }

    [Fact]
    public async Task Recover_keeps_an_entry_for_a_display_that_is_not_connected()
    {
        GammaRamp toolkitRamp = GammaRampComposer.Compose(Original, DefaultOptions.High);

        FakeDisplayGammaGateway gateway = new();

        await SaveRecoveryAsync("display-1", Original, GammaRampFingerprint.Compute(toolkitRamp));

        await using DisplayModule module = CreateModule(gateway);
        await module.RecoverAsync(CancellationToken.None);

        Assert.Null(gateway.LastWrittenRamp);

        // Unplugging a monitor must not throw its recovery away: it may be back next launch.
        DisplayRecoverySnapshot? snapshot = await _store.LoadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Displays);
    }

    [Fact]
    public async Task Recover_ignores_an_entry_written_without_any_toolkit_write()
    {
        // Enabling captures an original but writes nothing, so an empty fingerprint means there is
        // nothing to undo. Restoring here would rewrite a display the toolkit never touched.
        FakeDisplayGammaGateway gateway = FakeDisplayGammaGateway.OneDisplay(Original);

        await SaveRecoveryAsync("display-1", Original, string.Empty);

        await using DisplayModule module = CreateModule(gateway);
        await module.RecoverAsync(CancellationToken.None);

        Assert.Null(gateway.LastWrittenRamp);
    }

    // ---------------------------------------------------------------- helpers

    private static GammaRamp Original => BuildRamp(200);

    private static GammaRamp Second => BuildRamp(210);

    private static GammaRamp BuildRamp(ushort step)
    {
        ushort[] channel = Enumerable.Range(0, 256).Select(index => (ushort)(index * step)).ToArray();
        return new GammaRamp(channel, channel, channel);
    }

    private async Task SaveRecoveryAsync(string stableId, GammaRamp original, string lastWrittenFingerprint)
    {
        await _store.SaveAsync(
            new DisplayRecoverySnapshot(
                DisplayRecoverySnapshot.CurrentSchemaVersion,
                [DisplayRecoveryEntry.Create(stableId, original, lastWrittenFingerprint, CapturedAt)]),
            CancellationToken.None);
    }

    private DisplayModule CreateModule(FakeDisplayGammaGateway gateway, params string[] selectedIds)
    {
        // No argument means "the default selection", not "select nothing".
        DisplayOptions options = selectedIds.Length == 0
            ? DefaultOptions
            : DefaultOptions with { SelectedDisplayIds = selectedIds };

        return new DisplayModule(options, gateway, _store, logger: null, TimeProvider.System);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting until {because}.");
    }
}
