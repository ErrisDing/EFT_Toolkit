using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EftToolkit.App.Commands;
using EftToolkit.Audio;
using EftToolkit.Audio.Devices;
using EftToolkit.Audio.Dsp;
using EftToolkit.Audio.Routing;
using EftToolkit.Audio.Streaming;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Core.Modules;

namespace EftToolkit.App.ViewModels;

/// <summary>One entry in the profile picker.</summary>
public sealed record AudioProfileChoice(string Id, string DisplayName);

/// <summary>
/// The audio half of the panel: which devices the route is built from, what the limiter is set to,
/// and what the stream is doing.
/// </summary>
/// <remarks>
/// <para>
/// The module reads the stored configuration at every transition, so the panel's way of making an
/// edit take effect is a file write — the coordinator's — and, for the values the limiter is built
/// from, a transition that opens the stream again. A gain changed while a stream is open is heard
/// only when that stream is replaced, and the panel says so rather than pretending otherwise.
/// </para>
/// <para>
/// Nothing here disables anything on the display side. An audio device that is missing is a reason
/// to say so, not a reason to take away a feature that has nothing to do with it.
/// </para>
/// <para>
/// The meters are sampled by the window's timer rather than pushed by the module, and
/// <see cref="Sample"/> publishes at most once per <see cref="MeterInterval"/>. A meter that
/// repainted on every tick would be showing the sampling rate rather than the level.
/// </para>
/// </remarks>
public sealed class AudioViewModel : ObservableObject
{
    /// <summary>Where the user does the routing the toolkit refuses to do for them.</summary>
    public const string VolumeMixerUri = "ms-settings:apps-volume";

    /// <summary>What applying an audio setting does and does not do.</summary>
    public const string RestartNote =
        "增益、阈值与输出上限在音频流打开时确定，点「应用」会重新打开音频流使其立即生效（会有一次短暂中断）。";

    /// <summary>
    /// How often the meters may change. Ten times a second is faster than a peak meter needs to
    /// move, and slow enough that the numbers are readable while they move.
    /// </summary>
    public static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The level at and below which nothing is being measured.</summary>
    private static readonly double SilentDb = AudioProcessorMetrics.Silent.InputPeakDbFs;

    private readonly AudioModule _module;
    private readonly ToolkitCoordinator _coordinator;
    private readonly IAudioDeviceCatalog _catalog;
    private readonly Action<string> _open;
    private readonly TimeProvider _timeProvider;

    private readonly NumericField _gain;
    private readonly NumericField _thresholdDbFs;
    private readonly NumericField _ceilingDbFs;
    private readonly NumericField[] _limiterFields;

    private DateTimeOffset? _lastMeterPublish;

    private double _inputPeakDbFs = SilentDb;
    private double _outputPeakDbFs = SilentDb;
    private double _gainReductionDb;

    private AudioStreamMetrics? _metrics;
    private bool _isBypassed;
    private string _executableNameText = string.Empty;
    private AudioProfileChoice? _selectedProfile;
    private AudioEndpointViewModel? _selectedVirtualRender;
    private AudioEndpointViewModel? _selectedVirtualCapture;
    private AudioEndpointViewModel? _selectedPhysicalRender;

    public AudioViewModel(
        AudioModule module,
        ToolkitCoordinator coordinator,
        IAudioDeviceCatalog catalog,
        Action<string> open,
        Action<Exception> onError,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(onError);

        _module = module;
        _coordinator = coordinator;
        _catalog = catalog;
        _open = open;
        _timeProvider = timeProvider ?? TimeProvider.System;

        AudioLimiterOptions limiter = coordinator.Options.Audio.Limiter;

        _gain = new NumericField(
            "增益",
            limiter.InputGainDb,
            OptionsValidator.InputGainDbMinimum,
            OptionsValidator.InputGainDbMaximum);

        _thresholdDbFs = new NumericField(
            "阈值",
            limiter.ThresholdDbFs,
            OptionsValidator.ThresholdDbFsMinimum,
            OptionsValidator.ThresholdDbFsMaximum);

        _ceilingDbFs = new NumericField(
            "输出上限",
            limiter.CeilingDbFs,
            OptionsValidator.CeilingDbFsMinimum,
            OptionsValidator.CeilingDbFsMaximum);

        _limiterFields = [_gain, _thresholdDbFs, _ceilingDbFs];

        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync, onError);
        ToggleBypassCommand = new AsyncRelayCommand(ToggleBypassAsync, onError);
        RetryCommand = new AsyncRelayCommand(RetryAsync, onError);
        ApplySettingsCommand = new AsyncRelayCommand(ApplySettingsAsync, onError, () => AreSettingsValid);
        OpenVolumeMixerCommand = new AsyncRelayCommand(OpenVolumeMixerAsync, onError);

