using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MangaViewer.Helpers
{
    /// <summary>
    /// 비동기 실행 지원 ICommand. 중복 실행 방지(_isExecuting) 및 CanExecute 갱신.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _executeAsync(parameter);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
