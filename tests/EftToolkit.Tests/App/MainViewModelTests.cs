using EftToolkit.App.ViewModels;
using EftToolkit.Audio;
using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Dsp;
using EftToolkit.Audio.Processes;
using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Diagnostics;
using EftToolkit.Core.Display;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Core.Modules;
using EftToolkit.Core.Platform;
using EftToolkit.Display;
using EftToolkit.Display.Gamma;
using EftToolkit.Display.Recovery;
using EftToolkit.Tests.Display;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.App;

/// <summary>
/// The panel is the only place the two modules are presented side by side, so the tests that matter
/// are about what a click reaches: which module a switch starts, what a tick stores, and what a
/// fault in one half is allowed to do to the other.
/// </summary>
/// <remarks>
/// Everything under the view models is real — the coordinator, both modules, the option store — and
/// only the drivers are substituted: a display driver that holds ramps in memory, an audio catalog
/// that reports fixed endpoints, and a stream session that reports whatever levels a test sets. A
/// panel test that stubbed the modules would only be testing its own fakes.
/// </remarks>
public class MainViewModelTests
{
    [Fact]
    public async Task The_display_switch_enables_and_disables_only_the_display_module()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();
        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.False(harness.ViewModel.Display.IsEnabled);

        await harness.ViewModel.Display.ToggleEnabledCommand.ExecuteAsync();

        Assert.True(harness.ViewModel.Display.IsEnabled);
        Assert.True(harness.Coordinator.IsDisplayEnabled);

        // Switching on arms the feature: it records what is on each display so a preset can be
        // undone, and it does not change the picture by itself.
        Assert.Empty(harness.Gateway.WriteLog);

        // Audio was off before and is off after: switching one half of the panel on must not start
        // the other half, and nothing in the display path may wait on an audio device.
        Assert.False(harness.Coordinator.IsAudioEnabled);
        Assert.Empty(harness.Sessions);

        await harness.ViewModel.Display.ToggleEnabledCommand.ExecuteAsync();

        Assert.False(harness.ViewModel.Display.IsEnabled);
        Assert.False(harness.Coordinator.IsDisplayEnabled);