        SeedProfiles(coordinator.Options.Audio);

        foreach (NumericField field in _limiterFields)
        {
            field.Changed += OnSettingsFieldChanged;
        }
    }

    public ObservableCollection<AudioProfileChoice> Profiles { get; } = [];

    /// <summary>Playback endpoints, which is what the game's audio leaves through.</summary>
    public ObservableCollection<AudioEndpointViewModel> VirtualRenderEndpoints { get; } = [];

    /// <summary>Recording endpoints, which is where the virtual cable's audio comes back.</summary>
    public ObservableCollection<AudioEndpointViewModel> VirtualCaptureEndpoints { get; } = [];

    /// <summary>Playback endpoints on real hardware, which is where the audio ends up.</summary>
    public ObservableCollection<AudioEndpointViewModel> PhysicalRenderEndpoints { get; } = [];

    public AsyncRelayCommand ToggleEnabledCommand { get; }

    public AsyncRelayCommand ToggleBypassCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public AsyncRelayCommand ApplySettingsCommand { get; }

    public AsyncRelayCommand OpenVolumeMixerCommand { get; }

    public bool IsEnabled => _coordinator.IsAudioEnabled;

    /// <summary>
    /// Whether the limiter is passing audio through unchanged. Kept as a preference: turning bypass
    /// on while nothing is routed means it applies when something is.
    /// </summary>
    public bool IsBypassed => _isBypassed;

    public string GainDbText
    {
        get => _gain.Text;
        set => _gain.Text = value;
    }

    public string ThresholdDbFsText
    {
        get => _thresholdDbFs.Text;
        set => _thresholdDbFs.Text = value;
    }

    public string CeilingDbFsText
    {
        get => _ceilingDbFs.Text;
        set => _ceilingDbFs.Text = value;
    }

    /// <summary>The executable whose audio is routed, as a plain file name.</summary>
    public string ExecutableNameText
    {
        get => _executableNameText;

        set
        {
            if (!SetProperty(ref _executableNameText, value ?? string.Empty))
            {
                return;
            }

            OnSettingsChanged();
        }
    }

    public AudioProfileChoice? SelectedProfile
    {
        get => _selectedProfile;

        set
        {
            if (!SetProperty(ref _selectedProfile, value) || value is null)
            {
                return;
            }

            // Choosing a profile re-seeds the route from it, because every field below belongs to
            // the profile that was chosen rather than to the panel.
            AudioProfileOptions? profile = FindProfile(value.Id);

            if (profile is not null)
            {
                ExecutableNameText = profile.ExecutableName;
                SelectedVirtualRender = Find(VirtualRenderEndpoints, profile.VirtualRenderEndpointId);
                SelectedVirtualCapture = Find(VirtualCaptureEndpoints, profile.VirtualCaptureEndpointId);
                SelectedPhysicalRender = Find(PhysicalRenderEndpoints, profile.PhysicalRenderEndpointId);
            }
        }
    }

    public AudioEndpointViewModel? SelectedVirtualRender
    {
        get => _selectedVirtualRender;
        set => SetProperty(ref _selectedVirtualRender, value);
    }

    public AudioEndpointViewModel? SelectedVirtualCapture
    {
        get => _selectedVirtualCapture;
        set => SetProperty(ref _selectedVirtualCapture, value);
    }

    public AudioEndpointViewModel? SelectedPhysicalRender
    {
        get => _selectedPhysicalRender;
        set => SetProperty(ref _selectedPhysicalRender, value);
    }

    /// <summary>Whether every limiter field, and the executable name, can be stored.</summary>
    public bool AreSettingsValid =>
        _limiterFields.All(field => field.IsValid)
        && AudioRouteValidator.IsUsableExecutableName(_executableNameText);

    /// <summary>The first reason the settings cannot be stored, or <see langword="null"/> when none is.</summary>
    public string? SettingsMessage
    {
        get
        {
            foreach (NumericField field in _limiterFields)
            {
                if (field.Message is { } message)
                {
                    return message;
                }
            }

            return AudioRouteValidator.IsUsableExecutableName(_executableNameText)
                ? null
                : "应用程序名只能是一个文件名，不能带目录，例如 EscapeFromTarkov。";
        }
    }

    /// <summary>What the module is doing, in one line.</summary>
    public string StatusText => Describe(_module.Detail.Module);

    /// <summary>
    /// Whether the routed application is running. Read on demand from the module, because it is what
    /// the exit path asks before it stops a route that is carrying the user's game.
    /// </summary>
    public bool IsTargetRunning => _module.Detail.IsTargetRunning;

    /// <summary>Whether audio is passing through the toolkit right now.</summary>
    public bool IsRouteOpen => _module.Detail.IsRouteOpen;

    public string TargetText => _module.Detail.IsTargetRunning
        ? "目标应用正在运行"
        : "目标应用未运行";

    public string RouteText => _module.Detail.IsRouteOpen
        ? "音频流已打开（共享模式）"
        : "音频流未打开";

    /// <summary>
    /// Why the audio half cannot run at all, or <see langword="null"/> when it can. Read straight
    /// from the module rather than a snapshot, so it is right the first time the panel is shown.
    /// </summary>
    public string? DependencyMessage => _module.Detail.Module.State == ModuleState.Faulted
        ? _module.Detail.Module.Message
        : null;

    /// <summary>Something worth knowing that does not stop the audio.</summary>
    public string? WarningMessage => _module.Detail.WarningMessage;

    public double InputPeakDbFs => _inputPeakDbFs;

    public double OutputPeakDbFs => _outputPeakDbFs;

    public double GainReductionDb => _gainReductionDb;

    public string InputPeakText => FormatDb(_inputPeakDbFs);

    public string OutputPeakText => FormatDb(_outputPeakDbFs);

    public string GainReductionText => _gainReductionDb <= 0.0
        ? "0.0 dB"
        : _gainReductionDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

    /// <summary>The delay the toolkit's own buffers add, as the module measures it.</summary>
    public string LatencyText => _metrics is { } metrics
        ? "本工具增加的延迟约 "
            + metrics.EstimatedAdditionalLatencyMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)
            + " ms（缓冲 "
            + metrics.CaptureBufferMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " / "
            + metrics.RenderBufferMilliseconds.ToString(CultureInfo.InvariantCulture)
            + " ms）"
        : string.Empty;

    public string CountersText => _metrics is { } metrics
        ? "欠载 " + metrics.Underruns.ToString(CultureInfo.InvariantCulture)
            + " · 溢出 " + metrics.Overruns.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>Reads the endpoints the pickers offer, and selects what the active profile names.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshEndpointsAsync(cancellationToken).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>Re-reads the device list. Called when the panel is opened or a device change is seen.</summary>
    public async Task RefreshEndpointsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AudioEndpointDescriptor> endpoints =
            await _catalog.GetEndpointsAsync(cancellationToken).ConfigureAwait(true);

        Replace(
            VirtualRenderEndpoints,
            endpoints.Where(endpoint => endpoint.Flow == AudioDataFlow.Render));

        Replace(
            VirtualCaptureEndpoints,
            endpoints.Where(endpoint => endpoint.Flow == AudioDataFlow.Capture));

        Replace(
            PhysicalRenderEndpoints,
            endpoints.Where(endpoint => endpoint.Flow == AudioDataFlow.Render));
    }

    /// <summary>
    /// Re-reads the module's status. Called from the window's timer, so it must stay cheap: it reads
    /// the module's own state and touches no device.
    /// </summary>
    public void Refresh()
    {
        AudioModuleStatus detail = _module.Detail;

        if (detail.IsRouteOpen)
        {
            // Only while something is open: with no stream, bypass is a preference for the next one,
            // and reporting it off would be reporting the absence of a stream as the absence of the
            // preference.
            SetProperty(
                ref _isBypassed,
                detail.Module.State == ModuleState.Bypass,
                nameof(IsBypassed));
        }

        OnPropertiesChanged(
            nameof(IsEnabled),
            nameof(StatusText),
            nameof(IsTargetRunning),
            nameof(IsRouteOpen),
            nameof(TargetText),
            nameof(RouteText),
            nameof(DependencyMessage),
            nameof(WarningMessage));
    }

    /// <summary>
    /// Publishes what the stream is measuring, at most once per <see cref="MeterInterval"/>.
    /// </summary>
    /// <remarks>
    /// Silence is published as silence: a stream that has closed reports no level at all, and leaving
    /// the last reading on screen would be showing a number from a device that is no longer open.
    /// </remarks>
    public void Sample()
    {
        AudioStreamMetrics? metrics = _module.Metrics;

        if (metrics is null)
        {
            Publish(AudioProcessorMetrics.Silent, null);
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_lastMeterPublish is { } last && now - last < MeterInterval)
        {
            return;
        }

        _lastMeterPublish = now;

        Publish(metrics.Processor, metrics);
    }

    private void Publish(AudioProcessorMetrics processor, AudioStreamMetrics? metrics)
    {
        _metrics = metrics;

        SetProperty(ref _inputPeakDbFs, processor.InputPeakDbFs, nameof(InputPeakDbFs));
        SetProperty(ref _outputPeakDbFs, processor.OutputPeakDbFs, nameof(OutputPeakDbFs));
        SetProperty(ref _gainReductionDb, processor.GainReductionDb, nameof(GainReductionDb));

        OnPropertiesChanged(
            nameof(InputPeakText),
            nameof(OutputPeakText),
            nameof(GainReductionText),
            nameof(LatencyText),
            nameof(CountersText));
    }

    private async Task ToggleEnabledAsync()
    {
        await _coordinator
            .SetAudioEnabledAsync(!_coordinator.IsAudioEnabled, CancellationToken.None)
            .ConfigureAwait(true);

        Refresh();
    }

    private async Task ToggleBypassAsync()
    {
        bool next = !_isBypassed;

        SetProperty(ref _isBypassed, next, nameof(IsBypassed));

        // Switches the open stream rather than closing and reopening it: arriving at the same place
        // by dropping the audio the user is listening to would be a worse way to get there.
        await _module.SetBypassAsync(next, CancellationToken.None).ConfigureAwait(true);

        Refresh();
    }

    private async Task RetryAsync()
    {
        await _module.RetryAsync(CancellationToken.None).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>
    /// Stores the limiter and the route, and reopens the stream when there is one, because the
    /// limiter is built at that moment and not before.
    /// </summary>
    private async Task ApplySettingsAsync()
    {
        if (!TryReadSettings(out AudioLimiterOptions? limiter, out string? executableName))
        {
            // The button is disabled while any field is unusable, so this is a guard rather than a
            // path.
            return;
        }

        bool wasEnabled = _coordinator.IsAudioEnabled;

        await _coordinator
            .UpdateOptionsAsync(
                options => options with { Audio = EditAudio(options.Audio, limiter, executableName) },
                CancellationToken.None)
            .ConfigureAwait(true);

        if (wasEnabled)
        {
            await _coordinator.SetAudioEnabledAsync(false, CancellationToken.None).ConfigureAwait(true);
            await _coordinator.SetAudioEnabledAsync(true, CancellationToken.None).ConfigureAwait(true);
        }

        Refresh();
    }

    private Task OpenVolumeMixerAsync()
    {
        _open(VolumeMixerUri);

        return Task.CompletedTask;
    }

    private bool TryReadSettings(
        [NotNullWhen(true)] out AudioLimiterOptions? limiter,
        [NotNullWhen(true)] out string? executableName)
    {
        limiter = null;
        executableName = null;

        if (SettingsMessage is not null)
        {
            return false;
        }

        if (!_gain.TryRead(out double gain)
            || !_thresholdDbFs.TryRead(out double threshold)
            || !_ceilingDbFs.TryRead(out double ceiling))
        {
            return false;
        }

        // The values the panel does not surface stay as the versioned configuration has them.
        AudioLimiterOptions current = _coordinator.Options.Audio.Limiter;

        limiter = current with
        {
            InputGainDb = gain,
            ThresholdDbFs = threshold,
            CeilingDbFs = ceiling,
        };

        executableName = _executableNameText.Trim();

        return true;
    }

    private AudioOptions EditAudio(AudioOptions audio, AudioLimiterOptions limiter, string executableName)
    {
        AudioProfileOptions? active = FindProfile(audio.ActiveProfileId, audio.Profiles);

        if (active is null)
        {
            return audio with { Limiter = limiter };
        }

        AudioProfileOptions edited = active with
        {
            ExecutableName = executableName,
            VirtualRenderEndpointId = Chosen(_selectedVirtualRender, VirtualRenderEndpoints, active.VirtualRenderEndpointId),
            VirtualCaptureEndpointId = Chosen(_selectedVirtualCapture, VirtualCaptureEndpoints, active.VirtualCaptureEndpointId),
            PhysicalRenderEndpointId = Chosen(_selectedPhysicalRender, PhysicalRenderEndpoints, active.PhysicalRenderEndpointId),
        };

        List<AudioProfileOptions> profiles = [];

        foreach (AudioProfileOptions profile in audio.Profiles)
        {
            profiles.Add(string.Equals(profile.Id, active.Id, StringComparison.Ordinal) ? edited : profile);
        }

        return audio with { Limiter = limiter, Profiles = profiles };
    }

    /// <summary>
    /// The endpoint the user chose, or what was already stored when they were never shown a choice.
    /// </summary>
    /// <remarks>
    /// The pickers are empty until the device list has been read, and an empty picker means "no
    /// opinion" rather than "no device". Storing the absence of a list as the absence of a route
    /// would wipe a working configuration the first time the panel was used before the devices
    /// arrived — and the route would then be refused until the user picked all three again.
    /// </remarks>
    private static string? Chosen(
        AudioEndpointViewModel? selected,
        ObservableCollection<AudioEndpointViewModel> offered,
        string? stored) =>
        selected is null && offered.Count == 0 ? stored : selected?.Id;

    private void SeedProfiles(AudioOptions audio)
    {
        Profiles.Clear();
        AudioProfileChoice? selected = null;

        foreach (AudioProfileOptions profile in audio.Profiles)
        {
            AudioProfileChoice choice = new(profile.Id, profile.DisplayName);
            Profiles.Add(choice);

            if (string.Equals(profile.Id, audio.ActiveProfileId, StringComparison.Ordinal))
            {
                selected = choice;
            }
        }

        if (selected is null && Profiles.Count > 0)
        {
            selected = Profiles[0];
        }

        if (selected is not null)
        {
            _selectedProfile = selected;
            ExecutableNameText = FindProfile(selected.Id)?.ExecutableName ?? string.Empty;
        }
    }

    private AudioProfileOptions? FindProfile(string? profileId)
    {
        if (profileId is null)
        {
            return null;
        }

        return FindProfile(profileId, _coordinator.Options.Audio.Profiles);
    }

    private static AudioProfileOptions? FindProfile(
        string profileId,
        IReadOnlyList<AudioProfileOptions> profiles)
    {
        foreach (AudioProfileOptions profile in profiles)
        {
            if (string.Equals(profile.Id, profileId, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    private static AudioEndpointViewModel? Find(
        IEnumerable<AudioEndpointViewModel> endpoints,
        string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        return endpoints.FirstOrDefault(
            endpoint => string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));
    }

    private static void Replace(
        ObservableCollection<AudioEndpointViewModel> collection,
        IEnumerable<AudioEndpointDescriptor> endpoints)
    {
        collection.Clear();

        foreach (AudioEndpointDescriptor endpoint in endpoints)
        {
            collection.Add(new AudioEndpointViewModel(endpoint));
        }
    }

    private static string Describe(ModuleStatus status) => status.State switch
    {
        ModuleState.Disabled => "已关闭",
        ModuleState.Starting => "正在检查音频路由…",
        ModuleState.Active => "正在处理音频",
        ModuleState.Bypass => "已旁路，音频原样通过",
        ModuleState.Degraded => "已启用，但当前没有需要处理的音频",
        ModuleState.Faulted => "无法使用当前音频路由",
        ModuleState.Stopping => "正在停止…",
        _ => status.State.ToString(),
    };

    private static string FormatDb(double value) => value <= SilentDb
        ? "—"
        : value.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

    private void OnSettingsFieldChanged(object? sender, EventArgs e) => OnSettingsChanged();

    private void OnSettingsChanged()
    {
        OnPropertiesChanged(nameof(AreSettingsValid), nameof(SettingsMessage));
        ApplySettingsCommand.RaiseCanExecuteChanged();
    }
}
