using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DackelFxp.Core.Infrastructure;

/// <summary>INotifyPropertyChanged whose events are raised via <see cref="CoreDispatcher"/>.</summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        CoreDispatcher.Post(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}
