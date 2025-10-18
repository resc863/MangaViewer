# Helpers

Components
- `BaseViewModel`: implements `INotifyPropertyChanged`, provides `SetProperty` and `OnPropertyChanged`.
- `RelayCommand` / `AsyncRelayCommand`: synchronous/asynchronous commands with `RaiseCanExecuteChanged`.

Tips
- For long-running work, prefer `AsyncRelayCommand` with cancellation tokens.
- Encapsulate UI enable/disable logic with computed properties like `IsControlEnabled`, and raise `OnPropertyChanged` on related state changes.
