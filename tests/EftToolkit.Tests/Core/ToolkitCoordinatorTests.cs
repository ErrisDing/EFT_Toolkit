using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Display;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Core.Modules;
using EftToolkit.Core.Platform;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Core;

/// <summary>
/// The coordinator is the only thing that decides what runs, in what order, and what happens when
/// one of those things fails. Every participant is a fake that writes to a shared journal, so the
/// assertions are about the order rather than about what any participant did internally.
/// </summary>
public class ToolkitCoordinatorTests
{
    /// <summary>Short enough to keep the deadline test quick, long enough not to fire by accident.</summary>
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(250);

    private readonly List<string> _journal = [];
    private readonly RecordingLogger _logger = new();

    private readonly FakeDisplayController _display;
    private readonly FakeAudioController _audio;
    private readonly FakeHotkeyService _hotkeys;
    private readonly FakePlatformEventSource _platformEvents;
    private readonly FakeOptionsStore _store = new();

    public ToolkitCoordinatorTests()
    {
        _display = new FakeDisplayController(_journal);
        _audio = new FakeAudioController(_journal);
        _hotkeys = new FakeHotkeyService(_journal);
        _platformEvents = new FakePlatformEventSource(_journal);
    }

    private ToolkitCoordinator CreateCoordinator(TimeSpan? shutdownTimeout = null) =>
        new(_display, _audio, _hotkeys, _platformEvents, _store, _logger, TimeProvider.System, shutdownTimeout);

    private static ToolkitOptions WithDisplay(bool enabled) => ToolkitOptions.CreateDefault() with
    {
        Display = ToolkitOptions.CreateDefault().Display with { Enabled = enabled },
    };

    private static ToolkitOptions WithAudio(bool enabled) => ToolkitOptions.CreateDefault() with
    {
        Audio = ToolkitOptions.CreateDefault().Audio with { Enabled = enabled },
    };

    private static ToolkitOptions WithBoth(bool display, bool audio) => ToolkitOptions.CreateDefault() with
    {
        Display = ToolkitOptions.CreateDefault().Display with { Enabled = display },
        Audio = ToolkitOptions.CreateDefault().Audio with { Enabled = audio },
    };

    /// <summary>The steps the coordinator reported, in the order it reported them.</summary>
    private IReadOnlyList<string> ReportedSteps() =>
        [.. _logger.Entries
            .Where(entry => entry.EventName == "lifecycle.step")
            .Select(entry => (string)entry.Properties!["step"]!)];

    // ---------------------------------------------------------------- starting

    [Fact]
    public async Task Start_enables_nothing_that_is_not_configured()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();

        await coordinator.StartAsync(CancellationToken.None);

