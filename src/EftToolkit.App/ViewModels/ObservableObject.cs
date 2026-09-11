using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EftToolkit.App.ViewModels;

/// <summary>
/// The minimum a bindable object needs: a change notification, and a setter that raises it only when
/// the value actually changed.
/// </summary>
/// <remarks>
/// A base class rather than a framework dependency. The panel is bound to a handful of properties
/// whose notifications are the interesting part of it, and every one of them is raised deliberately
/// — the meters raise theirs on a timer, and a handler that fired for an unchanged value would make
/// the meter look like it was moving when it was not.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);

        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Announces several derived properties at once. A view model that computes a status on every
    /// read still has to say when that read would return something different, and a refresh usually
    /// makes several of them different at the same time.
    /// </summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }
}
