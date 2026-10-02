using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DRRipper.UI
{
    /// <summary>
    /// INotifyPropertyChanged base for all view-models (Ticket #006).
    /// WPF-assembly-free: no Dispatcher, no CommandManager, no visual types,
    /// so view-models are fully unit-testable headless.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
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
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } catch { }
        }
    }
}
