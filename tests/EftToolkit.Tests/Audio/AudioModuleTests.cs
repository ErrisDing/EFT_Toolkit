using EftToolkit.Audio;
using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Processes;
using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Modules;
using EftToolkit.Tests.TestSupport;

namespace EftToolkit.Tests.Audio;

/// <summary>
/// The audio module's job is to decide, and to keep deciding as the machine changes under it. These
/// tests drive that with fakes for everything outside the module: no endpoint is opened, no process
/// is watched, and nothing waits longer than it takes to prove the debounce fired.
/// </summary>
public class AudioModuleTests
{
    private const string VirtualRenderId = "virtual-render";
    private const string VirtualCaptureId = "virtual-capture";
    private const string PhysicalRenderId = "headphones";
    private const string ProfileId = "tarkov";
    private const string ExecutableName = "EscapeFromTarkov";

    /// <summary>
    /// Long enough that a test can observe the debounce coalescing two events, short enough that the
    /// suite does not spend half a second per case waiting for one to elapse.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(30);

    private readonly FakeDeviceCatalog _catalog = new();
    private readonly FakeProcessMonitor _monitor = new();
    private readonly List<FakeStreamSession> _sessions = [];
    private readonly RecordingLogger _logger = new();

    /// <summary>When set, the next session the module asks for will fail to open.</summary>
    private Exception? _startFailure;

    public AudioModuleTests() => _catalog.Endpoints = DefaultEndpoints();

    private static List<AudioEndpointDescriptor> DefaultEndpoints() =>
    [
        Endpoint(VirtualRenderId, AudioDataFlow.Render),
        Endpoint(VirtualCaptureId, AudioDataFlow.Capture),
        Endpoint(PhysicalRenderId, AudioDataFlow.Render),
    ];

    private AudioModule CreateModule(AudioOptions? options = null) =>
        new(
            options ?? Options(),
            _catalog,
            _monitor,
            CreateSession,
            optionsStore: null,
            _logger,
            timeProvider: null,
            Debounce);

    private IAudioStreamSession CreateSession()
    {
        FakeStreamSession session = new() { StartFailure = _startFailure, Metrics = FastMetrics() };
        _sessions.Add(session);
        return session;
    }

    /// <summary>Drops a device from the machine and tells the module it changed.</summary>
    private void RemoveEndpoint(string id)
    {
        _catalog.Endpoints = [.. _catalog.Endpoints.Where(endpoint => endpoint.Id != id)];
        _catalog.RaiseDevicesChanged();
    }

    private void RestoreEndpoint(string id, AudioDataFlow flow, int channels = 2, int sampleRate = 48_000)
    {
        _catalog.Endpoints = [.. _catalog.Endpoints.Where(endpoint => endpoint.Id != id), Endpoint(id, flow, channels, sampleRate)];
        _catalog.RaiseDevicesChanged();
    }

    private static AudioOptions Options() => new(
        Enabled: true,
        ActiveProfileId: ProfileId,
        Limiter: Limiter(),
        Profiles: [Profile()]);

    private static AudioProfileOptions Profile(
        string? virtualRender = VirtualRenderId,
        string? virtualCapture = VirtualCaptureId,
        string? physicalRender = PhysicalRenderId) =>
        new(ProfileId, "Escape from Tarkov", ExecutableName, virtualRender, virtualCapture, physicalRender);

    private static AudioLimiterOptions Limiter() => new(
        InputGainDb: 0.0,
        ThresholdDbFs: -3.0,
        Ratio: 4.0,
        KneeDb: 6.0,
        LookAheadMs: 1.0,
        AttackMs: 5.0,
        ReleaseMs: 120.0,
        CeilingDbFs: -0.5);

    private static AudioEndpointDescriptor Endpoint(
        string id,
        AudioDataFlow flow,
        int channels = 2,
        int sampleRate = 48_000) =>
        new(id, id, flow, true, channels, sampleRate);

    /// <summary>Metrics with the toolkit's own delay kept under the warning threshold.</summary>
    private static AudioStreamMetrics FastMetrics(double additionalLatencyMs = 41.0) => new(
        Underruns: 0,
        Overruns: 0,
        CaptureBufferMilliseconds: 20,
        RenderBufferMilliseconds: 20,
        EstimatedAdditionalLatencyMilliseconds: additionalLatencyMs,
        Processor: default);

    // ---------------------------------------------------------------- enabling

