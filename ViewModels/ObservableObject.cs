using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PDFEditor.ViewModels;

/// <summary>
/// Base commune a tous les ViewModels : notification de changement de propriete.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Affecte le champ et notifie uniquement si la valeur change reellement
    /// (evite les boucles de notification).
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name ?? string.Empty);
        return true;
    }

    protected void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
