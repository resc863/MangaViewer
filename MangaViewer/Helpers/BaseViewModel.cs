using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MangaViewer.Helpers
{
    /// <summary>
    /// INotifyPropertyChanged base implementation with optimized SetProperty that prevents unnecessary notifications.
    /// </summary>
    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Sets the backing field to new value and raises PropertyChanged only if value actually changed.
        /// Returns true if value changed, false otherwise.
        /// </summary>
        protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value)) 
                return false;
            
            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Raises PropertyChanged event for the specified property name.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
