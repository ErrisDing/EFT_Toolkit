using System.Collections.ObjectModel;
using EftToolkit.App.Commands;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Core.Modules;
using EftToolkit.Display;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// The display half of the panel: the switch, the monitor list, and the three editable presets.
/// </summary>
/// <remarks>
/// <para>
/// The switch is the module's, not the view model's. Everything shown here is read back from the
/// coordinator and the module on every refresh, so a transition that failed — or one that was
/// refused because the toolkit is shutting down — leaves the panel showing what is actually the
/// case rather than what was clicked.
/// </para>
/// <para>
/// The monitor list is readable while the module is switched off, which is the state a new user is
/// in: they have to be able to see their monitors before selecting one is possible.
/// </para>
/// </remarks>
public sealed class DisplayViewModel : ObservableObject
{
    private readonly DisplayModule _module;
    private readonly ToolkitCoordinator _coordinator;
    private readonly Action<Exception> _onError;

    public DisplayViewModel(DisplayModule module, ToolkitCoordinator coordinator, Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(onError);

        _module = module;
        _coordinator = coordinator;
        _onError = onError;

        DisplayOptions options = coordinator.Options.Display;

        Low = Create(DisplayPresetKind.Low, "F3 低", options.Low, onError);
        Medium = Create(DisplayPresetKind.Medium, "F4 中等", options.Medium, onError);
        High = Create(DisplayPresetKind.High, "F5 高", options.High, onError);

        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync, onError);
    }

    /// <summary>The warning shown wherever a selected monitor runs in HDR.</summary>
    public const string HdrNote = "该显示器处于 HDR：伽马校正仍然会写入，但 HDR 下的效果属于实验性，请以实际观感为准。";

    /// <summary>How many monitors the toolkit can drive.</summary>
    public ObservableCollection<DisplayRowViewModel> Monitors { get; } = [];

    public AsyncRelayCommand ToggleEnabledCommand { get; }

    public PresetSettingsViewModel Low { get; }

    public PresetSettingsViewModel Medium { get; }

    public PresetSettingsViewModel High { get; }

    public bool IsEnabled => _coordinator.IsDisplayEnabled;

    /// <summary>The preset the module is converging on.</summary>
    public DisplayPresetKind CurrentPreset => _module.CurrentPreset;

    public string CurrentPresetText => PresetNames.For(CurrentPreset) + " 生效中";

    public string StatusText => Describe(_module.Status);

    /// <summary>Whether a selected monitor is in HDR, which is what makes <see cref="HdrNote"/> apply.</summary>
    public bool IsHdrPresent => Monitors.Any(row => row.IsSelected && row.IsHdr && row.IsConnected);

    public string ValidationMessage => Low.ValidationMessage
        ?? Medium.ValidationMessage
        ?? High.ValidationMessage
        ?? string.Empty;

    /// <summary>Reads the monitors once, so the list is there before the module is switched on.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _module.EnumerateAsync(cancellationToken).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>
    /// Re-reads everything the panel shows. Called from the window's timer, so it must stay cheap:
    /// no enumeration, and nothing that touches a device.
    /// </summary>
    public void Refresh()
    {
        UpdateMonitors(_module.Displays);

        OnPropertiesChanged(
            nameof(IsEnabled),
            nameof(CurrentPreset),
            nameof(CurrentPresetText),
            nameof(StatusText),
            nameof(IsHdrPresent),
            nameof(ValidationMessage));
    }

    private static string Describe(ModuleStatus status) => status.State switch
    {
        ModuleState.Disabled => "已关闭",
        ModuleState.Starting => "正在读取显示器…",
        ModuleState.Active => "已启用",
        ModuleState.Bypass => "已启用，但没有可用的显示器",
        ModuleState.Degraded => "已启用，但画面未被修改",
        ModuleState.Faulted => "出错了",
        ModuleState.Stopping => "正在还原…",
        _ => status.State.ToString(),
    };

    private PresetSettingsViewModel Create(
        DisplayPresetKind kind,
        string label,
        DisplayPresetOptions values,
        Action<Exception> onError) =>
        new(kind, label, values, ApplyPresetValuesAsync, onError);

    private async Task ToggleEnabledAsync()
    {
        await _coordinator
            .SetDisplayEnabledAsync(!_coordinator.IsDisplayEnabled, CancellationToken.None)
            .ConfigureAwait(true);

        Refresh();
    }

    /// <summary>
    /// Stores the values a preset is composed from and rewrites it if it is the one in force. The
    /// coordinator decides what that means; the panel only reports it.
    /// </summary>
    private async Task ApplyPresetValuesAsync(DisplayPresetKind kind, DisplayPresetOptions values)
    {
        await _coordinator
            .UpdateOptionsAsync(
                options => options with { Display = WithPreset(options.Display, kind, values) },
                CancellationToken.None)
            .ConfigureAwait(true);

        Refresh();
    }

    private static DisplayOptions WithPreset(
        DisplayOptions display,
        DisplayPresetKind kind,
        DisplayPresetOptions values) => kind switch
    {
        DisplayPresetKind.Low => display with { Low = values },
        DisplayPresetKind.Medium => display with { Medium = values },
        DisplayPresetKind.High => display with { High = values },
        _ => display,
    };

    /// <summary>Re-reads the monitors and folds them into the rows the panel is already showing.</summary>
    private async Task AfterSelectionChangedAsync()
    {
        await _module.EnumerateAsync(CancellationToken.None).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>
    /// Updates the rows in place. Rebuilding the collection would replace the row a user is about to
    /// click, and a tick that is being set would be thrown away with it.
    /// </summary>
    private void UpdateMonitors(IReadOnlyList<DisplayStatus> rows)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int index = 0; index < rows.Count; index++)
        {
            DisplayStatus status = rows[index];
            seen.Add(status.Display.StableId);

            DisplayRowViewModel? existing = Monitors.FirstOrDefault(
                row => string.Equals(row.StableId, status.Display.StableId, StringComparison.Ordinal));

            if (existing is null)
            {
                Monitors.Insert(
                    Math.Min(index, Monitors.Count),
                    new DisplayRowViewModel(status, _coordinator, _onError, AfterSelectionChangedAsync));

                continue;
            }

            existing.Update(status);
        }

        for (int index = Monitors.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(Monitors[index].StableId))
            {
                Monitors.RemoveAt(index);
            }
        }
    }
}