        // The captured original is back, which is the only thing that makes switching off safe.
        Assert.Equal(PanelHarness.Original, harness.Gateway.CurrentRamp(PanelHarness.DisplayId));
        Assert.Single(harness.Gateway.WriteLog);
    }

    [Fact]
    public async Task The_audio_switch_starts_the_audio_module_without_touching_the_displays()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: true);

        await harness.ViewModel.Display.ToggleEnabledCommand.ExecuteAsync();
        await harness.Display.ApplyPresetAsync(DisplayPresetKind.Medium, CancellationToken.None);
        await AsyncWait.UntilAsync(() => harness.Gateway.WriteLog.Count >= 1, "the preset was never written");

        int writes = harness.Gateway.WriteLog.Count;

        await harness.ViewModel.Audio.ToggleEnabledCommand.ExecuteAsync();

        Assert.True(harness.ViewModel.Audio.IsEnabled);
        Assert.True(harness.Coordinator.IsAudioEnabled);
        Assert.True(Assert.Single(harness.Sessions).IsRunning);

        // Not one ramp was written by the audio transition, and the display module is still on.
        Assert.Equal(writes, harness.Gateway.WriteLog.Count);
        Assert.True(harness.Coordinator.IsDisplayEnabled);
    }

    [Fact]
    public async Task Ticking_a_monitor_stores_its_stable_id_alongside_the_others()
    {
        // Two monitors, neither selected yet.
        await using PanelHarness harness = await PanelHarness.CreateAsync(
            options: PanelHarness.DefaultOptions with
            {
                Display = PanelHarness.DefaultOptions.Display with { SelectedDisplayIds = [] },
            });

        harness.Gateway.AddDisplay(PanelHarness.SecondDisplayId, "Second", PanelHarness.Second);
        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, harness.ViewModel.Display.Monitors.Count);
        Assert.All(harness.ViewModel.Display.Monitors, row => Assert.False(row.IsSelected));

        await harness.ViewModel.Display.Monitors[0].ToggleSelectionCommand.ExecuteAsync();
        await harness.ViewModel.Display.Monitors[1].ToggleSelectionCommand.ExecuteAsync();

        // Both identifiers, in the order they were ticked, because the selection is what the next
        // launch reads back: a row that ticked itself without going through the coordinator would
        // leave the panel disagreeing with the file.
        Assert.Equal(
            [PanelHarness.DisplayId, PanelHarness.SecondDisplayId],
            await harness.StoredSelectionAsync());

        Assert.True(harness.ViewModel.Display.Monitors[0].IsSelected);
        Assert.True(harness.ViewModel.Display.Monitors[1].IsSelected);
    }

    [Fact]
    public async Task Unticking_a_monitor_removes_only_that_identifier()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        harness.Gateway.AddDisplay(PanelHarness.SecondDisplayId, "Second", PanelHarness.Second);

        await harness.ViewModel.InitializeAsync(CancellationToken.None);
        await harness.ViewModel.Display.Monitors[1].ToggleSelectionCommand.ExecuteAsync();

        Assert.Equal(
            [PanelHarness.DisplayId, PanelHarness.SecondDisplayId],
            await harness.StoredSelectionAsync());

        DisplayRowViewModel second = Assert.Single(
            harness.ViewModel.Display.Monitors,
            row => row.StableId == PanelHarness.SecondDisplayId);

        await second.ToggleSelectionCommand.ExecuteAsync();

        Assert.Equal([PanelHarness.DisplayId], await harness.StoredSelectionAsync());
        Assert.False(Assert.Single(
            harness.ViewModel.Display.Monitors,
            row => row.StableId == PanelHarness.SecondDisplayId).IsSelected);
    }

    [Fact]
    public async Task The_monitor_list_is_there_before_the_display_module_is_switched_on()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        // A user has to be able to see their monitors in order to select one, and selecting one is
        // what makes switching the feature on worth doing.
        Assert.False(harness.ViewModel.Display.IsEnabled);
        Assert.Equal("Display One", Assert.Single(harness.ViewModel.Display.Monitors).Name);
    }

    [Fact]
    public async Task A_preset_value_outside_the_approved_range_is_refused_at_the_field()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        // A gamma of 12 is a dark screen rather than a brighter one, so the field is where it is
        // stopped: the value never reaches the module, the file, or a display.
        harness.ViewModel.Display.High.GammaText = "12";

        Assert.False(harness.ViewModel.Display.High.IsValid);
        Assert.False(harness.ViewModel.Display.High.ApplyCommand.CanExecute(null));

        harness.ViewModel.Display.High.GammaText = "1.55";

        Assert.True(harness.ViewModel.Display.High.IsValid);
        Assert.True(harness.ViewModel.Display.High.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_preset_value_that_is_not_a_number_is_refused_at_the_field()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        // Refused rather than guessed at: a decimal comma read as a thousands separator would set a
        // gamma the user never asked for.
        harness.ViewModel.Display.Medium.ShadowLiftText = "0,05";

        Assert.False(harness.ViewModel.Display.Medium.IsValid);
        Assert.False(harness.ViewModel.Display.Medium.ApplyCommand.CanExecute(null));
        Assert.NotNull(harness.ViewModel.Display.Medium.ValidationMessage);
    }

    [Fact]
    public async Task Applying_preset_values_stores_them_and_writes_the_preset_again()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();
        await harness.ViewModel.Display.ToggleEnabledCommand.ExecuteAsync();

        // The user is looking at the medium preset — F4 — and edits the values it is built from.
        await harness.Display.ApplyPresetAsync(DisplayPresetKind.Medium, CancellationToken.None);
        await AsyncWait.UntilAsync(() => harness.Gateway.WriteLog.Count >= 1, "the preset was never written");
        harness.ViewModel.Refresh();

        int writes = harness.Gateway.WriteLog.Count;

        harness.ViewModel.Display.Medium.GammaText = "1.75";
        harness.ViewModel.Display.Medium.ShadowLiftText = "0.05";
        await harness.ViewModel.Display.Medium.ApplyCommand.ExecuteAsync();

        Assert.Equal(1.75, (await harness.Store.LoadAsync(CancellationToken.None)).Display.Medium.Gamma);

        // The preset in force was rewritten with the values the user just typed, composed against the
        // original captured before anything was written. The write is queued to the module's worker,
        // so it lands after the command has returned.
        await AsyncWait.UntilAsync(
            () => harness.Gateway.WriteLog.Count > writes,
            "the preset in force was never rewritten");

        Assert.True(harness.Gateway.WriteLog.Count > writes);

        GammaRamp expected = GammaRampComposer.Compose(
            PanelHarness.Original,
            new DisplayPresetOptions(Gamma: 1.75, ShadowLift: 0.05, OutputCeiling: 1.0));

        Assert.Equal(expected, harness.Gateway.LastWrittenRamp);
    }

    [Fact]
    public async Task Meter_updates_are_published_at_most_ten_times_a_second()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: true);
        await harness.ViewModel.Audio.ToggleEnabledCommand.ExecuteAsync();

        StubStreamSession session = Assert.Single(harness.Sessions);
        int published = 0;

        harness.ViewModel.Audio.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AudioViewModel.InputPeakDbFs))
            {
                published++;
            }
        };

        // The window is free to tick faster than the meters move. Twenty ticks inside one tenth of a
        // second is one reading, not twenty: a meter that repainted on every tick would be showing
        // the sampling rate rather than the level.
        for (int tick = 0; tick < 20; tick++)
        {
            harness.Clock.Now += TimeSpan.FromMilliseconds(5);
            session.Metrics = harness.Metrics with
            {
                Processor = new AudioProcessorMetrics(
                    InputPeakDbFs: -20.0 - tick,
                    OutputPeakDbFs: -21.0,
                    GainReductionDb: 1.0),
            };

            harness.ViewModel.Sample();
        }

        Assert.Equal(1, published);

        harness.Clock.Now += TimeSpan.FromMilliseconds(100);
        harness.ViewModel.Sample();

        Assert.Equal(2, published);
    }

    [Fact]
    public async Task The_meters_report_what_the_limiter_is_doing()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: true);
        await harness.ViewModel.Audio.ToggleEnabledCommand.ExecuteAsync();

        StubStreamSession session = Assert.Single(harness.Sessions);
        session.Metrics = harness.Metrics with
        {
            Processor = new AudioProcessorMetrics(InputPeakDbFs: -3.5, OutputPeakDbFs: -4.0, GainReductionDb: 6.5),
        };

        harness.ViewModel.Sample();

        Assert.Equal(-3.5, harness.ViewModel.Audio.InputPeakDbFs);
        Assert.Equal(-4.0, harness.ViewModel.Audio.OutputPeakDbFs);
        Assert.Equal(6.5, harness.ViewModel.Audio.GainReductionDb);
    }

    [Fact]
    public async Task An_unusable_audio_route_leaves_every_display_control_usable()
    {
        // The virtual cable is not installed and no endpoint has been picked: the state a user is in
        // before they have set anything up. It must not cost them the display half of the toolkit.
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: false, audioEnabled: true);

        Assert.False(string.IsNullOrWhiteSpace(harness.ViewModel.Audio.DependencyMessage));

        // Nothing was opened, because there is nothing to open it against.
        Assert.Empty(harness.Sessions);

        harness.ViewModel.Refresh();

        Assert.True(harness.ViewModel.Display.ToggleEnabledCommand.CanExecute(null));
        Assert.True(harness.ViewModel.Display.Low.ApplyCommand.CanExecute(null));
        Assert.True(harness.ViewModel.Audio.ToggleEnabledCommand.CanExecute(null));

        await harness.ViewModel.Display.ToggleEnabledCommand.ExecuteAsync();

        // An audio device that is missing is a reason to say so, not a reason to take the rest of the
        // toolkit away.
        Assert.True(harness.Coordinator.IsDisplayEnabled);
        Assert.NotEmpty(harness.ViewModel.Display.Monitors);
    }

    [Fact]
    public async Task Endpoint_pickers_offer_only_endpoints_of_the_kind_they_need()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();
        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        // A recording endpoint offered where a playback endpoint is needed is exactly the mistake the
        // route validator exists to catch, and the picker is where it is prevented.
        Assert.Equal(
            [PanelHarness.VirtualRenderId, PanelHarness.PhysicalRenderId, "surround"],
            [.. harness.ViewModel.Audio.VirtualRenderEndpoints.Select(endpoint => endpoint.Id)]);

        Assert.Equal(
            [PanelHarness.VirtualCaptureId, "microphone"],
            [.. harness.ViewModel.Audio.VirtualCaptureEndpoints.Select(endpoint => endpoint.Id)]);

        // A device the toolkit cannot carry is still offered, because hiding the user's own hardware
        // from them is worse than refusing it: it says so instead.
        AudioEndpointViewModel surround = Assert.Single(
            harness.ViewModel.Audio.VirtualRenderEndpoints,
            endpoint => endpoint.Id == "surround");

        Assert.False(surround.IsSupported);
        Assert.Contains("不支持", surround.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_endpoints_stores_the_route_the_user_built()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();
        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        harness.ViewModel.Audio.SelectedVirtualRender =
            harness.ViewModel.Audio.VirtualRenderEndpoints.Single(endpoint => endpoint.Id == PanelHarness.VirtualRenderId);

        harness.ViewModel.Audio.SelectedVirtualCapture =
            harness.ViewModel.Audio.VirtualCaptureEndpoints.Single(endpoint => endpoint.Id == PanelHarness.VirtualCaptureId);

        harness.ViewModel.Audio.SelectedPhysicalRender =
            harness.ViewModel.Audio.PhysicalRenderEndpoints.Single(endpoint => endpoint.Id == PanelHarness.PhysicalRenderId);

        harness.ViewModel.Audio.ExecutableNameText = "EscapeFromTarkov";

        await harness.ViewModel.Audio.ApplySettingsCommand.ExecuteAsync();

        AudioProfileOptions profile = Assert.Single(
            (await harness.Store.LoadAsync(CancellationToken.None)).Audio.Profiles);

        Assert.Equal(PanelHarness.VirtualRenderId, profile.VirtualRenderEndpointId);
        Assert.Equal(PanelHarness.VirtualCaptureId, profile.VirtualCaptureEndpointId);
        Assert.Equal(PanelHarness.PhysicalRenderId, profile.PhysicalRenderEndpointId);
        Assert.Equal("EscapeFromTarkov", profile.ExecutableName);
    }

    [Fact]
    public async Task Applying_audio_settings_reopens_the_stream_so_the_limiter_takes_effect()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: true);
        await harness.ViewModel.Audio.ToggleEnabledCommand.ExecuteAsync();

        harness.ViewModel.Audio.GainDbText = "15";
        await harness.ViewModel.Audio.ApplySettingsCommand.ExecuteAsync();

        // The limiter is built when the stream is opened, so a gain changed while it was open reaches
        // the audio path only by opening it again. Two sessions, the first stopped.
        Assert.Equal(2, harness.Sessions.Count);
        Assert.Equal(1, harness.Sessions[0].StopCount);
        Assert.True(harness.Sessions[1].IsRunning);
        Assert.Equal(15.0, harness.Sessions[1].LastLimiter!.InputGainDb);
    }

    [Fact]
    public async Task Applying_audio_settings_while_audio_is_off_stores_them_without_starting_it()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        harness.ViewModel.Audio.GainDbText = "15";
        await harness.ViewModel.Audio.ApplySettingsCommand.ExecuteAsync();

        Assert.Empty(harness.Sessions);
        Assert.False(harness.Coordinator.IsAudioEnabled);
        Assert.Equal(15.0, (await harness.Store.LoadAsync(CancellationToken.None)).Audio.Limiter.InputGainDb);
    }

    [Fact]
    public async Task An_audio_limiter_value_outside_the_approved_range_is_refused_at_the_field()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();
        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        harness.ViewModel.Audio.ThresholdDbFsText = "-40";

        Assert.False(harness.ViewModel.Audio.AreSettingsValid);
        Assert.False(harness.ViewModel.Audio.ApplySettingsCommand.CanExecute(null));
        Assert.NotNull(harness.ViewModel.Audio.SettingsMessage);
    }

    [Fact]
    public async Task Bypassing_the_limiter_switches_the_session_rather_than_closing_it()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: true);
        await harness.ViewModel.Audio.ToggleEnabledCommand.ExecuteAsync();

        StubStreamSession session = Assert.Single(harness.Sessions);

        await harness.ViewModel.Audio.ToggleBypassCommand.ExecuteAsync();

        Assert.True(harness.ViewModel.Audio.IsBypassed);
        Assert.Equal([true], session.BypassValues);

        // The stream stayed open: closing and reopening it would drop the audio the user is listening
        // to in order to arrive at the same place.
        Assert.Equal(0, session.StopCount);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task The_panel_reports_whether_the_target_is_playing_through_the_toolkit()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(configuredAudio: true);

        // What the exit path asks before stopping the forwarding. Nothing is open yet, so exiting
        // could not silence anything.
        Assert.False(harness.ViewModel.Audio.IsTargetRunning);
        Assert.False(harness.ViewModel.Audio.IsRouteOpen);

        await harness.ViewModel.Audio.ToggleEnabledCommand.ExecuteAsync();
        harness.ViewModel.Refresh();

        Assert.True(harness.ViewModel.Audio.IsTargetRunning);
        Assert.True(harness.ViewModel.Audio.IsRouteOpen);
    }

    [Fact]
    public async Task A_freshly_opened_panel_is_not_shutting_down()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        // What the window's close handler reads to decide between hiding and exiting, so it has to
        // be false for as long as the toolkit is running.
        Assert.False(harness.ViewModel.IsShuttingDown);
    }

    [Fact]
    public async Task Opening_the_volume_mixer_uses_the_windows_settings_page()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        await harness.ViewModel.Audio.OpenVolumeMixerCommand.ExecuteAsync();

        // The toolkit never changes per-application routing itself, so the panel opens the page where
        // the user does it.
        Assert.Equal(["ms-settings:apps-volume"], harness.OpenedTargets);
    }

    [Fact]
    public async Task The_footer_opens_the_folder_the_log_is_written_to()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        await harness.ViewModel.OpenLogsFolderCommand.ExecuteAsync();

        Assert.Equal([JsonLineLogger.DefaultRoot], harness.OpenedTargets);
    }

    [Fact]
    public async Task A_failure_is_reported_into_the_panel_rather_than_thrown_at_the_binding()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();

        // The panel has no console and no dialog of its own for this: a command that failed has to
        // say so where the user is looking.
        harness.ViewModel.ReportError(new InvalidOperationException("the ramp was refused"));

        Assert.Contains("the ramp was refused", harness.ViewModel.LatestWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_display_rows_report_what_the_module_says_about_each_monitor()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync();
        await harness.ViewModel.Display.ToggleEnabledCommand.ExecuteAsync();
        harness.ViewModel.Refresh();

        DisplayRowViewModel row = Assert.Single(harness.ViewModel.Display.Monitors);

        Assert.Equal("Display One", row.Name);
        Assert.True(row.IsSelected);
        Assert.True(row.IsConnected);

        // F2 is in force after switching on: nothing has been composed yet, so nothing is on the
        // display that the toolkit did not find there.
        Assert.Equal(DisplayPresetKind.Original, harness.ViewModel.Display.CurrentPreset);
        Assert.Contains("F2", harness.ViewModel.Display.CurrentPresetText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_monitor_the_toolkit_cannot_drive_is_reported_rather_than_dropped()
    {
        await using PanelHarness harness = await PanelHarness.CreateAsync(
            options: PanelHarness.DefaultOptions with
            {
                Display = PanelHarness.DefaultOptions.Display with
                {
                    SelectedDisplayIds = [PanelHarness.DisplayId, "unplugged"],
                },
            });

        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        DisplayRowViewModel missing = Assert.Single(
            harness.ViewModel.Display.Monitors,
            row => row.StableId == "unplugged");

        Assert.False(missing.IsConnected);
        Assert.False(string.IsNullOrWhiteSpace(missing.Message));
        Assert.Equal("unplugged", missing.Name);
    }

    // ---------------------------------------------------------------- harness

    private sealed class PanelHarness : IAsyncDisposable
    {
        internal const string DisplayId = "display-1";
        internal const string SecondDisplayId = "display-2";
        internal const string VirtualRenderId = "virtual-render";
        internal const string VirtualCaptureId = "virtual-capture";
        internal const string PhysicalRenderId = "headphones";

        internal static readonly GammaRamp Original = BuildRamp(257);

        internal static readonly GammaRamp Second = BuildRamp(250);

        internal static readonly ToolkitOptions DefaultOptions = CreateOptions(
            new DisplayOptions(
                Enabled: false,
                SelectedDisplayIds: [DisplayId],
                Low: new DisplayPresetOptions(1.15, 0.00, 1.00),
                Medium: new DisplayPresetOptions(1.35, 0.01, 1.00),
                High: new DisplayPresetOptions(1.55, 0.02, 1.00)),
            CreateAudio(enabled: false, configured: true));

        private readonly string _directory;

        private PanelHarness(string directory, ToolkitOptions options)
        {
            _directory = directory;

            Clock = new TestClock();
            Store = new MemoryOptionsStore(options);
            Gateway = FakeDisplayGammaGateway.OneDisplay(Original);
            Catalog = new StubDeviceCatalog();

            Display = new DisplayModule(
                options.Display,
                Gateway,
                new JsonDisplayRecoveryStore(directory, Clock),
                logger: null,
                Clock);

            Audio = new AudioModule(
                options.Audio,
                Catalog,
                new StubProcessMonitor { IsRunning = true },
                () => { StubStreamSession session = new(); Sessions.Add(session); return session; },
                Store,
                logger: null,
                Clock);

            Coordinator = new ToolkitCoordinator(
                Display, Audio, new StubHotkeys(), new StubEvents(), Store, logger: null, Clock);
        }

        /// <summary>Starts the toolkit the way the composition root does, then builds the panel.</summary>
        private async Task StartAsync()
        {
            await Coordinator.StartAsync(CancellationToken.None);

            ViewModel = new MainViewModel(
                Coordinator, Display, Audio, Catalog, OpenedTargets.Add, logger: null, Clock);
        }

        internal TestClock Clock { get; }

        internal MemoryOptionsStore Store { get; }

        internal FakeDisplayGammaGateway Gateway { get; }

        internal StubDeviceCatalog Catalog { get; }

        internal DisplayModule Display { get; }

        internal AudioModule Audio { get; }

        internal ToolkitCoordinator Coordinator { get; }

        /// <summary>Built after the toolkit has started, so it reads the stored configuration.</summary>
        internal MainViewModel ViewModel { get; private set; } = null!;

        internal List<StubStreamSession> Sessions { get; } = [];

        internal List<string> OpenedTargets { get; } = [];

        /// <summary>What a stream reports, so a test can say what the limiter has done.</summary>
        internal AudioStreamMetrics Metrics { get; } = new(
            Underruns: 0,
            Overruns: 0,
            CaptureBufferMilliseconds: 10,
            RenderBufferMilliseconds: 10,
            EstimatedAdditionalLatencyMilliseconds: 20.0,
            Processor: AudioProcessorMetrics.Silent);

        internal static async Task<PanelHarness> CreateAsync(
            ToolkitOptions? options = null,
            bool configuredAudio = false,
            bool audioEnabled = false)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "eft-toolkit-panel-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);

            PanelHarness harness = new(
                directory,
                options ?? DefaultOptions with { Audio = CreateAudio(audioEnabled, configuredAudio) });

            // The toolkit loads its configuration before the panel is built, because the panel shows
            // what is configured rather than what the panel itself would default to.
            await harness.StartAsync();

            return harness;
        }

        /// <summary>The selection the store holds, which is what the next launch reads.</summary>
        internal async Task<IReadOnlyList<string>> StoredSelectionAsync() =>
            (await Store.LoadAsync(CancellationToken.None)).Display.SelectedDisplayIds;

        public async ValueTask DisposeAsync()
        {
            await Coordinator.ShutdownAsync(CancellationToken.None);
            await Display.DisposeAsync();
            await Audio.DisposeAsync();
            await Catalog.DisposeAsync();

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over.
            }
        }

        private static ToolkitOptions CreateOptions(DisplayOptions display, AudioOptions audio) =>
            ToolkitOptions.CreateDefault() with { Display = display, Audio = audio };

        private static AudioOptions CreateAudio(bool enabled, bool configured)
        {
            AudioProfileOptions profile = Assert.Single(ToolkitOptions.CreateDefault().Audio.Profiles);

            return ToolkitOptions.CreateDefault().Audio with
            {
                Enabled = enabled,
                Profiles = configured
                    ? [profile with
                    {
                        VirtualRenderEndpointId = VirtualRenderId,
                        VirtualCaptureEndpointId = VirtualCaptureId,
                        PhysicalRenderEndpointId = PhysicalRenderId,
                    }]
                    : [profile],
            };
        }

        private static GammaRamp BuildRamp(ushort step)
        {
            ushort[] channel = new ushort[GammaRamp.ChannelLength];

            for (int index = 0; index < channel.Length; index++)
            {
                channel[index] = (ushort)(index * step);
            }

            return new GammaRamp(channel, channel, channel);
        }
    }

    /// <summary>A clock a test moves by hand, so the meter rate is asserted rather than waited out.</summary>
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        internal DateTimeOffset Now { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemoryOptionsStore(ToolkitOptions options) : IOptionsStore
    {
        private ToolkitOptions _options = options;

        public Task<ToolkitOptions> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_options);

        public Task SaveAsync(ToolkitOptions options, CancellationToken cancellationToken)
        {
            _options = options;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDeviceCatalog : IAudioDeviceCatalog
    {
        public List<AudioEndpointDescriptor> Endpoints { get; } =
        [
            new(PanelHarness.VirtualRenderId, "CABLE Input (VB-Audio Virtual Cable)", AudioDataFlow.Render, true, 2, 48_000),
            new(PanelHarness.VirtualCaptureId, "CABLE Output (VB-Audio Virtual Cable)", AudioDataFlow.Capture, true, 2, 48_000),
            new(PanelHarness.PhysicalRenderId, "Headphones", AudioDataFlow.Render, true, 2, 48_000),
            new("microphone", "Microphone", AudioDataFlow.Capture, true, 2, 48_000),
            new("surround", "Speakers (5.1)", AudioDataFlow.Render, true, 6, 48_000),
        ];

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<AudioEndpointDescriptor>> GetEndpointsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioEndpointDescriptor>>([.. Endpoints]);

        public Task<IReadOnlySet<int>> GetActiveProcessIdsAsync(
            string renderEndpointId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public bool IsRunning { get; set; }

        public IReadOnlySet<int> ProcessIds => IsRunning ? new HashSet<int> { 42 } : new HashSet<int>();

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task StartAsync(string executableName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Whether the application is running is a fact about the machine. Stopping the watch
            // does not stop the application, and a stub that conflated the two would make a module
            // that stops watching look like a game that exited.
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubStreamSession : IAudioStreamSession
    {
        public bool IsRunning { get; private set; }

        public int StopCount { get; private set; }

        /// <summary>The limiter the session was last opened with. Only known at that moment.</summary>
        public AudioLimiterOptions? LastLimiter { get; private set; }

        public List<bool> BypassValues { get; } = [];

        public AudioStreamMetrics Metrics { get; set; } = AudioStreamMetrics.Idle;

        public event EventHandler<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task StartAsync(AudioRoute route, AudioLimiterOptions limiter, CancellationToken cancellationToken)
        {
            LastLimiter = limiter;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task SetBypassAsync(bool bypass, CancellationToken cancellationToken)
        {
            BypassValues.Add(bypass);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHotkeys : IHotkeyService
    {
        public IReadOnlyDictionary<DisplayPresetKind, bool> Registrations { get; } =
            new Dictionary<DisplayPresetKind, bool>();

        public event EventHandler<DisplayPresetKind>? PresetRequested
        {
            add { }
            remove { }
        }

        public Task RegisterAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UnregisterAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubEvents : IPlatformEventSource
    {
        public event EventHandler? DisplayEnvironmentChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
