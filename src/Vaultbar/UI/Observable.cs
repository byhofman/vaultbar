using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vaultbar.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var other in also) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(other));
    }
}
