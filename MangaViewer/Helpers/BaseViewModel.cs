using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MangaViewer.Helpers
{
    /// <summary>
    /// INotifyPropertyChanged 기본 구현. SetProperty 를 통해 변경 시에만 이벤트 발생.
    /// </summary>
    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 백킹 필드에 새 값을 넣고 달라졌을 때만 PropertyChanged 발생.
        /// </summary>
        protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value)) return false;
            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
