using System.Diagnostics.CodeAnalysis;
using EftToolkit.App.Commands;
using EftToolkit.Core.Configuration;
using EftToolkit.Core.Display;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// One gamma preset — F3, F4, or F5 — as three editable numbers and a button that writes it again.
/// </summary>
/// <remarks>
/// The three values are composed against the ramp captured when the module was switched on, never
/// against what is on the display now, so editing a preset and applying it twice does not compound:
/// the second write is the same picture as the first.
/// </remarks>
public sealed class PresetSettingsViewModel : ObservableObject
{
    private readonly DisplayPresetKind _kind;
    private readonly Func<DisplayPresetKind, DisplayPresetOptions, Task> _apply;
    private readonly NumericField[] _fields;

    public PresetSettingsViewModel(
        DisplayPresetKind kind,
        string label,
        DisplayPresetOptions values,
        Func<DisplayPresetKind, DisplayPresetOptions, Task> apply,
        Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(onError);

        _kind = kind;
        _apply = apply;
        Label = label;

        Gamma = new NumericField("伽马", values.Gamma, OptionsValidator.GammaMinimum, OptionsValidator.GammaMaximum);
        ShadowLift = new NumericField(
            "阴影提亮",
            values.ShadowLift,
            OptionsValidator.ShadowLiftMinimum,
            OptionsValidator.ShadowLiftMaximum);
        OutputCeiling = new NumericField(
            "输出上限",
            values.OutputCeiling,
            OptionsValidator.OutputCeilingMinimum,
            OptionsValidator.OutputCeilingMaximum);

        _fields = [Gamma, ShadowLift, OutputCeiling];

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, onError, () => IsValid);

        // Subscribed after the command exists: the handler tells the button to look at the fields
        // again, and there would be nothing to tell if it ran before the button was built.
        foreach (NumericField field in _fields)
        {
            field.Changed += OnFieldChanged;
        }
    }

    /// <summary>The shortcut and level this preset is reached by, for example <c>F4 中等</c>.</summary>
    public string Label { get; }

    public DisplayPresetKind Kind => _kind;

    public NumericField Gamma { get; }

    public NumericField ShadowLift { get; }

    public NumericField OutputCeiling { get; }

    /// <summary>Writes the values currently in the fields as this preset.</summary>
    public AsyncRelayCommand ApplyCommand { get; }

    public string GammaText
    {
        get => Gamma.Text;
        set => Gamma.Text = value;
    }

    public string ShadowLiftText
    {
        get => ShadowLift.Text;
        set => ShadowLift.Text = value;
    }

    public string OutputCeilingText
    {
        get => OutputCeiling.Text;
        set => OutputCeiling.Text = value;
    }

    /// <summary>Whether every field holds a value the approved range allows.</summary>
    public bool IsValid => _fields.All(field => field.IsValid);

    /// <summary>The first reason a field is unusable, or <see langword="null"/> when none is.</summary>
    public string? ValidationMessage =>
        _fields.Select(field => field.Message).FirstOrDefault(message => message is not null);

    /// <summary>Reads the three fields as one preset. Fails when any of them is unusable.</summary>
    public bool TryReadValues([NotNullWhen(true)] out DisplayPresetOptions? values)
    {
        values = null;

        if (!Gamma.TryRead(out double gamma)
            || !ShadowLift.TryRead(out double shadowLift)
            || !OutputCeiling.TryRead(out double outputCeiling))
        {
            return false;
        }

        values = new DisplayPresetOptions(gamma, shadowLift, outputCeiling);

        return true;
    }

    private async Task ApplyAsync()
    {
        if (!TryReadValues(out DisplayPresetOptions? values))
        {
            // The button is disabled while any field is unusable, so this is a guard rather than a
            // path: a preset composed from half-read fields would be a picture nobody asked for.
            return;
        }

        await _apply(_kind, values).ConfigureAwait(true);
    }

    private void OnFieldChanged(object? sender, EventArgs e)
    {
        OnPropertiesChanged(nameof(IsValid), nameof(ValidationMessage));

        // A field that stopped being valid has to disable the button that would send it, and one
        // that became valid again has to enable it.
        ApplyCommand.RaiseCanExecuteChanged();
    }
}