        Assert.False(coordinator.IsDisplayEnabled);
        Assert.False(coordinator.IsAudioEnabled);
        Assert.Equal(["display.recover", "platformEvents.start"], _journal);
    }

    [Fact]
    public async Task Start_uses_the_configuration_the_store_returned()
    {
        _store.Stored = ToolkitOptions.CreateDefault() with
        {
            Display = ToolkitOptions.CreateDefault().Display with
            {
                Low = new DisplayPresetOptions(Gamma: 1.8, ShadowLift: 0.05, OutputCeiling: 0.9),
            },
        };

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(1, _store.LoadCount);
        Assert.Equal(1.8, coordinator.Options.Display.Low.Gamma);
        Assert.Equal(0.05, coordinator.Options.Display.Low.ShadowLift);
    }

    [Fact]
    public async Task Start_sanitizes_configuration_that_arrived_without_being_sanitized()
    {
        _store.Stored = ToolkitOptions.CreateDefault() with
        {
            Display = ToolkitOptions.CreateDefault().Display with
            {
                Low = new DisplayPresetOptions(Gamma: 12.0, ShadowLift: -3.0, OutputCeiling: 4.0),
            },
        };

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        // A gamma of 12 is a black screen rather than a brighter one. The panel validates what the
        // user types; this is the guarantee that what is in force is approved whatever the stored
        // file was found to contain.
        DisplayPresetOptions expected = ToolkitOptions.CreateDefault().Display.Low;

        Assert.Equal(expected.Gamma, coordinator.Options.Display.Low.Gamma);
        Assert.Equal(expected.ShadowLift, coordinator.Options.Display.Low.ShadowLift);
        Assert.Equal(expected.OutputCeiling, coordinator.Options.Display.Low.OutputCeiling);
    }

    [Fact]
    public async Task Start_restores_a_crashed_ramp_before_either_module_is_enabled()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        // Recovery is first: this process must not write a preset over a ramp that a previous run
        // left behind and never restored.
        Assert.Equal(
            [
                "display.recover",
                "platformEvents.start",
                "display.enable",
                "hotkey.register",
                "audio.enable",
            ],
            _journal);
    }

    [Fact]
    public async Task Start_enables_the_configured_display_and_registers_the_shortcuts_after_it()
    {
        _store.Stored = WithDisplay(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        Assert.True(coordinator.IsDisplayEnabled);
        Assert.False(coordinator.IsAudioEnabled);
        Assert.Equal(
            ["display.recover", "platformEvents.start", "display.enable", "hotkey.register"],
            _journal);
    }

    [Fact]
    public async Task Start_enables_configured_audio_without_touching_the_display()
    {
        _store.Stored = WithAudio(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        Assert.False(coordinator.IsDisplayEnabled);
        Assert.True(coordinator.IsAudioEnabled);
        Assert.DoesNotContain("display.enable", _journal);
        Assert.DoesNotContain("hotkey.register", _journal);
    }

    [Fact]
    public async Task Start_does_not_claim_the_shortcuts_when_the_display_is_off()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        // The shortcuts would do nothing, and registering them takes F2-F5 away from whatever else
        // the user runs for no benefit.
        Assert.Equal(0, _hotkeys.RegisterCount);
    }

    [Fact]
    public async Task Start_is_idempotent()
    {
        _store.Stored = WithDisplay(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(1, _display.EnableCount);
        Assert.Equal(1, _hotkeys.RegisterCount);
        Assert.Equal(1, _platformEvents.StartCount);
    }

    // ---------------------------------------------------------------- toggling display

    [Fact]
    public async Task Turning_the_display_on_starts_the_module_and_then_claims_the_shortcuts()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);

        Assert.True(coordinator.IsDisplayEnabled);
        Assert.Equal(["display.recover", "platformEvents.start", "display.enable", "hotkey.register"], _journal);
    }

    [Fact]
    public async Task Turning_the_display_off_releases_the_shortcuts_before_restoring_the_ramps()
    {
        _store.Stored = WithDisplay(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.SetDisplayEnabledAsync(false, CancellationToken.None);

        Assert.False(coordinator.IsDisplayEnabled);
        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _hotkeys.UnregisterCount);

        // No preset can arrive while the ramps are being put back, which is the reason the shortcuts
        // go first.
        Assert.True(
            _journal.IndexOf("hotkey.unregister") < _journal.IndexOf("display.disable"),
            string.Join(", ", _journal));
    }

    [Fact]
    public async Task Turning_the_display_on_persists_the_flag()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);

        Assert.NotNull(_store.LastSaved);
        Assert.True(_store.LastSaved!.Display.Enabled);
    }

    [Fact]
    public async Task A_display_transition_that_fails_is_not_persisted()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _display.EnableFailure = new InvalidOperationException("no display would take the ramp");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetDisplayEnabledAsync(true, CancellationToken.None));

        // The stored configuration is the record of what is on. Writing "on" for a module that is
        // not would make the next launch recover a state that was never reached.
        Assert.Null(_store.LastSaved);
        Assert.False(coordinator.IsDisplayEnabled);
    }

    [Fact]
    public async Task Repeating_a_display_transition_does_nothing_the_second_time()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);
        await coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);

        Assert.Equal(1, _display.EnableCount);
        Assert.Equal(1, _hotkeys.RegisterCount);
    }

    // ---------------------------------------------------------------- toggling audio

    [Fact]
    public async Task Turning_audio_on_and_off_touches_no_display_service()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.SetAudioEnabledAsync(true, CancellationToken.None);
        await coordinator.SetAudioEnabledAsync(false, CancellationToken.None);

        Assert.Equal(1, _audio.EnableCount);
        Assert.Equal(1, _audio.DisableCount);
        Assert.DoesNotContain("display.enable", _journal);
        Assert.DoesNotContain("display.disable", _journal);
        Assert.DoesNotContain("hotkey.register", _journal);
    }

    [Fact]
    public async Task Turning_audio_off_persists_the_flag()
    {
        _store.Stored = WithAudio(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.SetAudioEnabledAsync(false, CancellationToken.None);

        Assert.NotNull(_store.LastSaved);
        Assert.False(_store.LastSaved!.Audio.Enabled);
        Assert.False(coordinator.IsAudioEnabled);
    }

    [Fact]
    public async Task An_audio_transition_that_fails_is_not_persisted()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _audio.EnableFailure = new InvalidOperationException("the endpoints were busy");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetAudioEnabledAsync(true, CancellationToken.None));

        Assert.Null(_store.LastSaved);
        Assert.False(coordinator.IsAudioEnabled);
    }

    [Fact]
    public async Task Turning_audio_on_does_not_start_the_display_module()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.SetAudioEnabledAsync(true, CancellationToken.None);

        Assert.False(coordinator.IsDisplayEnabled);
        Assert.Equal(0, _display.EnableCount);
    }

    // ---------------------------------------------------------------- editing

    [Fact]
    public async Task An_edit_is_validated_adopted_and_stored()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.UpdateOptionsAsync(
            options => options with
            {
                Display = options.Display with
                {
                    Low = new DisplayPresetOptions(Gamma: 1.8, ShadowLift: 0.05, OutputCeiling: 0.9),
                },
            },
            CancellationToken.None);

        // The module holds the copy it was built with, so an edit that is only stored would be an
        // edit the running application never sees.
        Assert.Equal(1.8, _display.Adopted!.Low.Gamma);
        Assert.Equal(1.8, _store.LastSaved!.Display.Low.Gamma);
        Assert.Equal(1.8, coordinator.Options.Display.Low.Gamma);
    }

    [Fact]
    public async Task An_edit_outside_the_approved_ranges_is_replaced_with_the_approved_default()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.UpdateOptionsAsync(
            options => options with
            {
                Display = options.Display with
                {
                    High = new DisplayPresetOptions(Gamma: 12.0, ShadowLift: 0.0, OutputCeiling: 1.0),
                },
            },
            CancellationToken.None);

        // The panel refuses an out-of-range value at the field the user typed it into. This is the
        // second line of defence, and it is here because a gamma of 12 is a dark screen rather than
        // a brighter one: neither the module nor the file is allowed to end up holding it.
        Assert.Equal(1.55, _display.Adopted!.High.Gamma);
        Assert.Equal(1.55, _store.LastSaved!.Display.High.Gamma);
        Assert.Equal(1.55, coordinator.Options.Display.High.Gamma);
    }

    [Fact]
    public async Task Changing_the_selection_re_enumerates_while_the_display_is_on()
    {
        _store.Stored = WithDisplay(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.UpdateOptionsAsync(
            options => options with
            {
                Display = options.Display with { SelectedDisplayIds = ["monitor-a", "monitor-b"] },
            },
            CancellationToken.None);

        // A newly ticked monitor only exists as a row once the module enumerates again, and that is
        // also what captures its original ramp before anything is written to it.
        Assert.Equal(1, _display.RefreshCount);
        Assert.Equal(["monitor-a", "monitor-b"], _display.Adopted!.SelectedDisplayIds);
    }

    [Fact]
    public async Task Changing_a_preset_value_rewrites_the_preset_in_force()
    {
        _store.Stored = WithDisplay(enabled: true);
        _display.CurrentPreset = DisplayPresetKind.High;

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.UpdateOptionsAsync(
            options => options with
            {
                Display = options.Display with
                {
                    High = new DisplayPresetOptions(Gamma: 1.9, ShadowLift: 0.02, OutputCeiling: 1.0),
                },
            },
            CancellationToken.None);

        // The user is looking at a preset, so the values it composes from changing has to change what
        // is on the screen — and no re-enumeration is needed to do it.
        Assert.Equal(0, _display.RefreshCount);
        Assert.Equal(DisplayPresetKind.High, _display.LastPreset);
    }

    [Fact]
    public async Task An_edit_while_display_is_off_is_stored_and_nothing_is_written()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.UpdateOptionsAsync(
            options => options with
            {
                Display = options.Display with { SelectedDisplayIds = ["monitor-a"] },
            },
            CancellationToken.None);

        // Nothing is on the displays, so there is nothing to redo. What was stored is what the next
        // enable acts on, and the module already holds it.
        Assert.Equal(0, _display.RefreshCount);
        Assert.Equal(0, _display.ApplyCount);
        Assert.Equal(["monitor-a"], _store.LastSaved!.Display.SelectedDisplayIds);
    }

    [Fact]
    public async Task An_edit_never_switches_a_module_on_or_off()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        // Switching a module on is a transition that registers shortcuts and opens a WASAPI stream.
        // An edit that arrived here with a different flag would otherwise change the file and leave
        // the running application disagreeing with it.
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.UpdateOptionsAsync(
            options => options with { Display = options.Display with { Enabled = true } },
            CancellationToken.None));

        Assert.False(coordinator.IsDisplayEnabled);
        Assert.Null(_store.LastSaved);
    }

    [Fact]
    public async Task An_edit_after_shutdown_started_is_refused()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator(shutdownTimeout: ShortDeadline);
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.ShutdownAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UpdateOptionsAsync(
            options => options,
            CancellationToken.None));
    }

    // ---------------------------------------------------------------- events

    [Fact]
    public async Task A_preset_request_applies_only_while_the_display_is_enabled()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _hotkeys.RaisePresetRequested(DisplayPresetKind.High);
        await Task.Delay(50);
        Assert.Equal(0, _display.ApplyCount);

        await coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);
        _hotkeys.RaisePresetRequested(DisplayPresetKind.High);

        await AsyncWait.UntilAsync(() => _display.ApplyCount == 1, "the preset was applied");
        Assert.Equal(DisplayPresetKind.High, _display.LastPreset);
    }

    [Fact]
    public async Task A_preset_request_does_not_block_the_callback_that_delivered_it()
    {
        _store.Stored = WithDisplay(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _display.ApplyGate = release.Task;

        // The shortcut is delivered on the message window's thread. Waiting there for a gamma write
        // would stall every other message that window handles, including the next shortcut.
        _hotkeys.RaisePresetRequested(DisplayPresetKind.Medium);

        await AsyncWait.UntilAsync(() => _display.ApplyCount == 1, "the apply was started");
        Assert.False(_display.ApplyCompleted);

        release.SetResult();
        await AsyncWait.UntilAsync(() => _display.ApplyCompleted, "the apply finished");
    }

    [Fact]
    public async Task A_failing_preset_request_is_logged_rather_than_thrown_at_the_hotkey_thread()
    {
        _store.Stored = WithDisplay(enabled: true);

        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _display.ApplyFailure = new InvalidOperationException("the ramp was refused");

        _hotkeys.RaisePresetRequested(DisplayPresetKind.Low);

        await AsyncWait.UntilAsync(
            () => _logger.Logged("lifecycle.presetFailed"),
            "the preset failure was logged");
    }

    [Fact]
    public async Task A_display_environment_event_refreshes_only_while_the_display_is_enabled()
    {
        await using ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _platformEvents.RaiseDisplayEnvironmentChanged();
        await Task.Delay(50);
        Assert.Equal(0, _display.RefreshCount);

        await coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);
        _platformEvents.RaiseDisplayEnvironmentChanged();

        await AsyncWait.UntilAsync(() => _display.RefreshCount == 1, "the refresh ran");
    }

    // ---------------------------------------------------------------- shutdown

    [Fact]
    public async Task Shutdown_runs_the_steps_in_the_documented_order()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        ShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(ShutdownSteps.Ordered, ReportedSteps());

        // Bypass before disable: the limiter stops processing before the stream closes, so the
        // teardown is not heard as a click.
        Assert.Equal(
            ["audio.bypass", "audio.disable"],
            _journal.Where(entry => entry is "audio.bypass" or "audio.disable"));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.False(result.TimedOut);
        Assert.True(result.CompletedAtUtc >= result.StartedAtUtc);
    }

    [Fact]
    public async Task Shutdown_runs_every_participant_exactly_once()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(1, _audio.DisableCount);
        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _hotkeys.UnregisterCount);
        Assert.Equal(1, _platformEvents.StopCount);
        Assert.Equal(1, _logger.DisposeCount);

        // Starting a module is not a change the user made, so nothing is written until shutdown:
        // what is stored is what was in force, not what a module reached on its own.
        Assert.Equal(1, _store.SaveCount);
        Assert.True(_store.LastSaved!.Display.Enabled);
        Assert.True(_store.LastSaved.Audio.Enabled);
    }

    [Fact]
    public async Task Shutdown_twice_returns_the_same_task_and_does_nothing_again()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        Task<ShutdownResult> first = coordinator.ShutdownAsync(CancellationToken.None);
        Task<ShutdownResult> second = coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Same(first, second);

        ShutdownResult firstResult = await first;
        ShutdownResult secondResult = await second;

        Assert.Same(firstResult, secondResult);
        Assert.Equal(1, _audio.DisableCount);
        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _logger.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_waits_for_a_transition_that_is_already_running()
    {
        ToolkitCoordinator coordinator = CreateCoordinator(TimeSpan.FromSeconds(5));
        await coordinator.StartAsync(CancellationToken.None);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _display.EnableGate = release.Task;

        Task enabling = coordinator.SetDisplayEnabledAsync(true, CancellationToken.None);
        await AsyncWait.UntilAsync(() => _display.EnableCount == 1, "the transition started");

        Task<ShutdownResult> shutdown = coordinator.ShutdownAsync(CancellationToken.None);
        await Task.Delay(100);

        // The teardown does not start underneath a transition that is still running: a module being
        // switched on while the other half of the application is switching it off is how a display
        // is left with a ramp nobody is going to put back.
        Assert.False(shutdown.IsCompleted);

        release.SetResult();
        await enabling;

        ShutdownResult result = await shutdown;

        Assert.True(result.Succeeded);
        Assert.Equal(1, _display.EnableCount);
        Assert.Equal(1, _display.DisableCount);
        Assert.True(
            _journal.IndexOf("display.enable") < _journal.IndexOf("display.disable"),
            string.Join(", ", _journal));
    }

    [Fact]
    public async Task A_step_that_fails_is_recorded_and_the_remaining_steps_still_run()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _audio.DisableFailure = new InvalidOperationException("the endpoint would not release");

        ShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        ShutdownFailure failure = Assert.Single(result.Failures);
        Assert.Equal(ShutdownSteps.AudioDisable, failure.Step);
        Assert.Equal("the endpoint would not release", failure.Message);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);

        // Everything after the failure still ran, including the parts a failure makes more
        // important: the ramps are put back and the configuration is written.
        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _hotkeys.UnregisterCount);
        Assert.Equal(1, _platformEvents.StopCount);
        Assert.Equal(1, _store.SaveCount);
        Assert.Equal(1, _logger.DisposeCount);
    }

    [Fact]
    public async Task Settings_are_written_even_when_a_display_step_failed()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _display.DisableFailure = new InvalidOperationException("the ramp would not restore");

        ShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.Equal(ShutdownSteps.DisplayDisable, Assert.Single(result.Failures).Step);
        Assert.Equal(1, _store.SaveCount);
        Assert.Equal(1, _logger.DisposeCount);
    }

    [Fact]
    public async Task The_final_save_and_the_log_are_flushed_even_when_the_deadline_passes()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator(ShortDeadline);
        await coordinator.StartAsync(CancellationToken.None);

        // A device that has stopped answering, in the way the real teardown can: it holds the step
        // open and does not look at the token it was given.
        _audio.DisableGate =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        ShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(Assert.Single(result.Failures, failure => failure.Step == ShutdownSteps.AudioDisable).TimedOut);

        // The steps the deadline cut off are named rather than silently skipped.
        Assert.Contains(result.Failures, failure => failure.Step == ShutdownSteps.DisplayDisable);
        Assert.Contains(result.Failures, failure => failure.Step == ShutdownSteps.HotkeysUnregister);
        Assert.Contains(result.Failures, failure => failure.Step == ShutdownSteps.PlatformEventsStop);

        // The two that decide what the next launch sees are outside the deadline.
        Assert.Equal(1, _store.SaveCount);
        Assert.Equal(1, _logger.DisposeCount);
        Assert.DoesNotContain(result.Failures, failure => failure.Step == ShutdownSteps.SettingsSave);
        Assert.DoesNotContain(result.Failures, failure => failure.Step == ShutdownSteps.LoggerFlush);
    }

    [Fact]
    public async Task Commands_are_refused_once_shutdown_has_begun()
    {
        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        Task<ShutdownResult> shutdown = coordinator.ShutdownAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetDisplayEnabledAsync(true, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetAudioEnabledAsync(true, CancellationToken.None));

        await shutdown;

        Assert.Equal(0, _display.EnableCount);
        Assert.Equal(0, _audio.EnableCount);
    }

    [Fact]
    public async Task Shutdown_without_a_start_still_runs_every_step()
    {
        ToolkitCoordinator coordinator = CreateCoordinator();

        ShutdownResult result = await coordinator.ShutdownAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _audio.DisableCount);
        Assert.Equal(1, _logger.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_stops_acting_on_platform_events_and_shortcuts()
    {
        _store.Stored = WithDisplay(enabled: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        _hotkeys.RaisePresetRequested(DisplayPresetKind.High);
        await AsyncWait.UntilAsync(() => _display.ApplyCount == 1, "the preset was applied");

        int applied = _display.ApplyCount;
        int refreshed = _display.RefreshCount;

        await coordinator.ShutdownAsync(CancellationToken.None);

        _platformEvents.RaiseDisplayEnvironmentChanged();
        _hotkeys.RaisePresetRequested(DisplayPresetKind.High);
        await Task.Delay(50);

        Assert.Equal(applied, _display.ApplyCount);
        Assert.Equal(refreshed, _display.RefreshCount);
    }

    [Fact]
    public async Task Disposing_the_coordinator_shuts_it_down_once()
    {
        _store.Stored = WithBoth(display: true, audio: true);

        ToolkitCoordinator coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);

        await coordinator.DisposeAsync();

        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _audio.DisableCount);
        Assert.Equal(1, _logger.DisposeCount);

        await coordinator.DisposeAsync();

        Assert.Equal(1, _display.DisableCount);
        Assert.Equal(1, _logger.DisposeCount);
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeDisplayController(List<string> journal) : IDisplayController
    {
        public int EnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public int RefreshCount { get; private set; }

        public int ApplyCount { get; private set; }

        public DisplayPresetKind? LastPreset { get; private set; }

        public bool ApplyCompleted { get; private set; }

        /// <summary>The module's own preset, which the coordinator asks for when it reapplies.</summary>
        public DisplayPresetKind CurrentPreset { get; set; } = DisplayPresetKind.Medium;

        /// <summary>The last configuration handed over through <see cref="UpdateOptions"/>.</summary>
        public DisplayOptions? Adopted { get; private set; }

        public Exception? EnableFailure { get; set; }

        public Exception? DisableFailure { get; set; }

        public Exception? ApplyFailure { get; set; }

        /// <summary>When set, switching on waits on it. Stands in for a slow transition.</summary>
        public Task? EnableGate { get; set; }

        /// <summary>When set, applying a preset waits on it. Stands in for a slow display write.</summary>
        public Task? ApplyGate { get; set; }

        public ModuleStatus Status => new(ModuleState.Disabled);

        // The coordinator reads a module's state through the state machine's own event surface,
        // which these tests do not exercise, so the accessors are deliberately inert.
        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public async Task EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (EnableFailure is { } failure)
            {
                throw failure;
            }

            EnableCount++;
            journal.Add("display.enable");

            if (EnableGate is { } gate)
            {
                await gate;
            }
        }

        public Task DisableAsync(CancellationToken cancellationToken)
        {
            if (DisableFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            DisableCount++;
            journal.Add("display.disable");
            return Task.CompletedTask;
        }

        public Task ApplyPresetAsync(DisplayPresetKind preset, CancellationToken cancellationToken)
        {
            ApplyCount++;
            journal.Add($"display.apply.{preset}");

            return ApplyAsync();

            async Task ApplyAsync()
            {
                if (ApplyGate is { } gate)
                {
                    await gate;
                }

                ApplyCompleted = true;

                if (ApplyFailure is { } failure)
                {
                    throw failure;
                }

                LastPreset = preset;
            }
        }

        public void UpdateOptions(DisplayOptions options)
        {
            Adopted = options;
            journal.Add("display.options");
        }

        public Task RefreshAndReapplyAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            journal.Add("display.refresh");
            return Task.CompletedTask;
        }

        public Task RecoverAsync(CancellationToken cancellationToken)
        {
            journal.Add("display.recover");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudioController(List<string> journal) : IAudioController
    {
        public int EnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public bool Bypass { get; private set; }

        public Exception? EnableFailure { get; set; }

        public Exception? DisableFailure { get; set; }

        /// <summary>When set, stopping waits on it and ignores the token, as the real teardown does.</summary>
        public Task? DisableGate { get; set; }

        public bool IsTargetRunning => false;

        public bool IsRouteOpen => false;

        public ModuleStatus Status => new(ModuleState.Disabled);

        public event EventHandler<ModuleStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public async Task EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (EnableFailure is { } failure)
            {
                throw failure;
            }

            EnableCount++;
            journal.Add("audio.enable");

            await Task.CompletedTask;
        }

        public async Task DisableAsync(CancellationToken cancellationToken)
        {
            DisableCount++;
            journal.Add("audio.disable");

            if (DisableGate is { } gate)
            {
                await gate;
            }

            if (DisableFailure is { } failure)
            {
                throw failure;
            }
        }

        public Task SetBypassAsync(bool bypass, CancellationToken cancellationToken)
        {
            Bypass = bypass;
            journal.Add(bypass ? "audio.bypass" : "audio.unbypass");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHotkeyService(List<string> journal) : IHotkeyService
    {
        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public IReadOnlyDictionary<DisplayPresetKind, bool> Registrations { get; private set; } =
            new Dictionary<DisplayPresetKind, bool>();

        public event EventHandler<DisplayPresetKind>? PresetRequested;

        public Task RegisterAsync(CancellationToken cancellationToken)
        {
            RegisterCount++;
            journal.Add("hotkey.register");
            Registrations = new Dictionary<DisplayPresetKind, bool>
            {
                [DisplayPresetKind.Original] = true,
                [DisplayPresetKind.Low] = true,
                [DisplayPresetKind.Medium] = true,
                [DisplayPresetKind.High] = true,
            };

            return Task.CompletedTask;
        }

        public Task UnregisterAsync(CancellationToken cancellationToken)
        {
            UnregisterCount++;
            journal.Add("hotkey.unregister");
            Registrations = new Dictionary<DisplayPresetKind, bool>();
            return Task.CompletedTask;
        }

        public void RaisePresetRequested(DisplayPresetKind preset) => PresetRequested?.Invoke(this, preset);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlatformEventSource(List<string> journal) : IPlatformEventSource
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public event EventHandler? DisplayEnvironmentChanged;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            journal.Add("platformEvents.start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            journal.Add("platformEvents.stop");
            return Task.CompletedTask;
        }

        public void RaiseDisplayEnvironmentChanged() =>
            DisplayEnvironmentChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOptionsStore : IOptionsStore
    {
        public ToolkitOptions Stored { get; set; } = ToolkitOptions.CreateDefault();

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public ToolkitOptions? LastSaved { get; private set; }

        public Task<ToolkitOptions> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(Stored);
        }

        public Task SaveAsync(ToolkitOptions options, CancellationToken cancellationToken)
        {
            SaveCount++;
            LastSaved = options;
            return Task.CompletedTask;
        }
    }
}