    [Fact]
    public async Task Enable_opens_one_stream_when_the_route_is_complete_and_the_game_is_running()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Active, module.Status.State);
        Assert.True(module.IsRouteOpen);
        FakeStreamSession session = Assert.Single(_sessions);
        Assert.Equal(1, session.StartCount);
        Assert.Equal(VirtualCaptureId, session.Route?.VirtualCaptureEndpointId);
        Assert.Equal(PhysicalRenderId, session.Route?.PhysicalRenderEndpointId);
        Assert.Equal(ExecutableName, session.Route?.ExecutableName);
        Assert.NotNull(module.Metrics);
    }

    [Fact]
    public async Task Enable_watches_the_executable_the_active_profile_names()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ExecutableName, _monitor.WatchedName);
    }

    [Fact]
    public async Task Enable_is_idempotent()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);
        await module.EnableAsync(CancellationToken.None);

        // A second enable is a no-op rather than a second stream, which is what would happen if the
        // panel's switch were wired to something that fired twice.
        FakeStreamSession session = Assert.Single(_sessions);
        Assert.Equal(1, session.StartCount);
    }

    // ---------------------------------------------------------------- refusing to open

    [Theory]
    [InlineData(null, VirtualCaptureId, PhysicalRenderId, AudioModuleErrorCodes.MissingVirtualRender)]
    [InlineData(VirtualRenderId, null, PhysicalRenderId, AudioModuleErrorCodes.MissingVirtualCapture)]
    [InlineData(VirtualRenderId, VirtualCaptureId, null, AudioModuleErrorCodes.MissingPhysicalRender)]
    [InlineData("not-a-device", VirtualCaptureId, PhysicalRenderId, AudioModuleErrorCodes.MissingVirtualRender)]
    [InlineData(VirtualRenderId, "not-a-device", PhysicalRenderId, AudioModuleErrorCodes.MissingVirtualCapture)]
    [InlineData(VirtualRenderId, VirtualCaptureId, "not-a-device", AudioModuleErrorCodes.MissingPhysicalRender)]
    public async Task A_missing_endpoint_faults_the_module_without_opening_a_stream(
        string? virtualRender,
        string? virtualCapture,
        string? physicalRender,
        string expectedCode)
    {
        _monitor.Running = true;

        await using AudioModule module = CreateModule(
            Options() with
            {
                Profiles = [Profile(virtualRender, virtualCapture, physicalRender)],
            });

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(expectedCode, module.Status.ErrorCode);
        Assert.Empty(_sessions);
        Assert.False(module.IsRouteOpen);
    }

    [Fact]
    public async Task An_endpoint_of_the_wrong_kind_is_reported_as_the_endpoint_that_is_missing()
    {
        // The physical output is configured to a capture endpoint. Validation refuses it, and what
        // the operator needs to know is which of the three pickers is wrong.
        _catalog.Endpoints =
        [
            Endpoint(VirtualRenderId, AudioDataFlow.Render),
            Endpoint(VirtualCaptureId, AudioDataFlow.Capture),
            Endpoint(PhysicalRenderId, AudioDataFlow.Capture),
        ];

        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.MissingPhysicalRender, module.Status.ErrorCode);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task An_endpoint_that_is_not_stereo_faults_with_an_unsupported_format()
    {
        _catalog.Endpoints =
        [
            Endpoint(VirtualRenderId, AudioDataFlow.Render),
            Endpoint(VirtualCaptureId, AudioDataFlow.Capture),
            Endpoint(PhysicalRenderId, AudioDataFlow.Render, channels: 6),
        ];

        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.UnsupportedFormat, module.Status.ErrorCode);
        Assert.Empty(_sessions);
    }

    [Theory]
    [InlineData(22_050)]
    [InlineData(96_000)]
    public async Task An_endpoint_at_an_unsupported_sample_rate_faults_with_an_unsupported_format(int sampleRate)
    {
        _catalog.Endpoints =
        [
            Endpoint(VirtualRenderId, AudioDataFlow.Render),
            Endpoint(VirtualCaptureId, AudioDataFlow.Capture),
            Endpoint(PhysicalRenderId, AudioDataFlow.Render, sampleRate: sampleRate),
        ];

        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(AudioModuleErrorCodes.UnsupportedFormat, module.Status.ErrorCode);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task A_virtual_and_physical_render_endpoint_that_are_the_same_device_faults()
    {
        _catalog.Endpoints =
        [
            Endpoint(VirtualRenderId, AudioDataFlow.Render),
            Endpoint(VirtualCaptureId, AudioDataFlow.Capture),
        ];

        _monitor.Running = true;
        await using AudioModule module = CreateModule(
            Options() with { Profiles = [Profile(physicalRender: VirtualRenderId)] });

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.InvalidRoute, module.Status.ErrorCode);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task An_executable_name_that_carries_a_directory_faults_as_an_invalid_route()
    {
        _monitor.Running = true;

        await using AudioModule module = CreateModule(
            Options() with
            {
                Profiles =
                [
                    new AudioProfileOptions(
                        ProfileId,
                        "Escape from Tarkov",
                        @"C:\games\eft.exe",
                        VirtualRenderId,
                        VirtualCaptureId,
                        PhysicalRenderId),
                ],
            });

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.InvalidRoute, module.Status.ErrorCode);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task A_configuration_with_no_matching_profile_faults()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule(Options() with { ActiveProfileId = "something-else" });

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.MissingProfile, module.Status.ErrorCode);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task A_target_that_is_not_running_degrades_without_opening_a_stream()
    {
        _monitor.Running = false;
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Degraded, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.TargetNotRunning, module.Status.ErrorCode);
        Assert.False(module.IsTargetRunning);
        Assert.False(module.IsRouteOpen);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task A_configuration_problem_outranks_a_game_that_is_not_running()
    {
        // Waiting for the game would never fix a route that cannot be opened, so the fault is the
        // one worth reporting.
        _monitor.Running = false;

        await using AudioModule module = CreateModule(
            Options() with { Profiles = [Profile(virtualCapture: null)] });

        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.MissingVirtualCapture, module.Status.ErrorCode);
    }

    [Fact]
    public async Task A_stream_that_will_not_open_is_reported_as_a_shared_mode_failure()
    {
        _monitor.Running = true;
        _startFailure = new UnauthorizedAccessException("the endpoint is in use");

        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Faulted, module.Status.State);
        Assert.Equal(AudioModuleErrorCodes.SharedModeOpenFailed, module.Status.ErrorCode);
        Assert.False(module.IsRouteOpen);

        // The half-opened session has to be released rather than left holding the endpoint.
        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    // ---------------------------------------------------------------- following the machine

    [Fact]
    public async Task The_game_starting_opens_the_stream()
    {
        _monitor.Running = false;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);
        Assert.Empty(_sessions);

        _monitor.Running = true;
        _monitor.RaiseChanged();

        await AsyncWait.UntilAsync(() => _sessions.Count == 1, "the game was noticed");

        Assert.Equal(ModuleState.Active, module.Status.State);
        Assert.True(module.IsRouteOpen);
    }

    [Fact]
    public async Task The_game_exiting_stops_and_releases_the_stream()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        _monitor.Running = false;
        _monitor.RaiseChanged();

        await AsyncWait.UntilAsync(
            () => module.Status.State == ModuleState.Degraded,
            "the game was noticed leaving");

        Assert.Equal(AudioModuleErrorCodes.TargetNotRunning, module.Status.ErrorCode);
        Assert.False(module.IsRouteOpen);
        Assert.Equal(1, _sessions[0].StopCount);
        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    [Fact]
    public async Task An_endpoint_disappearing_faults_the_module_and_releases_the_stream()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        RemoveEndpoint(PhysicalRenderId);

        await AsyncWait.UntilAsync(
            () => module.Status.State == ModuleState.Faulted,
            "the missing output was noticed");

        Assert.Equal(AudioModuleErrorCodes.MissingPhysicalRender, module.Status.ErrorCode);
        Assert.False(module.IsRouteOpen);
        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    [Fact]
    public async Task An_endpoint_coming_back_restores_the_session()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        RemoveEndpoint(PhysicalRenderId);
        await AsyncWait.UntilAsync(() => !module.IsRouteOpen, "the session was released");

        RestoreEndpoint(PhysicalRenderId, AudioDataFlow.Render);

        await AsyncWait.UntilAsync(() => module.Status.State == ModuleState.Active, "the output came back");

        Assert.True(module.IsRouteOpen);
        Assert.Equal(2, _sessions.Count);
        Assert.Equal(1, _sessions[1].StartCount);
    }

    [Fact]
    public async Task Several_device_notifications_in_a_row_produce_one_re_evaluation()
    {
        // A cable being re-seated reports itself several times over. Each of those must not become
        // its own stream open and close.
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        RemoveEndpoint(PhysicalRenderId);
        await AsyncWait.UntilAsync(() => !module.IsRouteOpen, "the stream was released");

        // The cable being re-seated reports itself repeatedly. All three land inside one debounce
        // window, so they have to become one re-evaluation rather than three.
        RestoreEndpoint(PhysicalRenderId, AudioDataFlow.Render);
        _catalog.RaiseDevicesChanged();
        _catalog.RaiseDevicesChanged();

        await AsyncWait.UntilAsync(() => module.Status.State == ModuleState.Active, "the output came back");
        await Task.Delay(Debounce * 4);

        Assert.Equal(2, _sessions.Count);
    }

    [Fact]
    public async Task A_stream_fault_faults_the_module_and_releases_the_stream()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        _sessions[0].RaiseFault(new InvalidOperationException("the cable was unplugged"));

        await AsyncWait.UntilAsync(
            () => module.Status.State == ModuleState.Faulted,
            "the stream fault was noticed");

        Assert.Equal(AudioModuleErrorCodes.StreamFaulted, module.Status.ErrorCode);
        Assert.False(module.IsRouteOpen);
        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    [Fact]
    public async Task A_fault_raised_after_disable_changes_nothing()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        FakeStreamSession session = _sessions[0];
        await module.DisableAsync(CancellationToken.None);

        session.RaiseFault(new InvalidOperationException("late"));
        await Task.Delay(Debounce * 4);

        Assert.Equal(ModuleState.Disabled, module.Status.State);
    }

    // ---------------------------------------------------------------- bypass

    [Fact]
    public async Task Bypass_keeps_the_stream_open()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await module.SetBypassAsync(true, CancellationToken.None);

        Assert.Equal(ModuleState.Bypass, module.Status.State);
        Assert.True(module.IsRouteOpen);
        Assert.True(_sessions[0].Bypass);
        Assert.Equal(0, _sessions[0].StopCount);
    }

    [Fact]
    public async Task Leaving_bypass_returns_to_active()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);
        await module.SetBypassAsync(true, CancellationToken.None);

        await module.SetBypassAsync(false, CancellationToken.None);

        Assert.Equal(ModuleState.Active, module.Status.State);
        Assert.False(_sessions[0].Bypass);
    }

    [Fact]
    public async Task Bypass_set_before_the_stream_exists_is_remembered()
    {
        _monitor.Running = false;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await module.SetBypassAsync(true, CancellationToken.None);

        _monitor.Running = true;
        _monitor.RaiseChanged();

        // The wait is for what is asserted rather than for the session alone: the stream is created
        // and started before the status that describes it is published, so a wait on the session
        // count can return while the module is still reporting the state it was in before.
        await AsyncWait.UntilAsync(
            () => _sessions.Count == 1 && module.Status.State == ModuleState.Bypass,
            "the game was noticed and the remembered bypass was applied to the new stream");

        Assert.True(_sessions[0].Bypass);
        Assert.Equal(ModuleState.Bypass, module.Status.State);
    }

    [Fact]
    public async Task A_reopened_stream_keeps_the_bypass_the_operator_chose()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);
        await module.SetBypassAsync(true, CancellationToken.None);

        _monitor.Running = false;
        _monitor.RaiseChanged();
        await AsyncWait.UntilAsync(() => !module.IsRouteOpen, "the game was noticed leaving");

        _monitor.Running = true;
        _monitor.RaiseChanged();

        // As above: the reopened stream exists before the state that describes it does.
        await AsyncWait.UntilAsync(
            () => _sessions.Count == 2 && module.Status.State == ModuleState.Bypass,
            "the game came back and the bypass was applied to the reopened stream");

        Assert.True(_sessions[1].Bypass);
        Assert.Equal(ModuleState.Bypass, module.Status.State);
    }

    // ---------------------------------------------------------------- warnings

    [Fact]
    public async Task A_second_application_on_the_routed_endpoint_warns_without_stopping_the_audio()
    {
        _monitor.Running = true;
        _monitor.ProcessIds = new HashSet<int> { 4242 };
        _catalog.SessionProcessIds = new HashSet<int> { 4242, 9999 };
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.True(module.HasUnrelatedSessions);
        Assert.Equal(AudioModuleWarningCodes.UnrelatedSessions, module.Detail.WarningCode);
        Assert.Equal(ModuleState.Active, module.Status.State);
        Assert.True(module.IsRouteOpen);
    }

    [Fact]
    public async Task The_games_own_session_is_not_an_unrelated_one()
    {
        _monitor.Running = true;
        _monitor.ProcessIds = new HashSet<int> { 4242 };
        _catalog.SessionProcessIds = new HashSet<int> { 4242 };
        await using AudioModule module = CreateModule();

        await module.EnableAsync(CancellationToken.None);

        Assert.False(module.HasUnrelatedSessions);
        Assert.Null(module.Detail.WarningCode);
    }

    [Fact]
    public async Task Latency_above_the_target_warns_with_the_measured_value()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        _sessions[0].Metrics = FastMetrics(additionalLatencyMs: 78.5);

        AudioModuleStatus detail = module.Detail;

        Assert.Equal(AudioModuleWarningCodes.LatencyTargetExceeded, detail.WarningCode);
        Assert.Contains("78.5", detail.WarningMessage ?? string.Empty, StringComparison.Ordinal);

        // A warning is a warning: the audio keeps playing.
        Assert.Equal(ModuleState.Active, module.Status.State);
        Assert.True(module.IsRouteOpen);
    }

    [Fact]
    public async Task Latency_at_the_target_raises_no_warning()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        _sessions[0].Metrics = FastMetrics(additionalLatencyMs: 50.0);

        Assert.Null(module.Detail.WarningCode);
    }

    [Fact]
    public async Task An_idle_module_reports_no_warning_and_no_metrics()
    {
        await using AudioModule module = CreateModule();

        Assert.Equal(ModuleState.Disabled, module.Status.State);
        Assert.Null(module.Metrics);
        Assert.Null(module.Detail.WarningCode);
        Assert.False(module.IsRouteOpen);
        Assert.False(module.IsTargetRunning);
    }

    // ---------------------------------------------------------------- disabling and disposal

    [Fact]
    public async Task Disable_releases_the_stream_and_returns_to_disabled()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await module.DisableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Disabled, module.Status.State);
        Assert.False(module.IsRouteOpen);
        Assert.Equal(1, _sessions[0].StopCount);
        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    [Fact]
    public async Task Disable_before_enable_is_safe()
    {
        await using AudioModule module = CreateModule();

        await module.DisableAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Disabled, module.Status.State);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task Disable_is_idempotent()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await module.DisableAsync(CancellationToken.None);
        await module.DisableAsync(CancellationToken.None);

        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    [Fact]
    public async Task Events_after_disable_open_nothing_and_raise_nothing()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        List<ModuleStatus> observed = [];
        module.StatusChanged += (_, status) => observed.Add(status);

        await module.DisableAsync(CancellationToken.None);
        int settled = observed.Count;

        _monitor.Running = false;
        _monitor.RaiseChanged();
        _catalog.RaiseDevicesChanged();

        await Task.Delay(Debounce * 4);

        Assert.Equal(settled, observed.Count);
        Assert.Single(_sessions);
        Assert.Equal(ModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Disposing_the_module_releases_the_stream_exactly_once()
    {
        _monitor.Running = true;

        AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await module.DisposeAsync();
        await module.DisposeAsync();

        Assert.Equal(1, _sessions[0].DisposeCount);
        Assert.Equal(ModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task A_status_change_is_raised_after_the_new_state_is_published()
    {
        // A handler that read the module while it still reported the previous state would show the
        // operator a status one transition behind.
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        List<(ModuleStatus Raised, ModuleState Published)> observed = [];
        module.StatusChanged += (_, status) => observed.Add((status, module.Status.State));

        await module.EnableAsync(CancellationToken.None);

        Assert.NotEmpty(observed);
        Assert.All(observed, pair => Assert.Equal(pair.Raised.State, pair.Published));
        Assert.Equal(ModuleState.Active, observed[^1].Raised.State);
    }

    [Fact]
    public async Task A_status_handler_that_throws_does_not_break_the_module()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        module.StatusChanged += (_, _) => throw new InvalidOperationException("a panel bug");

        await module.EnableAsync(CancellationToken.None);

        Assert.True(module.IsRouteOpen);
        Assert.True(_logger.Entries.Count > 0);
    }

    // ---------------------------------------------------------------- retry

    [Fact]
    public async Task Retry_re_evaluates_immediately()
    {
        _monitor.Running = false;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);
        Assert.Empty(_sessions);

        _monitor.Running = true;
        await module.RetryAsync(CancellationToken.None);

        Assert.Single(_sessions);
        Assert.Equal(ModuleState.Active, module.Status.State);
    }

    [Fact]
    public async Task Retry_before_enable_opens_nothing()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await module.RetryAsync(CancellationToken.None);

        Assert.Empty(_sessions);
        Assert.Equal(ModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Retry_does_not_open_a_second_stream_when_one_is_already_running()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await module.RetryAsync(CancellationToken.None);

        FakeStreamSession session = Assert.Single(_sessions);
        Assert.Equal(1, session.StartCount);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task Concurrent_enables_open_at_most_one_stream()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await Task.WhenAll(
            module.EnableAsync(CancellationToken.None),
            module.EnableAsync(CancellationToken.None),
            module.EnableAsync(CancellationToken.None));

        FakeStreamSession session = Assert.Single(_sessions);
        Assert.Equal(1, session.StartCount);
    }

    [Fact]
    public async Task Concurrent_disable_calls_release_the_stream_once()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();
        await module.EnableAsync(CancellationToken.None);

        await Task.WhenAll(
            module.DisableAsync(CancellationToken.None),
            module.DisableAsync(CancellationToken.None),
            module.DisableAsync(CancellationToken.None));

        Assert.Equal(ModuleState.Disabled, module.Status.State);
        Assert.Equal(1, _sessions[0].DisposeCount);
    }

    [Fact]
    public async Task Concurrent_enable_and_disable_leave_the_reported_state_matching_the_open_stream()
    {
        _monitor.Running = true;
        await using AudioModule module = CreateModule();

        await Task.WhenAll(
            module.EnableAsync(CancellationToken.None),
            module.DisableAsync(CancellationToken.None),
            module.EnableAsync(CancellationToken.None),
            module.RetryAsync(CancellationToken.None));

        // The interleaving is not fixed, so what is asserted is the invariant that has to hold for
        // every one of them: the reported state and the open stream agree, no session was opened or
        // released twice, and the module is not holding a stream it has forgotten about.
        bool open = module.IsRouteOpen;

        Assert.Equal(open, module.Status.State is ModuleState.Active or ModuleState.Bypass);
        Assert.All(_sessions, session => Assert.True(session.StartCount <= 1));
        Assert.All(_sessions, session => Assert.True(session.DisposeCount <= 1));
        Assert.Equal(open ? 1 : 0, _sessions.Count(session => session.IsRunning));
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeDeviceCatalog : IAudioDeviceCatalog
    {
        internal IReadOnlyList<AudioEndpointDescriptor> Endpoints { get; set; } = [];

        internal IReadOnlySet<int> SessionProcessIds { get; set; } = new HashSet<int>();

        public event EventHandler? DevicesChanged;

        public Task<IReadOnlyList<AudioEndpointDescriptor>> GetEndpointsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(Endpoints);
        }

        public Task<IReadOnlySet<int>> GetActiveProcessIdsAsync(
            string renderEndpointId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(SessionProcessIds);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeProcessMonitor : IProcessMonitor
    {
        internal bool Running { get; set; }

        internal string? WatchedName { get; private set; }

        internal IReadOnlySet<int> ProcessIds { get; set; } = new HashSet<int>();

        internal int StartCount { get; private set; }

        public event EventHandler? Changed;

        public bool IsRunning => Running;

        IReadOnlySet<int> IProcessMonitor.ProcessIds => ProcessIds;

        public Task StartAsync(string executableName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WatchedName = executableName;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeStreamSession : IAudioStreamSession
    {
        internal int StartCount { get; private set; }

        internal int StopCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal bool Bypass { get; private set; }

        public bool IsRunning { get; private set; }

        internal bool IsStopped { get; private set; }

        internal AudioRoute? Route { get; private set; }

        internal Exception? StartFailure { get; init; }

        public AudioStreamMetrics Metrics { get; set; } = FastMetrics();

        public event EventHandler<Exception>? Faulted;

        public Task StartAsync(
            AudioRoute route,
            AudioLimiterOptions limiter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (StartFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            Route = route;
            StartCount++;
            IsRunning = true;
            IsStopped = false;

            return Task.CompletedTask;
        }

        public Task SetBypassAsync(bool bypass, CancellationToken cancellationToken)
        {
            Bypass = bypass;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            IsRunning = false;
            IsStopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsRunning = false;
            IsStopped = true;
            return ValueTask.CompletedTask;
        }

        internal void RaiseFault(Exception exception) => Faulted?.Invoke(this, exception);
    }
}
