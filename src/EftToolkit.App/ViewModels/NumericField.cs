using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// One numeric setting, as the user types it, with the approved range attached.
/// </summary>
/// <remarks>
/// <para>
/// The text is the source of truth rather than a number parsed out of it, because the interesting
/// state of a text box is the state it is in while someone is editing it: "1." and "" are both
/// things a person types on the way to a value, and neither has a number behind it yet.
/// </para>
/// <para>
/// A field that does not parse is invalid rather than defaulted. A decimal comma read as a
/// thousands separator would set a gamma the user never asked for — <c>0,05</c> becoming 5, which is
/// a dark screen rather than a shadow lift — so the parse is deliberately culture-invariant and the
/// value is refused.
/// </para>
/// <para>
/// The range is the one <see cref="OptionsValidator"/> enforces on the stored configuration. It is
/// checked here as well so the user is told at the field they are typing in, rather than by a value
/// that quietly changes back after a restart.
/// </para>
/// </remarks>
public sealed class NumericField : ObservableObject
{
    private readonly double _minimum;
    private readonly double _maximum;

    private string _text;

    /// <param name="name">What the field is called in the message shown when it is not usable.</param>
    public NumericField(string name, double value, double minimum, double maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _minimum = minimum;
        _maximum = maximum;
        _text = FormatInvariant(value);
    }

    /// <summary>Raised when the text changes, so whoever owns the field can re-ask what it may do.</summary>
    public event EventHandler? Changed;

    public string Name { get; }

    public double Minimum => _minimum;

    public double Maximum => _maximum;

    public string Text
    {
        get => _text;

        set
        {
            if (!SetProperty(ref _text, value ?? string.Empty))
            {
                return;
            }

            OnPropertiesChanged(nameof(IsValid), nameof(Message));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsValid => TryRead(out _);

    /// <summary>Why the field cannot be used, or <see langword="null"/> when it can.</summary>
    public string? Message
    {
        get
        {
            if (!TryParse(_text, out double value))
            {
                return Name + "：请输入数字。";
            }

            if (value < _minimum || value > _maximum)
            {
                return Name + " 必须在 " + FormatInvariant(_minimum) + " 与 " + FormatInvariant(_maximum) + " 之间。";
            }

            return null;
        }
    }

    /// <summary>The value, if the text currently in the field is one the approved range allows.</summary>
    public bool TryRead(out double value) =>
        TryParse(_text, out value) && value >= _minimum && value <= _maximum;

    private static bool TryParse(string? text, out double value)
    {
        value = 0.0;

        return !string.IsNullOrWhiteSpace(text)
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatInvariant(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
