using EftToolkit.App.Commands;
using EftToolkit.Core.Display;
using EftToolkit.Core.Lifecycle;
using EftToolkit.Display;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// One monitor in the panel's list: what it is, whether it is part of the selection, and what the
/// last write to it did.
/// </summary>
/// <remarks>
/// A row does not decide the selection for itself. It asks the coordinator to adopt a new selection
/// and then re-reads what came back, so the panel cannot show a tick the stored configuration
/// disagrees with — the tick means "the toolkit is enhancing this monitor", and only the toolkit can
/// say that.
/// </remarks>
public sealed class DisplayRowViewModel : ObservableObject
{
    private readonly ToolkitCoordinator _coordinator;
    private readonly Func<Task> _afterToggle;

    private DisplayStatus _row;

    internal DisplayRowViewModel(
        DisplayStatus row,
        ToolkitCoordinator coordinator,
        Action<Exception> onError,
        Func<Task> afterToggle)
    {
        _row = row;
        _coordinator = coordinator;
        _afterToggle = afterToggle;

        ToggleSelectionCommand = new AsyncRelayCommand(ToggleSelectionAsync, onError);
    }

    /// <summary>The identifier the selection is stored by. It survives the monitor being unplugged.</summary>
    public string StableId => _row.Display.StableId;

    /// <summary>What Windows calls the monitor, falling back to the identifier when it has no name.</summary>
    public string Name => string.IsNullOrWhiteSpace(_row.Display.FriendlyName)
        ? StableId
        : _row.Display.FriendlyName;

    public bool IsSelected => _row.Selected;

    public bool IsConnected => _row.Display.IsConnected;

    /// <summary>
    /// Whether the display is running in high dynamic range. Gamma ramps can still be written, but
    /// the toolkit does not claim the result is what the user will see.
    /// </summary>
    public bool IsHdr => _row.Display.IsHdr;

    public bool SupportsGammaRamp => _row.Display.SupportsGammaRamp;

    /// <summary>What the module said about this monitor, or an empty string when it said nothing.</summary>
    public string Message => _row.Message ?? string.Empty;

    /// <summary>A one-word state for the row.</summary>
    public string StateText
    {
        get
        {
            if (!IsConnected)
            {
                return "未连接";
            }

            if (!SupportsGammaRamp)
            {
                return "不支持伽马校正";
            }

            return IsSelected ? "已选中" : "未选中";
        }
    }

    /// <summary>Adds this monitor to the selection, or takes it out.</summary>
    public AsyncRelayCommand ToggleSelectionCommand { get; }

    /// <summary>Adopts what the module now says about this monitor.</summary>
    internal void Update(DisplayStatus row)
    {
        _row = row;

        OnPropertiesChanged(
            nameof(IsSelected),
            nameof(IsConnected),
            nameof(IsHdr),
            nameof(SupportsGammaRamp),
            nameof(Message),
            nameof(StateText));
    }

    private async Task ToggleSelectionAsync()
    {
        List<string> selection = [.. _coordinator.Options.Display.SelectedDisplayIds];

        int existing = selection.FindIndex(
            id => string.Equals(id, StableId, StringComparison.Ordinal));

        if (existing >= 0)
        {
            selection.RemoveAt(existing);
        }
        else
        {
            // Appended rather than sorted: the order is the order the user built the selection in,
            // and it is what the next launch reads back.
            selection.Add(StableId);
        }

        await _coordinator
            .UpdateOptionsAsync(
                options => options with { Display = options.Display with { SelectedDisplayIds = selection } },
                CancellationToken.None)
            .ConfigureAwait(true);

        await _afterToggle().ConfigureAwait(true);
    }
}

/// <summary>The shortcut and level names for the four presets.</summary>
public static class PresetNames
{
    /// <summary>For example <c>F4 中等</c>.</summary>
    public static string For(DisplayPresetKind preset) => preset switch
    {
        DisplayPresetKind.Original => "F2 原始",
        DisplayPresetKind.Low => "F3 低",
        DisplayPresetKind.Medium => "F4 中等",
        DisplayPresetKind.High => "F5 高",
        _ => preset.ToString(),
    };
}
